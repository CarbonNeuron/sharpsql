using Xunit;

namespace SharpSql.Tests;

public sealed class MemoryOptimizedHeapPayloadProvisioningTests
{
    [Theory]
    [InlineData(RuntimeDurabilityKind.Ephemeral, "SCHEMA_ONLY")]
    [InlineData(RuntimeDurabilityKind.Durable, "SCHEMA_AND_DATA")]
    public void ProvisionsTypedFieldAndDictionaryUnionTables(
        RuntimeDurabilityKind durability,
        string expectedDurability)
    {
        var sql = MemoryOptimizedRuntimeSqlEmitter.EmitPhysicalTables(durability);
        var fields = TableSql(sql, MemoryOptimizedRuntimeSqlEmitter.HeapFieldsTableName(durability));
        var dictionaries = TableSql(
            sql,
            MemoryOptimizedRuntimeSqlEmitter.HeapDictionaryEntriesTableName(durability));

        Assert.Contains("[__object_id] INT NOT NULL", fields, StringComparison.Ordinal);
        Assert.Contains("[__declaring_type_id] INT NOT NULL", fields, StringComparison.Ordinal);
        Assert.Contains("[__field_id] INT NOT NULL", fields, StringComparison.Ordinal);
        Assert.Contains("[__scalar_value] VARBINARY(8000) NULL", fields, StringComparison.Ordinal);
        Assert.Contains(
            "PRIMARY KEY NONCLUSTERED HASH ([__execution_id], [__object_id], [__declaring_type_id], [__field_id])",
            fields,
            StringComparison.Ordinal);

        Assert.Contains("[__id] BIGINT IDENTITY(1,1) NOT NULL", dictionaries, StringComparison.Ordinal);
        Assert.Contains("[__dictionary_id] INT NOT NULL", dictionaries, StringComparison.Ordinal);
        Assert.Contains("[__key_scalar] VARBINARY(8000) NULL", dictionaries, StringComparison.Ordinal);
        Assert.Contains("[__key_hash] BINARY(32) NULL", dictionaries, StringComparison.Ordinal);
        Assert.Contains("[__value_scalar] VARBINARY(8000) NULL", dictionaries, StringComparison.Ordinal);
        Assert.Contains(
            "PRIMARY KEY NONCLUSTERED HASH ([__execution_id], [__dictionary_id], [__id])",
            dictionaries,
            StringComparison.Ordinal);
        Assert.Contains($"DURABILITY = {expectedDurability}", fields, StringComparison.Ordinal);
        Assert.Contains($"DURABILITY = {expectedDurability}", dictionaries, StringComparison.Ordinal);
        Assert.DoesNotContain("SQL_VARIANT", fields, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SQL_VARIANT", dictionaries, StringComparison.OrdinalIgnoreCase);
    }

    private static string TableSql(string sql, string tableName)
    {
        var marker = $"CREATE TABLE [SharpSql].[{tableName}]";
        var start = sql.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Provisioning block for '{tableName}' was not found.");
        var end = sql.IndexOf(") WITH (MEMORY_OPTIMIZED = ON, DURABILITY = ", start, StringComparison.Ordinal);
        Assert.True(end > start, $"Provisioning block for '{tableName}' was incomplete.");
        return sql[start..Math.Min(sql.Length, end + 80)];
    }
}
