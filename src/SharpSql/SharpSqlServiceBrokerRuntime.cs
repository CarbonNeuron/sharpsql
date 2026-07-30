namespace SharpSql;

/// <summary>
/// Produces the database-scoped infrastructure used by SharpSql's Service Broker runtime.
/// </summary>
public static class SharpSqlServiceBrokerRuntime
{
    /// <summary>
    /// Generates an idempotent SQL Server provisioning script for execution tracking,
    /// output proxying, task scheduling, and the activated Service Broker worker pool.
    /// </summary>
    public static string GenerateProvisioningSql() =>
        ExecutionInfrastructureSqlEmitter.Emit() + Environment.NewLine +
        ExecutionInfrastructureSqlEmitter.EmitLifecycle() + Environment.NewLine +
        ServiceBrokerWorkerDispatcherSqlEmitter.Emit();
}
