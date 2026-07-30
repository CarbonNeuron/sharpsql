namespace SharpSql;

/// <summary>Controls how a SharpSql program is executed.</summary>
public enum RuntimeExecutionKind
{
    /// <summary>Select Service Broker for reachable async code and inline execution otherwise.</summary>
    Auto = 0,

    /// <summary>Execute the generated program in the submitting SQL batch.</summary>
    Inline = 1,

    /// <summary>Execute async continuations through SQL Server Service Broker.</summary>
    ServiceBroker = 2
}

/// <summary>Controls whether runtime state is execution-local or durable.</summary>
public enum RuntimeDurabilityKind
{
    /// <summary>
    /// Use execution-scoped state that may be discarded after completion or restart.
    /// </summary>
    Ephemeral = 0,

    /// <summary>Use reusable permanent tables partitioned by execution.</summary>
    Durable = 1
}

/// <summary>Describes the independent runtime choices used to generate a SQL program.</summary>
/// <param name="Execution">The execution strategy.</param>
/// <param name="Durability">The runtime-state durability.</param>
/// <param name="UseMemoryOptimizedTables">Whether eligible runtime tables use memory-optimized types.</param>
public sealed record RuntimeConfiguration(
    RuntimeExecutionKind Execution,
    RuntimeDurabilityKind Durability,
    bool UseMemoryOptimizedTables);

/// <summary>Controls where SharpSql stores runtime state while a generated batch executes.</summary>
public enum RuntimeStorageKind
{
    /// <summary>Use execution-local temporary tables and remove them at the end of the batch.</summary>
    Ephemeral = 0,

    /// <summary>
    /// Compatibility alias for inline, ephemeral execution using memory-optimized tables.
    /// The database must first be provisioned with <see cref="SharpSqlMemoryOptimizedRuntime"/>.
    /// </summary>
    MemoryOptimized = 3,

    /// <summary>Use reusable permanent tables in the <c>SharpSql</c> schema, partitioned by execution.</summary>
    Durable = 1,

    /// <summary>
    /// Use the durable runtime and execute async continuations through SQL Server Service Broker.
    /// </summary>
    ServiceBroker = 2
}

/// <summary>Controls SQL generation and compiler resource limits.</summary>
public sealed record TranspileOptions
{
    private RuntimeExecutionKind? _execution;
    private RuntimeDurabilityKind? _durability;
    private bool? _useMemoryOptimizedTables;
    private RuntimeStorageKind? _runtimeStorage;

    /// <summary>Gets the maximum method statement count eligible for inline expansion.</summary>
    public int MaxInlineStatements { get; init; } = 40;

    /// <summary>Gets the maximum number of call sites eligible for inline expansion.</summary>
    public int MaxInlineCallSites { get; init; } = 8;

    /// <summary>Gets whether generated batches begin with <c>SET NOCOUNT ON</c>.</summary>
    public bool EmitNoCount { get; init; } = true;

    /// <summary>Gets whether generated batches emit runtime diagnostic result sets.</summary>
    public bool EmitRuntimeDiagnostics { get; init; }

    /// <summary>
    /// Extract supported pure scalar methods into natively compiled stored-procedure kernels.
    /// This experimental optimization requires <see cref="UseMemoryOptimizedTables"/>.
    /// </summary>
    public bool EnableNativeKernels { get; init; }

    /// <summary>
    /// Gets the schema containing application-scoped runtime types and native kernels.
    /// </summary>
    public string ApplicationSchema { get; init; } = "SharpSql";

    /// <summary>
    /// Gets the requested execution strategy. The default selects from the reachable bound program.
    /// </summary>
    public RuntimeExecutionKind Execution
    {
        get => _execution ?? RuntimeExecutionKind.Auto;
        init => _execution = value;
    }

    /// <summary>Gets the requested runtime-state durability.</summary>
    public RuntimeDurabilityKind Durability
    {
        get => _durability ?? RuntimeDurabilityKind.Ephemeral;
        init => _durability = value;
    }

    /// <summary>Gets whether eligible runtime tables use memory-optimized table types.</summary>
    public bool UseMemoryOptimizedTables
    {
        get => _useMemoryOptimizedTables ?? false;
        init => _useMemoryOptimizedTables = value;
    }

    /// <summary>
    /// Gets the legacy combined runtime choice. Prefer <see cref="Execution"/>,
    /// <see cref="Durability"/>, and <see cref="UseMemoryOptimizedTables"/> for new code.
    /// </summary>
    public RuntimeStorageKind RuntimeStorage
    {
        get => _runtimeStorage ?? ToLegacyRuntimeStorage(new RuntimeConfiguration(
            Execution,
            Durability,
            UseMemoryOptimizedTables));
        init => _runtimeStorage = value;
    }

    internal RuntimeConfiguration RequestedRuntime
    {
        get
        {
            var configuration = _runtimeStorage is { } legacy
                ? FromLegacyRuntimeStorage(legacy)
                : new RuntimeConfiguration(
                    RuntimeExecutionKind.Auto,
                    RuntimeDurabilityKind.Ephemeral,
                    UseMemoryOptimizedTables: false);
            return configuration with
            {
                Execution = _execution ?? configuration.Execution,
                Durability = _durability ?? configuration.Durability,
                UseMemoryOptimizedTables = _useMemoryOptimizedTables ?? configuration.UseMemoryOptimizedTables
            };
        }
    }

    private static RuntimeConfiguration FromLegacyRuntimeStorage(RuntimeStorageKind storage) => storage switch
    {
        RuntimeStorageKind.Ephemeral => new(
            RuntimeExecutionKind.Inline,
            RuntimeDurabilityKind.Ephemeral,
            UseMemoryOptimizedTables: false),
        RuntimeStorageKind.Durable => new(
            RuntimeExecutionKind.Inline,
            RuntimeDurabilityKind.Durable,
            UseMemoryOptimizedTables: false),
        RuntimeStorageKind.ServiceBroker => new(
            RuntimeExecutionKind.ServiceBroker,
            RuntimeDurabilityKind.Durable,
            UseMemoryOptimizedTables: false),
        RuntimeStorageKind.MemoryOptimized => new(
            RuntimeExecutionKind.Inline,
            RuntimeDurabilityKind.Ephemeral,
            UseMemoryOptimizedTables: true),
        _ => throw new ArgumentOutOfRangeException(nameof(RuntimeStorage), storage, "Unknown runtime storage kind.")
    };

    private static RuntimeStorageKind ToLegacyRuntimeStorage(RuntimeConfiguration configuration)
    {
        if (configuration.Execution == RuntimeExecutionKind.ServiceBroker)
            return RuntimeStorageKind.ServiceBroker;
        if (configuration.Durability == RuntimeDurabilityKind.Durable)
            return RuntimeStorageKind.Durable;
        return configuration.UseMemoryOptimizedTables
            ? RuntimeStorageKind.MemoryOptimized
            : RuntimeStorageKind.Ephemeral;
    }
}
