namespace SharpSql;

/// <summary>Controls where SharpSql stores runtime state while a generated batch executes.</summary>
public enum RuntimeStorageKind
{
    /// <summary>Use execution-local temporary tables and remove them at the end of the batch.</summary>
    Ephemeral = 0,

    /// <summary>
    /// Use execution-local memory-optimized table variables for legacy VM state. The database
    /// must first be provisioned with <see cref="SharpSqlMemoryOptimizedRuntime"/>.
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
    /// This experimental optimization requires <see cref="RuntimeStorageKind.MemoryOptimized"/>.
    /// </summary>
    public bool EnableNativeKernels { get; init; }

    /// <summary>
    /// Gets the schema containing application-scoped runtime types and native kernels.
    /// </summary>
    public string ApplicationSchema { get; init; } = "SharpSql";

    /// <summary>
    /// Gets the runtime storage and execution strategy. The default preserves the ephemeral SQL contract.
    /// </summary>
    public RuntimeStorageKind RuntimeStorage { get; init; } = RuntimeStorageKind.Ephemeral;
}
