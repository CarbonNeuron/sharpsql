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
        Assert.Contains("CREATE TABLE [SharpSql].[__sharpsql_memory_vm_stack_ephemeral_v1]", sql, StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE [SharpSql].[__sharpsql_memory_vm_slots_ephemeral_v1]", sql, StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE [SharpSql].[__sharpsql_memory_heap_objects_ephemeral_v1]", sql, StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE [SharpSql].[__sharpsql_memory_heap_indexed_items_ephemeral_v1]", sql, StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE [SharpSql].[__sharpsql_memory_heap_fields_ephemeral_v1]", sql, StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE [SharpSql].[__sharpsql_memory_heap_dictionary_entries_ephemeral_v1]", sql, StringComparison.Ordinal);
        Assert.Equal(6, Count(sql, "DURABILITY = SCHEMA_ONLY"));
        Assert.Contains("[__scalar_value] VARBINARY(8000) NULL", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("SQL_VARIANT", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ADD FILEGROUP", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(RuntimeDurabilityKind.Ephemeral, "SCHEMA_ONLY")]
    [InlineData(RuntimeDurabilityKind.Durable, "SCHEMA_AND_DATA")]
    public void PhysicalVmTablesMapDurabilityAndPartitionEveryKeyByExecution(
        RuntimeDurabilityKind durability,
        string expectedDurability)
    {
        var sql = MemoryOptimizedRuntimeSqlEmitter.EmitPhysicalTables(durability);

        Assert.Contains(
            $"CREATE TABLE [SharpSql].[{MemoryOptimizedRuntimeSqlEmitter.VmStackTableName(durability)}]",
            sql,
            StringComparison.Ordinal);
        Assert.Contains(
            $"CREATE TABLE [SharpSql].[{MemoryOptimizedRuntimeSqlEmitter.VmSlotsTableName(durability)}]",
            sql,
            StringComparison.Ordinal);
        Assert.Contains(
            $"CREATE TABLE [SharpSql].[{MemoryOptimizedRuntimeSqlEmitter.HeapObjectsTableName(durability)}]",
            sql,
            StringComparison.Ordinal);
        Assert.Contains(
            $"CREATE TABLE [SharpSql].[{MemoryOptimizedRuntimeSqlEmitter.HeapIndexedItemsTableName(durability)}]",
            sql,
            StringComparison.Ordinal);
        Assert.Equal(6, Count(sql, $"WITH (MEMORY_OPTIMIZED = ON, DURABILITY = {expectedDurability})"));
        Assert.Equal(6, Count(sql, "[__execution_id] UNIQUEIDENTIFIER NOT NULL"));
        Assert.Contains(
            "PRIMARY KEY NONCLUSTERED HASH ([__execution_id], [__id])",
            sql,
            StringComparison.Ordinal);
        Assert.Contains(
            "PRIMARY KEY NONCLUSTERED HASH ([__execution_id], [__frame_id], [__slot_id])",
            sql,
            StringComparison.Ordinal);
        Assert.DoesNotContain("FOREIGN KEY", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PhysicalTableProvisioningRejectsAnIncompatibleExistingStore()
    {
        var sql = MemoryOptimizedRuntimeSqlEmitter.EmitPhysicalTables(RuntimeDurabilityKind.Ephemeral);

        Assert.Equal(51935, RuntimeTableSqlEmitter.IncompatibleTableErrorNumber);
        Assert.Equal(6, Count(sql, "[is_memory_optimized] = 1 AND [durability_desc] = N'SCHEMA_ONLY'"));
        Assert.Equal(6, Count(sql, "THROW 51935, 'The existing SharpSql runtime table has incompatible physical storage.'"));
        Assert.DoesNotContain("DROP TABLE", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EphemeralAndDurablePhysicalProfilesCanCoexist()
    {
        var ephemeral = MemoryOptimizedRuntimeSqlEmitter.EmitPhysicalTables(RuntimeDurabilityKind.Ephemeral);
        var durable = MemoryOptimizedRuntimeSqlEmitter.EmitPhysicalTables(RuntimeDurabilityKind.Durable);

        Assert.Contains("[SharpSql].[__sharpsql_memory_vm_stack_ephemeral_v1]", ephemeral, StringComparison.Ordinal);
        Assert.Contains("[SharpSql].[__sharpsql_memory_vm_slots_ephemeral_v1]", ephemeral, StringComparison.Ordinal);
        Assert.Contains("[SharpSql].[__sharpsql_memory_heap_objects_ephemeral_v1]", ephemeral, StringComparison.Ordinal);
        Assert.Contains("[SharpSql].[__sharpsql_memory_heap_indexed_items_ephemeral_v1]", ephemeral, StringComparison.Ordinal);
        Assert.Contains("[SharpSql].[__sharpsql_memory_heap_fields_ephemeral_v1]", ephemeral, StringComparison.Ordinal);
        Assert.Contains("[SharpSql].[__sharpsql_memory_heap_dictionary_entries_ephemeral_v1]", ephemeral, StringComparison.Ordinal);
        Assert.DoesNotContain("_durable_v1]", ephemeral, StringComparison.Ordinal);
        Assert.Contains("[SharpSql].[__sharpsql_memory_vm_stack_durable_v1]", durable, StringComparison.Ordinal);
        Assert.Contains("[SharpSql].[__sharpsql_memory_vm_slots_durable_v1]", durable, StringComparison.Ordinal);
        Assert.Contains("[SharpSql].[__sharpsql_memory_heap_objects_durable_v1]", durable, StringComparison.Ordinal);
        Assert.Contains("[SharpSql].[__sharpsql_memory_heap_indexed_items_durable_v1]", durable, StringComparison.Ordinal);
        Assert.Contains("[SharpSql].[__sharpsql_memory_heap_fields_durable_v1]", durable, StringComparison.Ordinal);
        Assert.Contains("[SharpSql].[__sharpsql_memory_heap_dictionary_entries_durable_v1]", durable, StringComparison.Ordinal);
        Assert.DoesNotContain("_ephemeral_v1]", durable, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(RuntimeExecutionKind.Inline, RuntimeDurabilityKind.Ephemeral, "SCHEMA_ONLY")]
    [InlineData(RuntimeExecutionKind.Inline, RuntimeDurabilityKind.Durable, "SCHEMA_AND_DATA")]
    [InlineData(RuntimeExecutionKind.ServiceBroker, RuntimeDurabilityKind.Ephemeral, "SCHEMA_ONLY")]
    [InlineData(RuntimeExecutionKind.ServiceBroker, RuntimeDurabilityKind.Durable, "SCHEMA_AND_DATA")]
    public void RuntimeConfigurationSelectsPhysicalTableDurability(
        RuntimeExecutionKind execution,
        RuntimeDurabilityKind durability,
        string expectedDurability)
    {
        var runtime = new RuntimeConfiguration(
            execution,
            durability,
            UseMemoryOptimizedTables: true);

        var sql = SharpSqlMemoryOptimizedRuntime.GenerateProvisioningSql(runtime);

        Assert.Equal(6, Count(sql, $"WITH (MEMORY_OPTIMIZED = ON, DURABILITY = {expectedDurability})"));
    }

    [Fact]
    public void PhysicalProvisioningRequiresMemoryOptimization()
    {
        var runtime = new RuntimeConfiguration(
            RuntimeExecutionKind.Inline,
            RuntimeDurabilityKind.Ephemeral,
            UseMemoryOptimizedTables: false);

        var error = Assert.Throws<ArgumentException>(
            () => SharpSqlMemoryOptimizedRuntime.GenerateProvisioningSql(runtime));

        Assert.Equal("runtime", error.ParamName);
    }

    [Fact]
    public void RecursiveLegacyVmUsesPartitionedGlobalMemoryOptimizedTables()
    {
        var result = new SharpSqlCompiler().Transpile(
            RecursiveSource,
            new TranspileOptions { RuntimeStorage = RuntimeStorageKind.MemoryOptimized });

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains("DECLARE @__sharpsql_execution_id UNIQUEIDENTIFIER = NEWID()", result.Sql, StringComparison.Ordinal);
        Assert.Contains("[SharpSql].[__sharpsql_memory_vm_stack_ephemeral_v1]", result.Sql, StringComparison.Ordinal);
        Assert.Contains("[SharpSql].[__sharpsql_memory_vm_slots_ephemeral_v1]", result.Sql, StringComparison.Ordinal);
        Assert.Equal(51936, MemoryOptimizedRuntimeSqlEmitter.MissingPhysicalTableErrorNumber);
        Assert.Contains("THROW 51936, 'Provision the SharpSql memory-optimized runtime", result.Sql, StringComparison.Ordinal);
        Assert.Contains("(__execution_id, __function_id, __return_id, __caller_id)", result.Sql, StringComparison.Ordinal);
        Assert.Contains("AND __execution_id = @__sharpsql_execution_id", result.Sql, StringComparison.Ordinal);
        Assert.Contains("DELETE FROM [SharpSql].[__sharpsql_memory_vm_slots_ephemeral_v1] WHERE __execution_id = @__sharpsql_execution_id", result.Sql, StringComparison.Ordinal);
        Assert.Contains("BEGIN TRY", result.Sql, StringComparison.Ordinal);
        Assert.Contains("BEGIN CATCH", result.Sql, StringComparison.Ordinal);
        Assert.Contains("Preserve the original error after reclaiming this execution's shared state", result.Sql, StringComparison.Ordinal);
        Assert.Contains("__scalar_value", result.Sql, StringComparison.Ordinal);
        Assert.Contains("CONVERT(VARBINARY(8000)", result.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("CREATE TABLE #__sharpsql_stack", result.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("DECLARE @__sharpsql_memory_stack", result.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("MERGE @__sharpsql_memory_slots", result.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void DurableMemoryOptimizedVmUsesTheSamePartitionedPhysicalTables()
    {
        var result = new SharpSqlCompiler().Transpile(
            RecursiveSource,
            new TranspileOptions
            {
                Execution = RuntimeExecutionKind.Inline,
                Durability = RuntimeDurabilityKind.Durable,
                UseMemoryOptimizedTables = true
            });

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Equal(RuntimeDurabilityKind.Durable, result.EffectiveRuntime.Durability);
        Assert.Contains("SharpSql durable shared runtime", result.Sql, StringComparison.Ordinal);
        Assert.Contains("[SharpSql].[__sharpsql_memory_vm_stack_durable_v1]", result.Sql, StringComparison.Ordinal);
        Assert.Contains("[SharpSql].[__sharpsql_memory_vm_slots_durable_v1]", result.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("CREATE TABLE [SharpSql].[__sharpsql_stack]", result.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("DECLARE @__sharpsql_memory_stack", result.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void EntryReturnRoutesThroughGlobalVmCleanup()
    {
        const string source = """
            bool stop = false;
            if (stop)
                return;

            int CountDown(int value) => value <= 0 ? 0 : CountDown(value - 1);
            Console.WriteLine(CountDown(3));
            """;

        var result = new SharpSqlCompiler().Transpile(
            source,
            new TranspileOptions { UseMemoryOptimizedTables = true });

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains("GOTO __sharpsql_execution_cleanup", result.Sql, StringComparison.Ordinal);
        Assert.Contains("DELETE FROM [SharpSql].[__sharpsql_memory_vm_stack_ephemeral_v1] WHERE __execution_id = @__sharpsql_execution_id", result.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void MemoryOptimizedModeUsesSharedObjectRegistryAndIndexedItems()
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
        Assert.Contains("[SharpSql].[__sharpsql_memory_heap_objects_ephemeral_v1]", result.Sql, StringComparison.Ordinal);
        Assert.Contains("INSERT INTO [SharpSql].[__sharpsql_memory_heap_objects_ephemeral_v1] (__execution_id, __type_id, __count)", result.Sql, StringComparison.Ordinal);
        Assert.Contains("WHERE __execution_id = @__sharpsql_execution_id AND __id", result.Sql, StringComparison.Ordinal);
        Assert.Contains("[SharpSql].[__sharpsql_memory_heap_indexed_items_ephemeral_v1]", result.Sql, StringComparison.Ordinal);
        Assert.Contains("__execution_id, __owner_id, __index, __scalar_value", result.Sql, StringComparison.Ordinal);
        Assert.Contains("CONVERT(VARBINARY(8000), 1)", result.Sql, StringComparison.Ordinal);
        Assert.Contains("DELETE FROM [SharpSql].[__sharpsql_memory_heap_objects_ephemeral_v1] WHERE __execution_id = @__sharpsql_execution_id", result.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("CREATE TABLE #__sharpsql_objects", result.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("CREATE TABLE #__sharpsql_indexed_items", result.Sql, StringComparison.Ordinal);
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
