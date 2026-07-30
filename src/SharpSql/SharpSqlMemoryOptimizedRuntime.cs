namespace SharpSql;

/// <summary>Produces database-scoped memory-optimized runtime types and physical tables.</summary>
public static class SharpSqlMemoryOptimizedRuntime
{
    /// <summary>
    /// Generates an idempotent provisioning script for ephemeral memory-optimized VM state.
    /// The target database must already contain a MEMORY_OPTIMIZED_DATA filegroup and container.
    /// </summary>
    public static string GenerateProvisioningSql() => GenerateProvisioningSql("SharpSql");

    /// <summary>
    /// Generates an idempotent provisioning script for ephemeral memory-optimized VM state
    /// in <paramref name="schemaName"/>.
    /// </summary>
    public static string GenerateProvisioningSql(string schemaName) =>
        GenerateProvisioningSql(schemaName, RuntimeDurabilityKind.Ephemeral);

    /// <summary>
    /// Generates an idempotent provisioning script for memory-optimized runtime state
    /// with the requested durability in <paramref name="schemaName"/>.
    /// </summary>
    public static string GenerateProvisioningSql(string schemaName, RuntimeDurabilityKind durability) =>
        MemoryOptimizedRuntimeSqlEmitter.Emit(schemaName) +
        Environment.NewLine +
        MemoryOptimizedRuntimeSqlEmitter.EmitPhysicalTables(durability, schemaName);

    /// <summary>
    /// Generates an idempotent provisioning script for memory-optimized runtime state.
    /// Ephemeral state uses database-global <c>SCHEMA_ONLY</c> tables; durable state uses
    /// <c>SCHEMA_AND_DATA</c>. Both forms partition rows by execution identifier.
    /// </summary>
    public static string GenerateProvisioningSql(RuntimeConfiguration runtime) =>
        GenerateProvisioningSql("SharpSql", runtime);

    /// <summary>
    /// Generates an idempotent provisioning script for memory-optimized runtime state
    /// in <paramref name="schemaName"/>.
    /// </summary>
    public static string GenerateProvisioningSql(string schemaName, RuntimeConfiguration runtime)
    {
        if (runtime is null)
            throw new ArgumentNullException(nameof(runtime));
        if (!runtime.UseMemoryOptimizedTables)
        {
            throw new ArgumentException(
                "Memory-optimized runtime provisioning requires UseMemoryOptimizedTables.",
                nameof(runtime));
        }

        return GenerateProvisioningSql(schemaName, runtime.Durability);
    }
}
