namespace SharpSql;

/// <summary>Produces the database-scoped types used by memory-optimized legacy execution.</summary>
public static class SharpSqlMemoryOptimizedRuntime
{
    /// <summary>
    /// Generates an idempotent provisioning script for the memory-optimized VM table types.
    /// The target database must already contain a MEMORY_OPTIMIZED_DATA filegroup and container.
    /// </summary>
    public static string GenerateProvisioningSql() => MemoryOptimizedRuntimeSqlEmitter.Emit();
}
