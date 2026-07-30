using System.Security.Cryptography;
using System.Text;

namespace SharpSql;

/// <summary>
/// Describes a compiled SharpSql application that can be installed into its own database schema.
/// </summary>
public sealed record SharpSqlApplicationPackage
{
    /// <summary>Creates a database package for an already compiled SharpSql program.</summary>
    public SharpSqlApplicationPackage(
        string schemaName,
        string applicationName,
        string version,
        string compiledProgramSql)
    {
        SchemaName = schemaName;
        ApplicationName = applicationName;
        Version = version;
        CompiledProgramSql = compiledProgramSql;
    }

    /// <summary>Gets the schema that owns all objects for this application.</summary>
    public string SchemaName { get; init; }

    /// <summary>Gets the stable application name recorded in the package manifest.</summary>
    public string ApplicationName { get; init; }

    /// <summary>Gets the application version recorded in the package manifest.</summary>
    public string Version { get; init; }

    /// <summary>Gets the executable SQL produced by <see cref="SharpSqlCompiler"/>.</summary>
    public string CompiledProgramSql { get; init; }

    /// <summary>Gets the schema procedure that serves as the installed application entry point.</summary>
    public string EntryProcedureName { get; init; } = "Run";

    /// <summary>Gets the runtime storage required by the compiled program.</summary>
    public RuntimeStorageKind RuntimeStorage { get; init; } = RuntimeStorageKind.Ephemeral;

    /// <summary>Gets whether the compiled application contains native memory-optimized kernels.</summary>
    public bool EnableNativeKernels { get; init; }

    /// <summary>
    /// Generates a concurrency-safe, idempotent SQL Server installation script for this package.
    /// </summary>
    public string GenerateInstallSql() => ApplicationPackageSqlEmitter.Emit(this);

    /// <summary>
    /// Generates a concurrency-safe SQL Server script that removes objects owned by this package.
    /// The application schema itself is retained so unrelated, operator-managed objects are never removed.
    /// </summary>
    public string GenerateUninstallSql() => GenerateUninstallSql(SchemaName, ApplicationName);

    /// <summary>Generates an uninstall script for an installed package without requiring its original compiled SQL.</summary>
    public static string GenerateUninstallSql(string schemaName, string applicationName) =>
        ApplicationPackageSqlEmitter.EmitUninstall(schemaName, applicationName);
}

internal static class ApplicationPackageSqlEmitter
{
    private const int ProvisioningLockErrorNumber = 51940;
    private const int PackageOwnershipErrorNumber = 51942;
    private const int PackageNotInstalledErrorNumber = 51943;
    private const string ManifestName = "PackageManifest";

    internal static string Emit(SharpSqlApplicationPackage package)
    {
        if (package is null)
            throw new ArgumentNullException(nameof(package));
        var schemaName = SqlIdentifier.Validate(package.SchemaName, nameof(package.SchemaName));
        var applicationName = RequiredManifestValue(package.ApplicationName, nameof(package.ApplicationName));
        var version = RequiredManifestValue(package.Version, nameof(package.Version));
        var entryName = SqlIdentifier.Validate(package.EntryProcedureName, nameof(package.EntryProcedureName));
        if (string.IsNullOrWhiteSpace(package.CompiledProgramSql))
            throw new ArgumentException("Compiled program SQL cannot be empty or whitespace.", nameof(package.CompiledProgramSql));
        if (!Enum.IsDefined(typeof(RuntimeStorageKind), package.RuntimeStorage))
            throw new ArgumentOutOfRangeException(nameof(package.RuntimeStorage));
        if (package.EnableNativeKernels && package.RuntimeStorage != RuntimeStorageKind.MemoryOptimized)
            throw new ArgumentException("Native kernels require memory-optimized runtime storage.", nameof(package.EnableNativeKernels));

        var schema = SqlIdentifier.Quote(schemaName, nameof(package.SchemaName));
        var schemaLiteral = SqlIdentifier.UnicodeLiteral(schemaName);
        var manifest = $"{schema}.{SqlIdentifier.Quote(ManifestName, ManifestName)}";
        var manifestLiteral = SqlIdentifier.UnicodeLiteral(manifest);
        var entryProcedure = $"{schema}.{SqlIdentifier.Quote(entryName, nameof(package.EntryProcedureName))}";
        var entryProcedureLiteral = SqlIdentifier.UnicodeLiteral(entryProcedure);
        var lockResource = SqlIdentifier.UnicodeLiteral($"SharpSql.Package.{schemaName}");
        var programHash = ComputeProgramHash(package.CompiledProgramSql);

        var sql = new SqlWriter();
        sql.Line("SET ANSI_NULLS ON;");
        sql.Line("SET QUOTED_IDENTIFIER ON;");
        sql.Line("SET XACT_ABORT ON;");
        sql.Line("SET NOCOUNT ON;");
        sql.Line("DECLARE @__sharpsql_package_lock_result INT;");
        sql.Line("BEGIN TRY");
        using (sql.Indent())
        {
            sql.Line("EXEC @__sharpsql_package_lock_result = sys.sp_getapplock");
            using (sql.Indent())
            {
                sql.Line($"@Resource = {lockResource},");
                sql.Line("@LockMode = N'Exclusive',");
                sql.Line("@LockOwner = N'Session',");
                sql.Line("@LockTimeout = 60000;");
            }
            sql.Line($"IF @__sharpsql_package_lock_result < 0 THROW {ProvisioningLockErrorNumber}, 'Could not acquire the SharpSql package publishing lock.', 1;");
            sql.Line();
            sql.Line($"IF SCHEMA_ID({schemaLiteral}) IS NULL");
            using (sql.Indent())
                sql.Line($"EXEC({SqlIdentifier.UnicodeLiteral($"CREATE SCHEMA {schema} AUTHORIZATION [dbo];")});");
            if (package.RuntimeStorage == RuntimeStorageKind.MemoryOptimized)
            {
                sql.Line();
                foreach (var line in SharpSqlMemoryOptimizedRuntime.GenerateProvisioningSql(schemaName)
                    .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
                {
                    sql.Line(line);
                }
            }

            sql.Line();
            sql.Line("BEGIN TRANSACTION;");
            sql.Line();
            sql.Line($"IF OBJECT_ID({manifestLiteral}, N'U') IS NULL");
            using (sql.Indent())
            {
                var createManifest = $"CREATE TABLE {manifest} (" +
                    "[ApplicationName] NVARCHAR(128) NOT NULL CONSTRAINT " +
                    SqlIdentifier.Quote($"PK_{ManifestName}", ManifestName) + " PRIMARY KEY, " +
                    "[PackageVersion] NVARCHAR(128) NOT NULL, " +
                    "[ProgramHash] CHAR(64) NOT NULL, " +
                    "[EntryProcedureName] NVARCHAR(128) NOT NULL, " +
                    "[RuntimeStorage] NVARCHAR(32) NOT NULL, " +
                    "[NativeKernelsEnabled] BIT NOT NULL, " +
                    "[PublishedAtUtc] DATETIME2(7) NOT NULL);";
                sql.Line($"EXEC({SqlIdentifier.UnicodeLiteral(createManifest)});");
            }
            sql.Line($"IF EXISTS (SELECT 1 FROM {manifest} WHERE [ApplicationName] <> {SqlIdentifier.UnicodeLiteral(applicationName)})");
            using (sql.Indent())
                sql.Line($"THROW {PackageOwnershipErrorNumber}, 'The application schema is already owned by a different SharpSql package.', 1;");
            sql.Line($"DECLARE @__sharpsql_previous_entry SYSNAME = (SELECT [EntryProcedureName] FROM {manifest} WHERE [ApplicationName] = {SqlIdentifier.UnicodeLiteral(applicationName)});");
            sql.Line();
            var procedureDefinition = $"CREATE OR ALTER PROCEDURE {entryProcedure}{Environment.NewLine}" +
                $"AS{Environment.NewLine}" +
                $"BEGIN{Environment.NewLine}" +
                package.CompiledProgramSql.TrimEnd() + Environment.NewLine +
                "END;";
            sql.Line($"EXEC({SqlIdentifier.UnicodeLiteral(procedureDefinition)});");
            sql.Line($"IF OBJECT_ID({entryProcedureLiteral}, N'P') IS NULL THROW 51941, 'The SharpSql package entry procedure was not created.', 1;");
            sql.Line("IF @__sharpsql_previous_entry IS NOT NULL AND @__sharpsql_previous_entry <> " + SqlIdentifier.UnicodeLiteral(entryName));
            using (sql.Indent())
            {
                sql.Line("BEGIN");
                using (sql.Indent())
                {
                    sql.Line($"DECLARE @__sharpsql_drop_previous_entry NVARCHAR(776) = N'DROP PROCEDURE {schema}.' + QUOTENAME(@__sharpsql_previous_entry) + N';';");
                    sql.Line("EXEC sys.sp_executesql @__sharpsql_drop_previous_entry;");
                }
                sql.Line("END;");
            }
            sql.Line();
            sql.Line($"UPDATE {manifest}");
            using (sql.Indent())
            {
                sql.Line($"SET [PackageVersion] = {SqlIdentifier.UnicodeLiteral(version)},");
                sql.Line($"[ProgramHash] = N'{programHash}',");
                sql.Line($"[EntryProcedureName] = {SqlIdentifier.UnicodeLiteral(entryName)},");
                sql.Line($"[RuntimeStorage] = N'{package.RuntimeStorage}',");
                sql.Line($"[NativeKernelsEnabled] = {(package.EnableNativeKernels ? 1 : 0)},");
                sql.Line("[PublishedAtUtc] = SYSUTCDATETIME()");
            }
            sql.Line($"WHERE [ApplicationName] = {SqlIdentifier.UnicodeLiteral(applicationName)};");
            sql.Line("IF @@ROWCOUNT = 0");
            using (sql.Indent())
            {
                sql.Line($"INSERT INTO {manifest}");
                sql.Line("([ApplicationName], [PackageVersion], [ProgramHash], [EntryProcedureName], [RuntimeStorage], [NativeKernelsEnabled], [PublishedAtUtc])");
                sql.Line($"VALUES ({SqlIdentifier.UnicodeLiteral(applicationName)}, {SqlIdentifier.UnicodeLiteral(version)}, N'{programHash}', {SqlIdentifier.UnicodeLiteral(entryName)}, N'{package.RuntimeStorage}', {(package.EnableNativeKernels ? 1 : 0)}, SYSUTCDATETIME());");
            }
            sql.Line("COMMIT TRANSACTION;");
            sql.Line($"EXEC sys.sp_releaseapplock @Resource = {lockResource}, @LockOwner = N'Session';");
        }
        sql.Line("END TRY");
        sql.Line("BEGIN CATCH");
        using (sql.Indent())
        {
            sql.Line("IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;");
            sql.Line("IF @__sharpsql_package_lock_result >= 0");
            using (sql.Indent())
                sql.Line($"EXEC sys.sp_releaseapplock @Resource = {lockResource}, @LockOwner = N'Session';");
            sql.Line("THROW;");
        }
        sql.Line("END CATCH;");
        return sql.ToString();
    }

    internal static string EmitUninstall(string schemaName, string applicationName)
    {
        schemaName = SqlIdentifier.Validate(schemaName, nameof(schemaName));
        applicationName = RequiredManifestValue(applicationName, nameof(applicationName));
        var schema = SqlIdentifier.Quote(schemaName, nameof(schemaName));
        var schemaLiteral = SqlIdentifier.UnicodeLiteral(schemaName);
        var manifest = $"{schema}.{SqlIdentifier.Quote(ManifestName, ManifestName)}";
        var manifestLiteral = SqlIdentifier.UnicodeLiteral(manifest);
        var kernelCatalog = $"{schema}.{SqlIdentifier.Quote(NativeKernelRuntimeSqlEmitter.CatalogName, NativeKernelRuntimeSqlEmitter.CatalogName)}";
        var kernelCatalogLiteral = SqlIdentifier.UnicodeLiteral(kernelCatalog);
        var lockResource = SqlIdentifier.UnicodeLiteral($"SharpSql.Package.{schemaName}");

        var sql = new SqlWriter();
        sql.Line("SET XACT_ABORT ON;");
        sql.Line("SET NOCOUNT ON;");
        sql.Line("DECLARE @__sharpsql_package_lock_result INT;");
        sql.Line("BEGIN TRY");
        using (sql.Indent())
        {
            sql.Line("EXEC @__sharpsql_package_lock_result = sys.sp_getapplock");
            using (sql.Indent())
            {
                sql.Line($"@Resource = {lockResource},");
                sql.Line("@LockMode = N'Exclusive',");
                sql.Line("@LockOwner = N'Session',");
                sql.Line("@LockTimeout = 60000;");
            }
            sql.Line($"IF @__sharpsql_package_lock_result < 0 THROW {ProvisioningLockErrorNumber}, 'Could not acquire the SharpSql package publishing lock.', 1;");
            sql.Line($"IF OBJECT_ID({manifestLiteral}, N'U') IS NULL THROW {PackageNotInstalledErrorNumber}, 'The SharpSql package is not installed in this schema.', 1;");
            sql.Line("DECLARE @__sharpsql_entry SYSNAME;");
            sql.Line($"SELECT @__sharpsql_entry = [EntryProcedureName] FROM {manifest} WHERE [ApplicationName] = {SqlIdentifier.UnicodeLiteral(applicationName)};");
            sql.Line($"IF @__sharpsql_entry IS NULL THROW {PackageNotInstalledErrorNumber}, 'The requested SharpSql package is not installed in this schema.', 1;");
            sql.Line("BEGIN TRANSACTION;");
            sql.Line($"IF OBJECT_ID(QUOTENAME({schemaLiteral}) + N'.' + QUOTENAME(@__sharpsql_entry), N'P') IS NOT NULL");
            using (sql.Indent())
            {
                sql.Line("BEGIN");
                using (sql.Indent())
                {
                    sql.Line($"DECLARE @__sharpsql_drop_entry NVARCHAR(776) = N'DROP PROCEDURE {schema}.' + QUOTENAME(@__sharpsql_entry) + N';';");
                    sql.Line("EXEC sys.sp_executesql @__sharpsql_drop_entry;");
                }
                sql.Line("END;");
            }
            sql.Line("DECLARE @__sharpsql_drop_kernels NVARCHAR(MAX);");
            sql.Line("SELECT @__sharpsql_drop_kernels = STRING_AGG(CAST(N'DROP PROCEDURE ' + QUOTENAME(SCHEMA_NAME([schema_id])) + N'.' + QUOTENAME([name]) AS NVARCHAR(MAX)), N';')");
            using (sql.Indent())
                sql.Line($"FROM sys.procedures WHERE [schema_id] = SCHEMA_ID({schemaLiteral}) AND [name] LIKE N'NativeKernel[_]%';");
            sql.Line("IF @__sharpsql_drop_kernels IS NOT NULL EXEC sys.sp_executesql @__sharpsql_drop_kernels;");
            sql.Line($"IF OBJECT_ID({kernelCatalogLiteral}, N'U') IS NOT NULL DROP TABLE {kernelCatalog};");
            sql.Line($"DROP TABLE {manifest};");
            sql.Line($"IF TYPE_ID(N'{schemaName.Replace("'", "''", StringComparison.Ordinal)}.MemoryVmSlotsV1') IS NOT NULL DROP TYPE {schema}.[MemoryVmSlotsV1];");
            sql.Line($"IF TYPE_ID(N'{schemaName.Replace("'", "''", StringComparison.Ordinal)}.MemoryVmStackV1') IS NOT NULL DROP TYPE {schema}.[MemoryVmStackV1];");
            sql.Line("COMMIT TRANSACTION;");
            sql.Line($"EXEC sys.sp_releaseapplock @Resource = {lockResource}, @LockOwner = N'Session';");
        }
        sql.Line("END TRY");
        sql.Line("BEGIN CATCH");
        using (sql.Indent())
        {
            sql.Line("IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;");
            sql.Line("IF @__sharpsql_package_lock_result >= 0");
            using (sql.Indent())
                sql.Line($"EXEC sys.sp_releaseapplock @Resource = {lockResource}, @LockOwner = N'Session';");
            sql.Line("THROW;");
        }
        sql.Line("END CATCH;");
        return sql.ToString();
    }

    private static string RequiredManifestValue(string value, string parameterName)
    {
        if (value is null)
            throw new ArgumentNullException(parameterName);
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Package manifest values cannot be empty or whitespace.", parameterName);
        if (value.Length > 128)
            throw new ArgumentException("Package manifest values cannot exceed 128 characters.", parameterName);
        if (value.Any(character => char.IsControl(character) || char.IsSurrogate(character)))
            throw new ArgumentException("Package manifest values cannot contain control or UTF-16 surrogate characters.", parameterName);
        return value;
    }

    private static string ComputeProgramHash(string programSql)
    {
        using var algorithm = SHA256.Create();
        var bytes = algorithm.ComputeHash(Encoding.UTF8.GetBytes(programSql));
        var result = new StringBuilder(bytes.Length * 2);
        foreach (var value in bytes)
            result.Append(value.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
        return result.ToString();
    }
}
