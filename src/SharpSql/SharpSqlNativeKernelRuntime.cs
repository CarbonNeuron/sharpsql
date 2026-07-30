namespace SharpSql;

/// <summary>Generates operator SQL for inspecting and retaining content-addressed native kernels.</summary>
public static class SharpSqlNativeKernelRuntime
{
    /// <summary>Generates a read-only status query for native kernels in an application schema.</summary>
    public static string GenerateStatusSql(string schemaName) =>
        NativeKernelRuntimeSqlEmitter.EmitStatus(schemaName);

    /// <summary>
    /// Generates a cleanup batch for kernels unused for at least the specified duration.
    /// Cleanup coordinates with compiler provisioning and live kernel calls.
    /// </summary>
    public static string GenerateCleanupSql(
        string schemaName,
        TimeSpan unusedFor,
        int batchSize = 20,
        bool dryRun = false) =>
        NativeKernelRuntimeSqlEmitter.EmitCleanup(schemaName, unusedFor, batchSize, dryRun);
}

internal sealed record NativeKernelDefinition(string Name, string QualifiedName, string ProvisioningSql);

internal static class NativeKernelRuntimeSqlEmitter
{
    internal const string CatalogName = "NativeKernelCatalog";
    internal const int LockErrorNumber = 51932;
    internal const int InvalidRetentionErrorNumber = 51933;

    internal static string KernelLockResource(string schemaName, string kernelName) =>
        $"SharpSql.NativeKernel.{schemaName}.{kernelName}";

    internal static string EmitProvisioning(
        string schemaName,
        IEnumerable<NativeKernelDefinition> definitions)
    {
        schemaName = SqlIdentifier.Validate(schemaName, nameof(schemaName));
        var kernels = definitions
            .GroupBy(item => item.Name, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
        var schema = SqlIdentifier.Quote(schemaName, nameof(schemaName));
        var catalog = $"{schema}.{SqlIdentifier.Quote(CatalogName, CatalogName)}";
        var catalogLiteral = SqlIdentifier.UnicodeLiteral(catalog);
        var catalogLock = SqlIdentifier.UnicodeLiteral($"SharpSql.NativeKernel.Catalog.{schemaName}");
        var sql = new SqlWriter();

        sql.Line("DECLARE @__sharpsql_native_catalog_lock INT;");
        sql.Line("EXEC @__sharpsql_native_catalog_lock = sys.sp_getapplock");
        using (sql.Indent())
        {
            sql.Line($"@Resource = {catalogLock},");
            sql.Line("@LockMode = N'Exclusive',");
            sql.Line("@LockOwner = N'Session',");
            sql.Line("@LockTimeout = 60000,");
            sql.Line("@DbPrincipal = N'public';");
        }
        sql.Line($"IF @__sharpsql_native_catalog_lock < 0 THROW {LockErrorNumber}, 'Could not acquire the SharpSql native-kernel catalog lock.', 1;");
        sql.Line("BEGIN TRY");
        using (sql.Indent())
        {
            sql.Line($"IF OBJECT_ID({catalogLiteral}, N'U') IS NULL");
            using (sql.Indent())
            {
                var create = $"CREATE TABLE {catalog} (" +
                    "[KernelName] SYSNAME NOT NULL PRIMARY KEY, " +
                    "[InstalledAtUtc] DATETIME2(7) NOT NULL, " +
                    "[LastUsedAtUtc] DATETIME2(7) NOT NULL);";
                sql.Line($"EXEC({SqlIdentifier.UnicodeLiteral(create)});");
            }
            sql.Line($"EXEC sys.sp_releaseapplock @Resource = {catalogLock}, @LockOwner = N'Session', @DbPrincipal = N'public';");
        }
        sql.Line("END TRY");
        sql.Line("BEGIN CATCH");
        using (sql.Indent())
        {
            sql.Line($"EXEC sys.sp_releaseapplock @Resource = {catalogLock}, @LockOwner = N'Session', @DbPrincipal = N'public';");
            sql.Line("THROW;");
        }
        sql.Line("END CATCH;");

        if (kernels.Length > 0)
            sql.Line("DECLARE @__sharpsql_native_kernel_lock INT;");
        foreach (var kernel in kernels)
        {
            var lockResource = SqlIdentifier.UnicodeLiteral(KernelLockResource(schemaName, kernel.Name));
            var nameLiteral = SqlIdentifier.UnicodeLiteral(kernel.Name);
            sql.Line("SET @__sharpsql_native_kernel_lock = NULL;");
            sql.Line("EXEC @__sharpsql_native_kernel_lock = sys.sp_getapplock");
            using (sql.Indent())
            {
                sql.Line($"@Resource = {lockResource},");
                sql.Line("@LockMode = N'Exclusive',");
                sql.Line("@LockOwner = N'Session',");
                sql.Line("@LockTimeout = 60000,");
                sql.Line("@DbPrincipal = N'public';");
            }
            sql.Line($"IF @__sharpsql_native_kernel_lock < 0 THROW {LockErrorNumber}, 'Could not acquire the SharpSql native-kernel provisioning lock.', 1;");
            sql.Line("BEGIN TRY");
            using (sql.Indent())
            {
                sql.Line(kernel.ProvisioningSql);
                sql.Line($"UPDATE {catalog} SET [LastUsedAtUtc] = SYSUTCDATETIME() WHERE [KernelName] = {nameLiteral};");
                sql.Line("IF @@ROWCOUNT = 0");
                using (sql.Indent())
                    sql.Line($"INSERT INTO {catalog} ([KernelName], [InstalledAtUtc], [LastUsedAtUtc]) VALUES ({nameLiteral}, SYSUTCDATETIME(), SYSUTCDATETIME());");
                sql.Line($"EXEC sys.sp_releaseapplock @Resource = {lockResource}, @LockOwner = N'Session', @DbPrincipal = N'public';");
            }
            sql.Line("END TRY");
            sql.Line("BEGIN CATCH");
            using (sql.Indent())
            {
                sql.Line($"EXEC sys.sp_releaseapplock @Resource = {lockResource}, @LockOwner = N'Session', @DbPrincipal = N'public';");
                sql.Line("THROW;");
            }
            sql.Line("END CATCH;");
        }

        return sql.ToString();
    }

    internal static string EmitStatus(string schemaName)
    {
        schemaName = SqlIdentifier.Validate(schemaName, nameof(schemaName));
        var schema = SqlIdentifier.Quote(schemaName, nameof(schemaName));
        var catalog = $"{schema}.{SqlIdentifier.Quote(CatalogName, CatalogName)}";
        var sql = new SqlWriter();
        sql.Line($"IF OBJECT_ID({SqlIdentifier.UnicodeLiteral(catalog)}, N'U') IS NULL");
        using (sql.Indent())
            sql.Line("THROW 51934, 'The SharpSql native-kernel catalog is not installed.', 1;");
        sql.Line("SELECT [catalog].[KernelName], [catalog].[InstalledAtUtc], [catalog].[LastUsedAtUtc],");
        using (sql.Indent())
            sql.Line($"CONVERT(BIT, CASE WHEN OBJECT_ID({SqlIdentifier.UnicodeLiteral(schemaName)} + N'.' + QUOTENAME([catalog].[KernelName]), N'P') IS NULL THEN 0 ELSE 1 END) AS [IsInstalled]");
        sql.Line($"FROM {catalog} AS [catalog] ORDER BY [catalog].[LastUsedAtUtc] DESC, [catalog].[KernelName];");
        return sql.ToString();
    }

    internal static string EmitCleanup(
        string schemaName,
        TimeSpan unusedFor,
        int batchSize,
        bool dryRun)
    {
        schemaName = SqlIdentifier.Validate(schemaName, nameof(schemaName));
        if (unusedFor < TimeSpan.FromMinutes(1) || unusedFor > TimeSpan.FromDays(3650))
            throw new ArgumentOutOfRangeException(nameof(unusedFor), "Native-kernel retention must be between one minute and ten years.");
        if (batchSize is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(batchSize), "Native-kernel cleanup batch size must be between 1 and 100.");

        var schema = SqlIdentifier.Quote(schemaName, nameof(schemaName));
        var catalog = $"{schema}.{SqlIdentifier.Quote(CatalogName, CatalogName)}";
        var catalogLiteral = SqlIdentifier.UnicodeLiteral(catalog);
        var cutoffSeconds = checked((long)unusedFor.TotalSeconds);
        var sql = new SqlWriter();
        sql.Line("SET NOCOUNT ON;");
        sql.Line($"IF OBJECT_ID({catalogLiteral}, N'U') IS NULL THROW 51934, 'The SharpSql native-kernel catalog is not installed.', 1;");
        sql.Line($"DECLARE @__sharpsql_cutoff DATETIME2(7) = DATEADD(SECOND, -{cutoffSeconds}, SYSUTCDATETIME());");
        sql.Line("DECLARE @__sharpsql_candidates TABLE ([KernelName] SYSNAME NOT NULL PRIMARY KEY, [Removed] BIT NOT NULL);");
        sql.Line($"INSERT INTO @__sharpsql_candidates SELECT TOP ({batchSize}) [KernelName], CONVERT(BIT, 0) FROM {catalog} WHERE [LastUsedAtUtc] < @__sharpsql_cutoff ORDER BY [LastUsedAtUtc], [KernelName];");
        if (dryRun)
        {
            sql.Line("SELECT [KernelName], [Removed] FROM @__sharpsql_candidates ORDER BY [KernelName];");
            return sql.ToString();
        }

        sql.Line("DECLARE @__sharpsql_kernel SYSNAME;");
        sql.Line("DECLARE @__sharpsql_lock INT;");
        sql.Line("DECLARE @__sharpsql_lock_resource NVARCHAR(255);");
        sql.Line("DECLARE [native_kernel] CURSOR LOCAL FAST_FORWARD FOR SELECT [KernelName] FROM @__sharpsql_candidates;");
        sql.Line("OPEN [native_kernel];");
        sql.Line("FETCH NEXT FROM [native_kernel] INTO @__sharpsql_kernel;");
        sql.Line("WHILE @@FETCH_STATUS = 0");
        sql.Line("BEGIN");
        using (sql.Indent())
        {
            sql.Line($"SET @__sharpsql_lock_resource = N'SharpSql.NativeKernel.{schemaName.Replace("'", "''", StringComparison.Ordinal)}.' + @__sharpsql_kernel;");
            sql.Line("EXEC @__sharpsql_lock = sys.sp_getapplock @Resource = @__sharpsql_lock_resource, @LockMode = N'Exclusive', @LockOwner = N'Session', @LockTimeout = 60000, @DbPrincipal = N'public';");
            sql.Line($"IF @__sharpsql_lock < 0 THROW {LockErrorNumber}, 'Could not acquire the SharpSql native-kernel cleanup lock.', 1;");
            sql.Line("BEGIN TRY");
            using (sql.Indent())
            {
                sql.Line($"IF EXISTS (SELECT 1 FROM {catalog} WHERE [KernelName] = @__sharpsql_kernel AND [LastUsedAtUtc] < @__sharpsql_cutoff)");
                sql.Line("BEGIN");
                using (sql.Indent())
                {
                    sql.Line($"DECLARE @__sharpsql_drop NVARCHAR(776) = N'DROP PROCEDURE {schema}.' + QUOTENAME(@__sharpsql_kernel) + N';';");
                    sql.Line($"IF OBJECT_ID({SqlIdentifier.UnicodeLiteral(schemaName)} + N'.' + QUOTENAME(@__sharpsql_kernel), N'P') IS NOT NULL EXEC sys.sp_executesql @__sharpsql_drop;");
                    sql.Line($"DELETE FROM {catalog} WHERE [KernelName] = @__sharpsql_kernel;");
                    sql.Line("UPDATE @__sharpsql_candidates SET [Removed] = 1 WHERE [KernelName] = @__sharpsql_kernel;");
                }
                sql.Line("END;");
                sql.Line("EXEC sys.sp_releaseapplock @Resource = @__sharpsql_lock_resource, @LockOwner = N'Session', @DbPrincipal = N'public';");
            }
            sql.Line("END TRY");
            sql.Line("BEGIN CATCH");
            using (sql.Indent())
            {
                sql.Line("EXEC sys.sp_releaseapplock @Resource = @__sharpsql_lock_resource, @LockOwner = N'Session', @DbPrincipal = N'public';");
                sql.Line("CLOSE [native_kernel];");
                sql.Line("DEALLOCATE [native_kernel];");
                sql.Line("THROW;");
            }
            sql.Line("END CATCH;");
            sql.Line("FETCH NEXT FROM [native_kernel] INTO @__sharpsql_kernel;");
        }
        sql.Line("END;");
        sql.Line("CLOSE [native_kernel];");
        sql.Line("DEALLOCATE [native_kernel];");
        sql.Line("SELECT [KernelName], [Removed] FROM @__sharpsql_candidates ORDER BY [KernelName];");
        return sql.ToString();
    }
}
