using Xunit;

namespace SharpSql.Tests;

public sealed class NativeKernelCompilerTests
{
    private const string SupportedSource = """
        long Accumulate(int iterations, long seed)
        {
            long result = seed;
            int index = 0;
            while (index < iterations)
            {
                result = (result + index) % 2147483647;
                index++;
            }
            return result;
        }

        long value = Accumulate(100, 7);
        Console.WriteLine(value);
        """;

    [Fact]
    public void ExtractsSupportedPureMethodWhenEnabled()
    {
        var result = new SharpSqlCompiler().Transpile(
            SupportedSource,
            new TranspileOptions
            {
                RuntimeStorage = RuntimeStorageKind.MemoryOptimized,
                EnableNativeKernels = true
            });

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains("CREATE PROCEDURE [SharpSql].[NativeKernel_", result.Sql, StringComparison.Ordinal);
        Assert.Contains("WITH NATIVE_COMPILATION, SCHEMABINDING", result.Sql, StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE [SharpSql].[NativeKernelCatalog]", result.Sql, StringComparison.Ordinal);
        Assert.Contains("SharpSql.NativeKernel.SharpSql.NativeKernel_", result.Sql, StringComparison.Ordinal);
        Assert.Contains("@LockMode = N'Shared'", result.Sql, StringComparison.Ordinal);
        Assert.Contains("@__result =", result.Sql, StringComparison.Ordinal);
        Assert.Contains("OUTPUT", result.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void DisabledOptimizationPreservesLegacySql()
    {
        var implicitDisabled = new SharpSqlCompiler().Transpile(
            SupportedSource,
            new TranspileOptions { RuntimeStorage = RuntimeStorageKind.MemoryOptimized });
        var explicitDisabled = new SharpSqlCompiler().Transpile(
            SupportedSource,
            new TranspileOptions
            {
                RuntimeStorage = RuntimeStorageKind.MemoryOptimized,
                EnableNativeKernels = false
            });

        Assert.True(implicitDisabled.Success);
        Assert.Equal(implicitDisabled.Sql, explicitDisabled.Sql);
        Assert.DoesNotContain("NATIVE_COMPILATION", implicitDisabled.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void UnsupportedMethodFallsBackToLegacyInlining()
    {
        const string source = """
            string Repeat(string value)
            {
                string result = value;
                result += value;
                return result;
            }

            string output = Repeat("x");
            Console.WriteLine(output);
            """;
        var result = new SharpSqlCompiler().Transpile(
            source,
            new TranspileOptions
            {
                RuntimeStorage = RuntimeStorageKind.MemoryOptimized,
                EnableNativeKernels = true
            });

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.DoesNotContain("NATIVE_COMPILATION", result.Sql, StringComparison.Ordinal);
        Assert.Contains("DECLARE @_repeat_", result.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void NativeKernelsRequireMemoryOptimizedRuntime()
    {
        var result = new SharpSqlCompiler().Transpile(
            SupportedSource,
            new TranspileOptions { EnableNativeKernels = true });

        Assert.False(result.Success);
        var diagnostic = Assert.Single(result.Diagnostics, item => item.Code == "SS8201");
        Assert.Contains(nameof(RuntimeStorageKind.MemoryOptimized), diagnostic.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("NATIVE_COMPILATION", result.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void GeneratesValidatedStatusAndRetentionSql()
    {
        var status = SharpSqlNativeKernelRuntime.GenerateStatusSql("TenantJobs");
        var preview = SharpSqlNativeKernelRuntime.GenerateCleanupSql(
            "TenantJobs",
            TimeSpan.FromDays(7),
            batchSize: 12,
            dryRun: true);
        var cleanup = SharpSqlNativeKernelRuntime.GenerateCleanupSql(
            "TenantJobs",
            TimeSpan.FromHours(1));

        Assert.Contains("[TenantJobs].[NativeKernelCatalog]", status, StringComparison.Ordinal);
        Assert.Contains("TOP (12)", preview, StringComparison.Ordinal);
        Assert.DoesNotContain("DROP PROCEDURE", preview, StringComparison.Ordinal);
        Assert.Contains("@LockMode = N'Exclusive'", cleanup, StringComparison.Ordinal);
        Assert.Contains("DROP PROCEDURE [TenantJobs].", cleanup, StringComparison.Ordinal);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SharpSqlNativeKernelRuntime.GenerateCleanupSql("TenantJobs", TimeSpan.FromSeconds(30)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SharpSqlNativeKernelRuntime.GenerateCleanupSql("TenantJobs", TimeSpan.FromHours(1), batchSize: 101));
    }
}
