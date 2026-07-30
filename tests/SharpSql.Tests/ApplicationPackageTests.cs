using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace SharpSql.Tests;

public sealed class ApplicationPackageTests
{
    private const string CompiledProgram = "PRINT N'package ran';";

    [Fact]
    public void InstallerIsDeterministicAndUsesIdempotentDatabaseOperations()
    {
        var package = Package();

        var first = package.GenerateInstallSql();
        var second = package.GenerateInstallSql();

        Assert.Equal(first, second);
        Assert.Contains("sys.sp_getapplock", first, StringComparison.Ordinal);
        Assert.Contains("IF SCHEMA_ID(N'TenantJobs') IS NULL", first, StringComparison.Ordinal);
        Assert.Contains("IF OBJECT_ID(N'[TenantJobs].[PackageManifest]', N'U') IS NULL", first, StringComparison.Ordinal);
        Assert.Contains("CREATE OR ALTER PROCEDURE [TenantJobs].[Run]", first, StringComparison.Ordinal);
        Assert.Contains("UPDATE [TenantJobs].[PackageManifest]", first, StringComparison.Ordinal);
        Assert.Contains("IF @@ROWCOUNT = 0", first, StringComparison.Ordinal);
        Assert.Contains("BEGIN TRANSACTION", first, StringComparison.Ordinal);
        Assert.Contains("COMMIT TRANSACTION", first, StringComparison.Ordinal);
        Assert.Contains("already owned by a different SharpSql package", first, StringComparison.Ordinal);
        Assert.Contains("@__sharpsql_previous_entry", first, StringComparison.Ordinal);
    }

    [Fact]
    public void UninstallerIsDeterministicAndRemovesOnlyPackageOwnedObjects()
    {
        var package = Package();

        var first = package.GenerateUninstallSql();
        var second = package.GenerateUninstallSql();

        Assert.Equal(first, second);
        Assert.Contains("sys.sp_getapplock", first, StringComparison.Ordinal);
        Assert.Contains("N'InventoryRebuild'", first, StringComparison.Ordinal);
        Assert.Contains("DROP PROCEDURE [TenantJobs]", first, StringComparison.Ordinal);
        Assert.Contains("NativeKernel[_]%", first, StringComparison.Ordinal);
        Assert.Contains("DROP TABLE [TenantJobs].[NativeKernelCatalog]", first, StringComparison.Ordinal);
        Assert.Contains("DROP TABLE [TenantJobs].[PackageManifest]", first, StringComparison.Ordinal);
        Assert.Contains("DROP TYPE [TenantJobs].[MemoryVmSlotsV1]", first, StringComparison.Ordinal);
        Assert.Contains("DROP TYPE [TenantJobs].[MemoryVmStackV1]", first, StringComparison.Ordinal);
        Assert.DoesNotContain("DROP SCHEMA", first, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UninstallerQuotesSchemaAndEscapesApplicationIdentity()
    {
        var sql = new SharpSqlApplicationPackage(
            "Tenant]Jobs",
            "O'Brien's job",
            "ignored",
            CompiledProgram).GenerateUninstallSql();

        Assert.Contains("DROP TABLE [Tenant]]Jobs].[PackageManifest]", sql, StringComparison.Ordinal);
        Assert.Contains("N'O''Brien''s job'", sql, StringComparison.Ordinal);
        Assert.Contains("DROP PROCEDURE [Tenant]]Jobs]", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void InstallerQuotesIdentifiersAndEscapesManifestLiterals()
    {
        var sql = new SharpSqlApplicationPackage(
            "Tenant]Jobs",
            "O'Brien's job",
            "2.0'preview",
            CompiledProgram)
        {
            EntryProcedureName = "Run]Now"
        }.GenerateInstallSql();

        Assert.Contains("[Tenant]]Jobs].[PackageManifest]", sql, StringComparison.Ordinal);
        Assert.Contains("CREATE OR ALTER PROCEDURE [Tenant]]Jobs].[Run]]Now]", sql, StringComparison.Ordinal);
        Assert.Contains("N'O''Brien''s job'", sql, StringComparison.Ordinal);
        Assert.Contains("N'2.0''preview'", sql, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(UnsafeIdentifiers))]
    public void InstallerRejectsUnsafeIdentifiers(string identifier)
    {
        var package = new SharpSqlApplicationPackage(
            identifier,
            "Inventory",
            "1.0.0",
            CompiledProgram);

        Assert.ThrowsAny<ArgumentException>(() => package.GenerateInstallSql());
    }

    public static IEnumerable<object[]> UnsafeIdentifiers()
    {
        yield return [""];
        yield return ["   "];
        yield return [" leading"];
        yield return ["trailing "];
        yield return ["line\nbreak"];
        yield return [new string('x', 129)];
    }

    [Fact]
    public void InstallerRejectsUnpairedSurrogateInIdentifier()
    {
        var package = new SharpSqlApplicationPackage(
            "unpaired" + new string('\ud800', 1) + "surrogate",
            "Inventory",
            "1.0.0",
            CompiledProgram);

        Assert.ThrowsAny<ArgumentException>(() => package.GenerateInstallSql());
    }

    [Fact]
    public void ManifestRecordsPackageIdentityVersionAndExecutionShape()
    {
        var sql = Package() with
        {
            EntryProcedureName = "Execute",
            Version = "2026.07.30"
        };

        var installer = sql.GenerateInstallSql();

        Assert.Contains("[ApplicationName] NVARCHAR(128) NOT NULL", installer, StringComparison.Ordinal);
        Assert.Contains("[PackageVersion] NVARCHAR(128) NOT NULL", installer, StringComparison.Ordinal);
        Assert.Contains("[EntryProcedureName] NVARCHAR(128) NOT NULL", installer, StringComparison.Ordinal);
        Assert.Contains("[RuntimeStorage] NVARCHAR(32) NOT NULL", installer, StringComparison.Ordinal);
        Assert.Contains("[NativeKernelsEnabled] BIT NOT NULL", installer, StringComparison.Ordinal);
        Assert.Contains("[ProgramHash] CHAR(64) NOT NULL", installer, StringComparison.Ordinal);
        Assert.Contains("N'InventoryRebuild'", installer, StringComparison.Ordinal);
        Assert.Contains("N'2026.07.30'", installer, StringComparison.Ordinal);
        Assert.Contains("N'Execute'", installer, StringComparison.Ordinal);
        Assert.Contains("N'Ephemeral'", installer, StringComparison.Ordinal);
        var expectedHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(CompiledProgram)))
            .ToLowerInvariant();
        Assert.Contains($"N'{expectedHash}'", installer, StringComparison.Ordinal);
    }

    [Fact]
    public void MemoryOptimizedPackageScopesTypesAndChecksPhysicalPrerequisite()
    {
        var installer = (Package() with
        {
            RuntimeStorage = RuntimeStorageKind.MemoryOptimized
        }).GenerateInstallSql();

        Assert.Contains("MEMORY_OPTIMIZED_DATA filegroup", installer, StringComparison.Ordinal);
        Assert.Contains("CREATE TYPE [TenantJobs].[MemoryVmStackV1] AS TABLE", installer, StringComparison.Ordinal);
        Assert.Contains("CREATE TYPE [TenantJobs].[MemoryVmSlotsV1] AS TABLE", installer, StringComparison.Ordinal);
        Assert.Contains("WITH (MEMORY_OPTIMIZED = ON)", installer, StringComparison.Ordinal);
        Assert.Contains("N'MemoryOptimized'", installer, StringComparison.Ordinal);
        Assert.DoesNotContain("ADD FILEGROUP", installer, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ADD FILE", installer, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NativeKernelsRequireMemoryOptimizedPackageStorage()
    {
        var package = Package() with { EnableNativeKernels = true };

        var exception = Assert.Throws<ArgumentException>(() => package.GenerateInstallSql());

        Assert.Equal(nameof(SharpSqlApplicationPackage.EnableNativeKernels), exception.ParamName);
        Assert.Contains("memory-optimized", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CompilerScopesMemoryTypesAndNativeKernelsToApplicationSchema()
    {
        const string source = """
            long Sum(int count)
            {
                long result = 0;
                int index = 0;
                while (index < count)
                {
                    result += index;
                    index++;
                }
                return result;
            }

            long value = Sum(10);
            Console.WriteLine(value);
            """;
        var result = new SharpSqlCompiler().Transpile(
            source,
            new TranspileOptions
            {
                RuntimeStorage = RuntimeStorageKind.MemoryOptimized,
                EnableNativeKernels = true,
                ApplicationSchema = "Tenant]Jobs"
            });

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains("[Tenant]]Jobs].[NativeKernel_", result.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("[SharpSql].[NativeKernel_", result.Sql, StringComparison.Ordinal);
    }

    private static SharpSqlApplicationPackage Package() => new(
        "TenantJobs",
        "InventoryRebuild",
        "1.0.0",
        CompiledProgram);
}
