namespace SharpSql;

public sealed partial class SharpSqlCompiler
{
    private const string DurableRuntimeSchema = "[SharpSql]";
    private const string RuntimeExecutionId = "@__sharpsql_execution_id";
    private const string RuntimeProvisioningLockResult = "@__sharpsql_provisioning_lock_result";
    private const string RuntimeProvisioningLockResource = "SharpSql.Runtime.Provisioning";
    private string? _runtimeCleanupLabel;
    private RuntimeConfiguration _effectiveRuntime = new(
        RuntimeExecutionKind.Inline,
        RuntimeDurabilityKind.Ephemeral,
        UseMemoryOptimizedTables: false);

    private bool UsesMemoryOptimizedRuntime => _effectiveRuntime.UseMemoryOptimizedTables;
    private bool UsesServiceBrokerRuntime => _effectiveRuntime.Execution == RuntimeExecutionKind.ServiceBroker;
    private bool UsesDurableRuntime =>
        _effectiveRuntime.Durability == RuntimeDurabilityKind.Durable || UsesServiceBrokerRuntime;
    private bool UsesExecutionScopedRuntime => UsesDurableRuntime || UsesMemoryOptimizedRuntime;
    private string RuntimeCleanupLabel => _runtimeCleanupLabel ??= _names.AllocateLabel("execution_cleanup");

    private bool ResolveRuntimeConfiguration(IrProgram program)
    {
        var requested = _options.RequestedRuntime;
        ValidateRuntimeConfiguration(requested);
        var requiresAsync = ProgramRequiresAsyncExecution(program);
        var execution = requested.Execution == RuntimeExecutionKind.Auto
            ? requiresAsync ? RuntimeExecutionKind.ServiceBroker : RuntimeExecutionKind.Inline
            : requested.Execution;
        _effectiveRuntime = requested with
        {
            Execution = execution
        };

        if (requiresAsync && execution == RuntimeExecutionKind.Inline)
        {
            AddDiagnostic(
                "SS7006",
                "RuntimeExecutionKind.Inline cannot execute reachable async or await code. Use Auto or ServiceBroker.",
                program.EntryPoint.Source);
            return false;
        }
        return true;
    }

    private void ResolveRuntimeConfigurationWithoutProgram()
    {
        var requested = _options.RequestedRuntime;
        ValidateRuntimeConfiguration(requested);
        _effectiveRuntime = requested with
        {
            Execution = requested.Execution == RuntimeExecutionKind.Auto
                ? RuntimeExecutionKind.Inline
                : requested.Execution
        };
    }

    private static void ValidateRuntimeConfiguration(RuntimeConfiguration configuration)
    {
        if (!Enum.IsDefined(typeof(RuntimeExecutionKind), configuration.Execution))
            throw new ArgumentOutOfRangeException(nameof(TranspileOptions.Execution));
        if (!Enum.IsDefined(typeof(RuntimeDurabilityKind), configuration.Durability))
            throw new ArgumentOutOfRangeException(nameof(TranspileOptions.Durability));
    }

    private bool ProgramRequiresAsyncExecution(IrProgram program)
    {
        if (AsyncStateMachinePlan.Create("__entry", program.EntryPoint).SuspensionPoints.Count > 0)
            return true;
        if (_methodGraph is null)
            return false;
        var methods = program.Methods.ToDictionary(method => method.Id);
        foreach (var methodId in _methodGraph.ReachableFromEntryPoint())
        {
            if (!methods.TryGetValue(methodId, out var method))
                continue;
            if (method.IsAsync || AsyncStateMachinePlan.Create(method.Name, method).SuspensionPoints.Count > 0)
                return true;
        }
        return false;
    }

    private void EmitDurableRuntimePreamble()
    {
        if (!UsesExecutionScopedRuntime)
            return;

        if (!UsesDurableRuntime)
        {
            _sql.Line("-- SharpSql execution-scoped shared runtime");
            _sql.Line($"DECLARE {RuntimeExecutionId} UNIQUEIDENTIFIER = NEWID();");
            _sql.Line();
            return;
        }

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
        if (UsesExecutionScopedRuntime)
            _sql.Line("BEGIN TRY");
    }

    private void EmitDurableExecutionCleanupLabel()
    {
        if (!UsesExecutionScopedRuntime)
            return;

        EmitLabel(RuntimeCleanupLabel);
        EmitDurableVmCleanup();
    }

    private void EmitDurableExecutionBodyEpilogue()
    {
        if (!UsesExecutionScopedRuntime)
            return;

        EmitServiceBrokerRegistryCleanup();
        _sql.Line("END TRY");
        _sql.Line("BEGIN CATCH");
        using (_sql.Indent())
        {
            _sql.Line("-- Preserve the original error after reclaiming this execution's shared state.");
            EmitDurableVmCleanup();
            EmitDurableHeapCleanup();
            EmitServiceBrokerRegistryCleanup();
            _sql.Line("THROW;");
        }
        _sql.Line("END CATCH;");
    }
}
