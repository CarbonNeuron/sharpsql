using Xunit;

namespace SharpSql.Tests;

public sealed class MemoryOptimizedIndexedItemsProvisioningTests
{
    [Theory]
    [InlineData(RuntimeDurabilityKind.Ephemeral, "ephemeral", "SCHEMA_ONLY")]
    [InlineData(RuntimeDurabilityKind.Durable, "durable", "SCHEMA_AND_DATA")]
    public void ProvisionsExecutionPartitionedIndexedItemsWithoutSqlVariant(
        RuntimeDurabilityKind durability,
        string durabilityName,
        string durabilitySql)
    {
        var sql = MemoryOptimizedRuntimeSqlEmitter.EmitPhysicalTables(durability);
        var tableName = MemoryOptimizedRuntimeSqlEmitter.HeapIndexedItemsTableName(durability);

        Assert.Equal($"__sharpsql_memory_heap_indexed_items_{durabilityName}_v1", tableName);
        Assert.Contains($"CREATE TABLE [SharpSql].[{tableName}]", sql, StringComparison.Ordinal);
        Assert.Contains("[__execution_id] UNIQUEIDENTIFIER NOT NULL", TableSql(sql, tableName), StringComparison.Ordinal);
        Assert.Contains("[__owner_id] INT NOT NULL", TableSql(sql, tableName), StringComparison.Ordinal);
        Assert.Contains("[__index] INT NOT NULL", TableSql(sql, tableName), StringComparison.Ordinal);
        Assert.Contains("[__scalar_value] VARBINARY(8000) NULL", TableSql(sql, tableName), StringComparison.Ordinal);
        Assert.Contains("[__text_value] NVARCHAR(MAX) NULL", TableSql(sql, tableName), StringComparison.Ordinal);
        Assert.Contains("[__binary_value] VARBINARY(MAX) NULL", TableSql(sql, tableName), StringComparison.Ordinal);
        Assert.Contains("[__reference_value] INT NULL", TableSql(sql, tableName), StringComparison.Ordinal);
        Assert.Contains(
            "PRIMARY KEY NONCLUSTERED HASH ([__execution_id], [__owner_id], [__index])",
            TableSql(sql, tableName),
            StringComparison.Ordinal);
        Assert.Contains($"DURABILITY = {durabilitySql}", TableSql(sql, tableName), StringComparison.Ordinal);
        Assert.DoesNotContain("SQL_VARIANT", TableSql(sql, tableName), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IndexedItemsProvisioningIsIdempotentAndRejectsIncompatibleStorage()
    {
        var durability = RuntimeDurabilityKind.Ephemeral;
        var tableName = MemoryOptimizedRuntimeSqlEmitter.HeapIndexedItemsTableName(durability);
        var sql = MemoryOptimizedRuntimeSqlEmitter.EmitPhysicalTables(durability);
        var tableSql = TableSql(sql, tableName);

        Assert.Contains($"IF OBJECT_ID(N'[SharpSql].[{tableName}]', N'U') IS NULL", tableSql, StringComparison.Ordinal);
        Assert.Contains("[is_memory_optimized] = 1", tableSql, StringComparison.Ordinal);
        Assert.Contains("[durability_desc] = N'SCHEMA_ONLY'", tableSql, StringComparison.Ordinal);
        Assert.Contains(
            $"THROW {RuntimeTableSqlEmitter.IncompatibleTableErrorNumber}, 'The existing SharpSql runtime table has incompatible physical storage.'",
            tableSql,
            StringComparison.Ordinal);
        Assert.DoesNotContain("DROP TABLE", tableSql, StringComparison.OrdinalIgnoreCase);
    }

    private static string TableSql(string sql, string tableName)
    {
        var marker = $"IF OBJECT_ID(N'[SharpSql].[{tableName}]'";
        var start = sql.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Provisioning block for '{tableName}' was not found.");
        return sql[start..];
    }
}
