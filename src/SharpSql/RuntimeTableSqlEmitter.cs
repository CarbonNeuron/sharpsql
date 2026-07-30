namespace SharpSql;

internal sealed record RuntimeTableDefinition(
    string Name,
    IReadOnlyList<string> Columns,
    string PrimaryKeyName,
    string PrimaryKeyColumns,
    int BucketCount);

/// <summary>Renders database-global runtime tables independently of their logical execution policy.</summary>
internal static class RuntimeTableSqlEmitter
{
    internal const int IncompatibleTableErrorNumber = 51935;

    internal static void EmitMemoryOptimizedTable(
        SqlWriter sql,
        string schemaName,
        RuntimeTableDefinition table,
        RuntimeDurabilityKind durability)
    {
        if (sql is null)
            throw new ArgumentNullException(nameof(sql));
        schemaName = SqlIdentifier.Validate(schemaName, nameof(schemaName));
        var tableName = SqlIdentifier.Validate(table.Name, nameof(table));
        var qualifiedName =
            $"{SqlIdentifier.Quote(schemaName, nameof(schemaName))}.{SqlIdentifier.Quote(tableName, nameof(table))}";
        var qualifiedNameLiteral = SqlIdentifier.UnicodeLiteral(qualifiedName);
        var durabilitySql = durability switch
        {
            RuntimeDurabilityKind.Ephemeral => "SCHEMA_ONLY",
            RuntimeDurabilityKind.Durable => "SCHEMA_AND_DATA",
            _ => throw new ArgumentOutOfRangeException(nameof(durability), durability, null)
        };

        sql.Line($"IF OBJECT_ID({qualifiedNameLiteral}, N'U') IS NOT NULL AND NOT EXISTS (");
        using (sql.Indent())
        {
            sql.Line("SELECT 1");
            sql.Line("FROM sys.tables");
            sql.Line($"WHERE [object_id] = OBJECT_ID({qualifiedNameLiteral}, N'U')");
            sql.Line($"AND [is_memory_optimized] = 1 AND [durability_desc] = N'{durabilitySql}'");
        }
        sql.Line(")");
        using (sql.Indent())
        {
            sql.Line(
                $"THROW {IncompatibleTableErrorNumber}, 'The existing SharpSql runtime table has incompatible physical storage.', 1;");
        }
        sql.Line($"IF OBJECT_ID({qualifiedNameLiteral}, N'U') IS NULL");
        sql.Line("BEGIN");
        using (sql.Indent())
        {
            sql.Line($"CREATE TABLE {qualifiedName} (");
            using (sql.Indent())
            {
                foreach (var column in table.Columns)
                    sql.Line(column + ",");
                sql.Line(
                    $"CONSTRAINT {SqlIdentifier.Quote(table.PrimaryKeyName, nameof(table))} " +
                    $"PRIMARY KEY NONCLUSTERED HASH ({table.PrimaryKeyColumns}) " +
                    $"WITH (BUCKET_COUNT = {table.BucketCount})");
            }
            sql.Line($") WITH (MEMORY_OPTIMIZED = ON, DURABILITY = {durabilitySql});");
        }
        sql.Line("END;");
    }
}
