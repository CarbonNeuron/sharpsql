namespace SharpSql;

public sealed partial class SharpSqlCompiler
{
    private const string DurableRuntimeSchema = "[SharpSql]";
    private const string RuntimeExecutionId = "@__sharpsql_execution_id";
    private const string RuntimeProvisioningLockResult = "@__sharpsql_provisioning_lock_result";
    private const string RuntimeProvisioningLockResource = "SharpSql.Runtime.Provisioning";
    private string? _runtimeCleanupLabel;

    private bool UsesDurableRuntime => _options.RuntimeStorage != RuntimeStorageKind.Ephemeral;
    private bool UsesServiceBrokerRuntime => _options.RuntimeStorage == RuntimeStorageKind.ServiceBroker;
    private string RuntimeCleanupLabel => _runtimeCleanupLabel ??= _names.AllocateLabel("execution_cleanup");

    private void EmitDurableRuntimePreamble()
    {
        if (!UsesDurableRuntime)
            return;

        _sql.Line("SET ANSI_NULLS ON;");
        _sql.Line("SET ANSI_PADDING ON;");
        _sql.Line("SET ANSI_WARNINGS ON;");
        _sql.Line("SET ARITHABORT ON;");
        _sql.Line("SET CONCAT_NULL_YIELDS_NULL ON;");
        _sql.Line("SET QUOTED_IDENTIFIER ON;");
        _sql.Line("SET NUMERIC_ROUNDABORT OFF;");
        _sql.Line();
        _sql.Line("-- SharpSql durable shared runtime");
        _sql.Line("IF @@TRANCOUNT > 0 THROW 51904, 'SharpSql durable runtime provisioning must run outside an existing transaction.', 1;");
        _sql.Line($"DECLARE {RuntimeExecutionId} UNIQUEIDENTIFIER = NEWID();");
        _sql.Line($"DECLARE {RuntimeProvisioningLockResult} INT;");
        _sql.Line("BEGIN TRY");
        _sql.Line("BEGIN TRANSACTION;");
        _sql.Line(
            $"EXEC {RuntimeProvisioningLockResult} = sys.sp_getapplock " +
            $"@Resource = N'{RuntimeProvisioningLockResource}', @LockMode = 'Exclusive', " +
            "@LockOwner = 'Transaction', @LockTimeout = 60000;");
        _sql.Line($"IF {RuntimeProvisioningLockResult} < 0 THROW 51900, 'Unable to provision the SharpSql durable runtime.', 1;");
        _sql.Line("IF SCHEMA_ID(N'SharpSql') IS NULL EXEC(N'CREATE SCHEMA [SharpSql] AUTHORIZATION [dbo]');");
    }

    private void EmitDurableRuntimeProvisioningEpilogue()
    {
        if (!UsesDurableRuntime)
            return;

        _sql.Line("COMMIT TRANSACTION;");
        _sql.Line("END TRY");
        _sql.Line("BEGIN CATCH");
        _sql.Line("    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;");
        _sql.Line("    THROW;");
        _sql.Line("END CATCH;");
        _sql.Line();
    }

    private void EmitDurableExecutionBodyPreamble()
    {
        if (UsesDurableRuntime)
            _sql.Line("BEGIN TRY");
    }

    private void EmitDurableExecutionCleanupLabel()
    {
        if (!UsesDurableRuntime)
            return;

        EmitLabel(RuntimeCleanupLabel);
        EmitDurableVmCleanup();
    }

    private void EmitDurableExecutionBodyEpilogue()
    {
        if (!UsesDurableRuntime)
            return;

        EmitServiceBrokerRegistryCleanup();
        _sql.Line("END TRY");
        _sql.Line("BEGIN CATCH");
        using (_sql.Indent())
        {
            _sql.Line("-- Preserve the original error after reclaiming this execution's durable state.");
            EmitDurableVmCleanup();
            EmitDurableHeapCleanup();
            EmitServiceBrokerRegistryCleanup();
            _sql.Line("THROW;");
        }
        _sql.Line("END CATCH;");
    }
}
