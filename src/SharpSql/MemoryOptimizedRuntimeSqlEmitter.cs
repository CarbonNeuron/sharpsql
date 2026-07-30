namespace SharpSql;

/// <summary>Emits compatibility table types and global memory-optimized VM tables.</summary>
internal static class MemoryOptimizedRuntimeSqlEmitter
{
    internal const int MissingFilegroupErrorNumber = 51921;
    internal const int MissingPhysicalTableErrorNumber = 51936;
    internal static string VmStackTableName(RuntimeDurabilityKind durability) =>
        $"__sharpsql_memory_vm_stack_{DurabilityName(durability)}_v1";

    internal static string VmSlotsTableName(RuntimeDurabilityKind durability) =>
        $"__sharpsql_memory_vm_slots_{DurabilityName(durability)}_v1";

    internal static string HeapObjectsTableName(RuntimeDurabilityKind durability) =>
        $"__sharpsql_memory_heap_objects_{DurabilityName(durability)}_v1";

    internal static string HeapIndexedItemsTableName(RuntimeDurabilityKind durability) =>
        $"__sharpsql_memory_heap_indexed_items_{DurabilityName(durability)}_v1";

    internal static string Emit(string schemaName = "SharpSql")
    {
        schemaName = SqlIdentifier.Validate(schemaName, nameof(schemaName));
        var schema = SqlIdentifier.Quote(schemaName, nameof(schemaName));
        var schemaLiteral = SqlIdentifier.UnicodeLiteral(schemaName);
        var stackType = $"{schema}.{SqlIdentifier.Quote("MemoryVmStackV1", "typeName")}";
        var slotsType = $"{schema}.{SqlIdentifier.Quote("MemoryVmSlotsV1", "typeName")}";
        var stackTypeLiteral = SqlIdentifier.UnicodeLiteral(stackType);
        var slotsTypeLiteral = SqlIdentifier.UnicodeLiteral(slotsType);
        var sql = new SqlWriter();
        sql.Line("SET ANSI_NULLS ON;");
        sql.Line("SET ANSI_PADDING ON;");
        sql.Line("SET ANSI_WARNINGS ON;");
        sql.Line("SET ARITHABORT ON;");
        sql.Line("SET CONCAT_NULL_YIELDS_NULL ON;");
        sql.Line("SET QUOTED_IDENTIFIER ON;");
        sql.Line("SET NUMERIC_ROUNDABORT OFF;");
        sql.Line();
        sql.Line("IF NOT EXISTS (SELECT 1 FROM sys.filegroups WHERE [type] = N'FX')");
        using (sql.Indent())
        {
            sql.Line(
                $"THROW {MissingFilegroupErrorNumber}, 'The database needs a MEMORY_OPTIMIZED_DATA filegroup before provisioning SharpSql memory-optimized runtime types.', 1;");
        }
        sql.Line();
        sql.Line($"IF SCHEMA_ID({schemaLiteral}) IS NULL");
        using (sql.Indent())
            sql.Line($"EXEC({SqlIdentifier.UnicodeLiteral($"CREATE SCHEMA {schema} AUTHORIZATION [dbo];")});");
        sql.Line();
        sql.Line($"IF TYPE_ID({stackTypeLiteral}) IS NULL");
        using (sql.Indent())
        {
            sql.Line($"EXEC(N'CREATE TYPE {stackType} AS TABLE (");
            using (sql.Indent())
            {
                sql.Line("[__id] INT IDENTITY(1,1) NOT NULL,");
                sql.Line("[__function_id] INT NOT NULL,");
                sql.Line("[__return_id] INT NOT NULL,");
                sql.Line("[__caller_id] INT NULL,");
                sql.Line("PRIMARY KEY NONCLUSTERED ([__id])");
            }
            sql.Line(") WITH (MEMORY_OPTIMIZED = ON);');");
        }
        sql.Line();
        sql.Line($"IF TYPE_ID({slotsTypeLiteral}) IS NULL");
        using (sql.Indent())
        {
            sql.Line($"EXEC(N'CREATE TYPE {slotsType} AS TABLE (");
            using (sql.Indent())
            {
                sql.Line("[__frame_id] INT NOT NULL,");
                sql.Line("[__slot_id] INT NOT NULL,");
                sql.Line("[__scalar_value] VARBINARY(8000) NULL,");
                sql.Line("[__text_value] NVARCHAR(MAX) NULL,");
                sql.Line("[__binary_value] VARBINARY(MAX) NULL,");
                sql.Line("PRIMARY KEY NONCLUSTERED ([__frame_id], [__slot_id])");
            }
            sql.Line(") WITH (MEMORY_OPTIMIZED = ON);');");
        }
        return sql.ToString();
    }

    internal static string EmitPhysicalTables(
        RuntimeDurabilityKind durability,
        string schemaName = "SharpSql")
    {
        schemaName = SqlIdentifier.Validate(schemaName, nameof(schemaName));
        var schema = SqlIdentifier.Quote(schemaName, nameof(schemaName));
        var schemaLiteral = SqlIdentifier.UnicodeLiteral(schemaName);
        var sql = new SqlWriter();
        sql.Line("SET ANSI_NULLS ON;");
        sql.Line("SET ANSI_PADDING ON;");
        sql.Line("SET ANSI_WARNINGS ON;");
        sql.Line("SET ARITHABORT ON;");
        sql.Line("SET CONCAT_NULL_YIELDS_NULL ON;");
        sql.Line("SET QUOTED_IDENTIFIER ON;");
        sql.Line("SET NUMERIC_ROUNDABORT OFF;");
        sql.Line();
        sql.Line("IF NOT EXISTS (SELECT 1 FROM sys.filegroups WHERE [type] = N'FX')");
        using (sql.Indent())
        {
            sql.Line(
                $"THROW {MissingFilegroupErrorNumber}, 'The database needs a MEMORY_OPTIMIZED_DATA filegroup before provisioning SharpSql memory-optimized runtime tables.', 1;");
        }
        sql.Line();
        sql.Line($"IF SCHEMA_ID({schemaLiteral}) IS NULL");
        using (sql.Indent())
            sql.Line($"EXEC({SqlIdentifier.UnicodeLiteral($"CREATE SCHEMA {schema} AUTHORIZATION [dbo];")});");
        sql.Line();
        RuntimeTableSqlEmitter.EmitMemoryOptimizedTable(sql, schemaName, VmStackTable(durability), durability);
        sql.Line();
        RuntimeTableSqlEmitter.EmitMemoryOptimizedTable(sql, schemaName, VmSlotsTable(durability), durability);
        sql.Line();
        RuntimeTableSqlEmitter.EmitMemoryOptimizedTable(sql, schemaName, HeapObjectsTable(durability), durability);
        sql.Line();
        RuntimeTableSqlEmitter.EmitMemoryOptimizedTable(sql, schemaName, HeapIndexedItemsTable(durability), durability);
        return sql.ToString();
    }

    private static RuntimeTableDefinition VmStackTable(RuntimeDurabilityKind durability) => new(
        VmStackTableName(durability),
        [
            "[__execution_id] UNIQUEIDENTIFIER NOT NULL",
            "[__id] INT IDENTITY(1,1) NOT NULL",
            "[__function_id] INT NOT NULL",
            "[__return_id] INT NOT NULL",
            "[__caller_id] INT NULL"
        ],
        $"PK_sharpsql_memory_vm_stack_{DurabilityName(durability)}_v1",
        "[__execution_id], [__id]",
        131_072);

    private static RuntimeTableDefinition VmSlotsTable(RuntimeDurabilityKind durability) => new(
        VmSlotsTableName(durability),
        [
            "[__execution_id] UNIQUEIDENTIFIER NOT NULL",
            "[__frame_id] INT NOT NULL",
            "[__slot_id] INT NOT NULL",
            "[__scalar_value] VARBINARY(8000) NULL",
            "[__text_value] NVARCHAR(MAX) NULL",
            "[__binary_value] VARBINARY(MAX) NULL"
        ],
        $"PK_sharpsql_memory_vm_slots_{DurabilityName(durability)}_v1",
        "[__execution_id], [__frame_id], [__slot_id]",
        524_288);

    private static RuntimeTableDefinition HeapObjectsTable(RuntimeDurabilityKind durability) => new(
        HeapObjectsTableName(durability),
        [
            "[__execution_id] UNIQUEIDENTIFIER NOT NULL",
            "[__id] INT IDENTITY(1,1) NOT NULL",
            "[__type_id] INT NOT NULL",
            "[__count] INT NULL",
            "[__state0] INT NULL",
            "[__state1] INT NULL"
        ],
        $"PK_sharpsql_memory_heap_objects_{DurabilityName(durability)}_v1",
        "[__execution_id], [__id]",
        524_288);

    private static RuntimeTableDefinition HeapIndexedItemsTable(RuntimeDurabilityKind durability) => new(
        HeapIndexedItemsTableName(durability),
        [
            "[__execution_id] UNIQUEIDENTIFIER NOT NULL",
            "[__owner_id] INT NOT NULL",
            "[__index] INT NOT NULL",
            "[__scalar_value] VARBINARY(8000) NULL",
            "[__text_value] NVARCHAR(MAX) NULL",
            "[__binary_value] VARBINARY(MAX) NULL",
            "[__reference_value] INT NULL"
        ],
        $"PK_sharpsql_memory_heap_indexed_items_{DurabilityName(durability)}_v1",
        "[__execution_id], [__owner_id], [__index]",
        1_048_576);

    private static string DurabilityName(RuntimeDurabilityKind durability) => durability switch
    {
        RuntimeDurabilityKind.Ephemeral => "ephemeral",
        RuntimeDurabilityKind.Durable => "durable",
        _ => throw new ArgumentOutOfRangeException(nameof(durability), durability, null)
    };
}
