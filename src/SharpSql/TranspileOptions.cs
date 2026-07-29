namespace SharpSql;

/// <summary>Controls where SharpSql stores runtime state while a generated batch executes.</summary>
public enum RuntimeStorageKind
{
    /// <summary>Use execution-local temporary tables and remove them at the end of the batch.</summary>
    Ephemeral,

    /// <summary>Use reusable permanent tables in the <c>SharpSql</c> schema, partitioned by execution.</summary>
    Durable,

    /// <summary>
    /// Use the durable runtime and execute async continuations through SQL Server Service Broker.
    /// </summary>
    ServiceBroker
}

public sealed record TranspileOptions
{
    public int MaxInlineStatements { get; init; } = 40;
    public int MaxInlineCallSites { get; init; } = 8;
    public bool EmitNoCount { get; init; } = true;
    public bool EmitRuntimeDiagnostics { get; init; }

    /// <summary>
    /// Gets the runtime storage and execution strategy. The default preserves the ephemeral SQL contract.
    /// </summary>
    public RuntimeStorageKind RuntimeStorage { get; init; } = RuntimeStorageKind.Ephemeral;
}
