using Xunit;

namespace SharpSql.Tests;

public sealed class MemoryOptimizedRuntimeTests
{
    private const string RecursiveSource = """
        int Fibonacci(int value)
        {
            if (value < 2)
                return value;
            return Fibonacci(value - 1) + Fibonacci(value - 2);
        }

        Console.WriteLine(Fibonacci(10));
        """;

    [Fact]
    public void ExistingRuntimeStorageValuesRemainStable()
    {
        Assert.Equal(0, (int)RuntimeStorageKind.Ephemeral);
        Assert.Equal(1, (int)RuntimeStorageKind.Durable);
        Assert.Equal(2, (int)RuntimeStorageKind.ServiceBroker);
        Assert.Equal(3, (int)RuntimeStorageKind.MemoryOptimized);
    }

    [Fact]
    public void ProvisioningCreatesVersionedMemoryOptimizedTableTypes()
    {
        var sql = SharpSqlMemoryOptimizedRuntime.GenerateProvisioningSql();

        Assert.Contains("MEMORY_OPTIMIZED_DATA filegroup", sql, StringComparison.Ordinal);
        Assert.Contains("CREATE TYPE [SharpSql].[MemoryVmStackV1] AS TABLE", sql, StringComparison.Ordinal);
        Assert.Contains("CREATE TYPE [SharpSql].[MemoryVmSlotsV1] AS TABLE", sql, StringComparison.Ordinal);
        Assert.Equal(2, Count(sql, "WITH (MEMORY_OPTIMIZED = ON)"));
        Assert.Contains("[__scalar_value] VARBINARY(8000) NULL", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("SQL_VARIANT", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ADD FILEGROUP", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RecursiveLegacyVmUsesMemoryOptimizedTableVariables()
    {
        var result = new SharpSqlCompiler().Transpile(
            RecursiveSource,
            new TranspileOptions { RuntimeStorage = RuntimeStorageKind.MemoryOptimized });

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains("DECLARE @__sharpsql_memory_stack [SharpSql].[MemoryVmStackV1]", result.Sql, StringComparison.Ordinal);
        Assert.Contains("DECLARE @__sharpsql_memory_slots [SharpSql].[MemoryVmSlotsV1]", result.Sql, StringComparison.Ordinal);
        Assert.Contains("__scalar_value", result.Sql, StringComparison.Ordinal);
        Assert.Contains("CONVERT(VARBINARY(8000)", result.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("CREATE TABLE #__sharpsql_stack", result.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("MERGE @__sharpsql_memory_slots", result.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void MemoryOptimizedModeLeavesManagedHeapTemporaryForNow()
    {
        const string source = """
            var values = new List<int>();
            values.Add(1);
            Console.WriteLine(values[0]);
            """;

        var result = new SharpSqlCompiler().Transpile(
            source,
            new TranspileOptions { RuntimeStorage = RuntimeStorageKind.MemoryOptimized });

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains("CREATE TABLE #__sharpsql_objects", result.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("SharpSql durable managed heap", result.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void EphemeralRemainsTheDefault()
    {
        var result = new SharpSqlCompiler().Transpile(RecursiveSource);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains("CREATE TABLE #__sharpsql_stack", result.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("MemoryVmStackV1", result.Sql, StringComparison.Ordinal);
    }

    private static int Count(string text, string value)
    {
        var count = 0;
        var offset = 0;
        while ((offset = text.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += value.Length;
        }
        return count;
    }
}
