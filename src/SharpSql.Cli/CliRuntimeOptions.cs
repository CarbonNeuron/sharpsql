namespace SharpSql.Cli;

internal static class CliRuntimeOptions
{
    internal static RuntimeConfiguration Resolve(
        RuntimeExecutionKind execution,
        RuntimeDurabilityKind durability,
        bool useMemoryOptimizedTables,
        RuntimeStorageKind? compatibilityStorage) => compatibilityStorage switch
        {
            RuntimeStorageKind.Ephemeral => new RuntimeConfiguration(
                RuntimeExecutionKind.Inline,
                RuntimeDurabilityKind.Ephemeral,
                UseMemoryOptimizedTables: false),
            RuntimeStorageKind.MemoryOptimized => new RuntimeConfiguration(
                RuntimeExecutionKind.Inline,
                RuntimeDurabilityKind.Ephemeral,
                UseMemoryOptimizedTables: true),
            RuntimeStorageKind.Durable => new RuntimeConfiguration(
                RuntimeExecutionKind.Inline,
                RuntimeDurabilityKind.Durable,
                UseMemoryOptimizedTables: false),
            RuntimeStorageKind.ServiceBroker => new RuntimeConfiguration(
                RuntimeExecutionKind.ServiceBroker,
                RuntimeDurabilityKind.Durable,
                UseMemoryOptimizedTables: false),
            _ => new RuntimeConfiguration(execution, durability, useMemoryOptimizedTables)
        };

    internal static bool HasSplitConfiguration(
        RuntimeExecutionKind execution,
        RuntimeDurabilityKind durability,
        bool useMemoryOptimizedTables) =>
        execution != RuntimeExecutionKind.Auto ||
        durability != RuntimeDurabilityKind.Ephemeral ||
        useMemoryOptimizedTables;

    internal static RuntimeConfiguration ResolveSqlInput(RuntimeConfiguration requested)
    {
        var execution = requested.Execution == RuntimeExecutionKind.Auto
            ? RuntimeExecutionKind.Inline
            : requested.Execution;
        return requested with
        {
            Execution = execution
        };
    }
}
