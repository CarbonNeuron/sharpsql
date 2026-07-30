using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;

namespace SharpSql;

public sealed partial class SharpSqlCompiler
{
    private const string ServiceBrokerEntryHandler = "__entry";
    private const int ServiceBrokerProgramMissingError = 51920;
    private const int ServiceBrokerHandlerMissingError = 51921;
    private const int ServiceBrokerExecutionLostError = 51922;
    private const int ServiceBrokerUnhandledDatabaseError = 51923;
    private const int ServiceBrokerCanceledError = 51924;
    private const int ServiceBrokerWorkerTransactionRequiredError = 51927;
    private const string ServiceBrokerProgramAbi = "2";

    private bool _emittingServiceBrokerWorker;
    private bool _serviceBrokerProgramEmitted;
    private string? _serviceBrokerProgramId;

    private bool TryEmitServiceBrokerProgram(IrProgram program)
    {
        if (!UsesServiceBrokerRuntime)
            return false;

        var rootStateMachine = AsyncStateMachinePlan.Create(ServiceBrokerEntryHandler, program.EntryPoint);
        var reachableMethods = _methodGraph?.ReachableFromEntryPoint().ToHashSet() ?? [];
        var asyncMethods = program.Methods
            .Where(method => method.IsAsync && reachableMethods.Contains(method.Id))
            .ToArray();
        if (rootStateMachine.SuspensionPoints.Count == 0 && asyncMethods.Length == 0)
            return false;

        if (!TryCreateServiceBrokerPlan(program, rootStateMachine, asyncMethods, out var plan))
        {
            _sql.Line(
                $"THROW {ServiceBrokerProgramMissingError}, " +
                "'This async program uses a shape that the Service Broker backend does not support yet.', 1;");
            return true;
        }

        _serviceBrokerProgramId = ComputeServiceBrokerProgramId(program);
        var procedureName = ServiceBrokerProcedureName(_serviceBrokerProgramId);
        var procedureSql = CaptureServiceBrokerWorkerSql(() => EmitServiceBrokerWorkerProcedure(plan, procedureName));
        _sql.Line("-- SharpSql Service Broker program worker");
        _sql.Line($"IF OBJECT_ID(N'[SharpSql].[{ExecutionInfrastructureSqlEmitter.RegisterProgramProcedureName}]', N'P') IS NULL");
        using (_sql.Indent())
            _sql.Line($"THROW {ServiceBrokerProgramMissingError}, 'Run SharpSqlServiceBrokerRuntime.GenerateProvisioningSql() before executing an async program.', 1;");
        _sql.Line("DECLARE @__sharpsql_program_lock_result INT;");
        _sql.Line("BEGIN TRY");
        using (_sql.Indent())
        {
            _sql.Line("BEGIN TRANSACTION;");
            _sql.Line("EXEC @__sharpsql_program_lock_result = sys.sp_getapplock");
            using (_sql.Indent())
            {
                _sql.Line($"@Resource = N'SharpSql.ServiceBroker.Program.{_serviceBrokerProgramId}',");
                _sql.Line("@LockMode = N'Exclusive',");
                _sql.Line("@LockOwner = N'Transaction',");
                _sql.Line("@LockTimeout = 60000,");
                _sql.Line("@DbPrincipal = N'public';");
            }
            _sql.Line("IF @__sharpsql_program_lock_result < 0");
            using (_sql.Indent())
                _sql.Line($"THROW {ExecutionInfrastructureSqlEmitter.ProvisioningLockErrorNumber}, 'Could not acquire the SharpSql program installation lock.', 1;");
            _sql.Line($"EXEC(N'{procedureSql.TrimEnd().Replace("'", "''", StringComparison.Ordinal)}');");
            _sql.Line($"EXEC [SharpSql].[{ExecutionInfrastructureSqlEmitter.RegisterProgramProcedureName}] @ProgramId = N'{_serviceBrokerProgramId}';");
            _sql.Line("COMMIT TRANSACTION;");
        }
        _sql.Line("END TRY");
        _sql.Line("BEGIN CATCH");
        using (_sql.Indent())
        {
            _sql.Line("IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;");
            _sql.Line("THROW;");
        }
        _sql.Line("END CATCH;");
        _sql.Line();
        EmitServiceBrokerLauncher(_serviceBrokerProgramId);
        _serviceBrokerProgramEmitted = true;
        return true;
    }

    private bool TryCreateServiceBrokerPlan(
        IrProgram program,
        AsyncStateMachinePlan rootStateMachine,
        IReadOnlyList<MethodDefinition> asyncMethods,
        out ServiceBrokerProgramPlan plan)
    {
        plan = null!;
        if (_vmMethods.Count > 0)
        {
            AddDiagnostic(
                "SS7005",
                "The current Service Broker backend cannot call stack-machine fallback methods from worker continuations.",
                program.EntryPoint.Source);
            return false;
        }
        if (rootStateMachine.SuspensionPoints.Count != 1 ||
            rootStateMachine.SuspensionPoints[0].Operation != AsyncAwaitOperationKind.WhenAll ||
            !TrySplitRootAtWhenAll(program.EntryPoint, out var rootBefore, out var rootAwait, out var rootAfter) ||
            !TryGetWhenAllTaskSymbol(rootAwait, out var taskListSymbol))
        {
            AddDiagnostic(
                "SS7001",
                "The first Service Broker backend slice supports one entry-point await of Task.WhenAll.",
                program.EntryPoint.Source);
            return false;
        }

        var rootPreAwaitSymbols = rootBefore
            .SelectMany(CollectDeclaredSymbols)
            .Select(symbol => symbol.Id)
            .ToHashSet();
        if (ContainsAnyReturn(rootBefore) || ContainsNestedReturn(rootAfter))
        {
            AddDiagnostic(
                "SS7001",
                "The current Service Broker root handler supports only a top-level return after Task.WhenAll.",
                rootAwait.Source);
            return false;
        }
        var unsupportedRootLocal = rootStateMachine.SuspensionPoints[0].LiveSymbols.FirstOrDefault(symbol =>
            symbol.Id != taskListSymbol.Id && rootPreAwaitSymbols.Contains(symbol.Id));
        if (unsupportedRootLocal is not null)
        {
            AddDiagnostic(
                "SS7004",
                $"Entry local '{unsupportedRootLocal.Name}' is live across Task.WhenAll but is not spilled by the current Service Broker backend.",
                rootAwait.Source);
            return false;
        }

        var entrySymbols = CollectDeclaredSymbols(program.EntryPoint)
            .ToDictionary(symbol => symbol.Id);
        var handlers = new List<ServiceBrokerMethodPlan>();
        foreach (var method in asyncMethods)
        {
            var stateMachine = AsyncStateMachinePlan.Create(method.Name, method);
            if (stateMachine.SuspensionPoints.Count != 1 ||
                stateMachine.SuspensionPoints[0].Operation != AsyncAwaitOperationKind.Delay ||
                !TrySplitMethodAtDelay(method, out var before, out var delay, out var after) ||
                !IsSupportedTaskDelay(delay))
            {
                AddDiagnostic(
                    "SS7002",
                    $"Async method '{method.Name}' currently needs exactly one Task.Delay(int) await.",
                    method.Source);
                return false;
            }
            if (ContainsAnyReturn(before) || ContainsNestedReturn(after))
            {
                AddDiagnostic(
                    "SS7002",
                    $"Async method '{method.Name}' supports only a top-level return after Task.Delay.",
                    method.Source);
                return false;
            }

            var preAwaitLocalIds = before
                .SelectMany(CollectDeclaredSymbols)
                .Select(symbol => symbol.Id)
                .ToHashSet();
            var unsupportedLocal = stateMachine.SuspensionPoints[0].LiveSymbols.FirstOrDefault(symbol =>
                preAwaitLocalIds.Contains(symbol.Id));
            if (unsupportedLocal is not null)
            {
                AddDiagnostic(
                    "SS7004",
                    $"Async local '{unsupportedLocal.Name}' is live across Task.Delay but is not spilled by the current Service Broker backend.",
                    delay.Source);
                return false;
            }

            var referencedSymbols = CollectReferencedSymbols(method);
            var captures = referencedSymbols
                .Where(symbol => entrySymbols.ContainsKey(symbol.Id))
                .GroupBy(symbol => symbol.Id)
                .Select(group => group.First())
                .ToArray();
            var captureIds = captures.Select(capture => capture.Id).ToHashSet();
            if (FindDirectlyMutatedSymbol(method, captureIds) is { } mutatedCapture)
            {
                AddDiagnostic(
                    "SS7004",
                    $"Captured entry local '{mutatedCapture.Name}' is assigned by async method '{method.Name}', but shared closure cells are not implemented yet.",
                    method.Source);
                return false;
            }
            handlers.Add(new ServiceBrokerMethodPlan(
                method,
                ServiceBrokerHandlerName(method, handlers.Count),
                before,
                delay,
                after,
                captures));
        }

        if (!TryFindTaskListCreation(rootBefore, taskListSymbol, handlers, out var taskCreation))
        {
            AddDiagnostic(
                "SS7003",
                "Task.WhenAll currently requires a materialized Select over an async method, for example source.Select(Work).ToList().",
                rootAwait.Source);
            return false;
        }

        plan = new ServiceBrokerProgramPlan(
            rootBefore,
            rootAwait,
            rootAfter,
            taskListSymbol,
            taskCreation,
            handlers);
        return true;
    }

    private void EmitServiceBrokerWorkerProcedure(ServiceBrokerProgramPlan plan, string procedureName)
    {
        _sql.Line($"CREATE OR ALTER PROCEDURE [SharpSql].[{procedureName}]");
        using (_sql.Indent())
        {
            _sql.Line($"{RuntimeExecutionId} UNIQUEIDENTIFIER,");
            _sql.Line("@__sharpsql_task_id BIGINT");
        }
        _sql.Line("AS");
        _sql.Line("BEGIN");
        using (_sql.Indent())
        {
            _sql.Line("SET NOCOUNT ON;");
            // Source-level catch clauses must be able to recover and continue. THROW with
            // XACT_ABORT ON would doom the activation transaction before the catch body runs.
            _sql.Line("SET XACT_ABORT OFF;");
            _sql.Line($"IF @@TRANCOUNT = 0 THROW {ServiceBrokerWorkerTransactionRequiredError}, 'A SharpSql Service Broker worker requires an activation transaction.', 1;");
            _sql.Line("DECLARE @__sharpsql_handler NVARCHAR(450);");
            _sql.Line("DECLARE @__sharpsql_continuation_state INT;");
            _sql.Line("DECLARE @__sharpsql_payload NVARCHAR(MAX);");
            _sql.Line("DECLARE @__sharpsql_claimed TABLE ([HandlerName] NVARCHAR(450), [ContinuationState] INT, [PayloadJson] NVARCHAR(MAX));");
            _sql.Line("UPDATE [SharpSql].[Tasks] WITH (UPDLOCK, ROWLOCK)");
            _sql.Line("SET [State] = 3, [StartedAtUtc] = SYSUTCDATETIME()");
            _sql.Line("OUTPUT INSERTED.[HandlerName], INSERTED.[ContinuationState], INSERTED.[PayloadJson]");
            _sql.Line("INTO @__sharpsql_claimed ([HandlerName], [ContinuationState], [PayloadJson])");
            _sql.Line($"WHERE [ExecutionId] = {RuntimeExecutionId} AND [TaskId] = @__sharpsql_task_id AND [State] = 2;");
            _sql.Line("IF NOT EXISTS (SELECT 1 FROM @__sharpsql_claimed) RETURN;");
            _sql.Line("SELECT @__sharpsql_handler = [HandlerName], @__sharpsql_continuation_state = [ContinuationState], @__sharpsql_payload = [PayloadJson]");
            _sql.Line("FROM @__sharpsql_claimed;");
            _sql.Line();
            _sql.Line("BEGIN TRY");
            using (_sql.Indent())
            {
                _sql.Line($"IF @__sharpsql_handler = N'{ServiceBrokerEntryHandler}'");
                EmitServiceBrokerRootHandler(plan);
                foreach (var handler in plan.Methods)
                {
                    _sql.Line($"ELSE IF @__sharpsql_handler = N'{EscapeSqlString(handler.HandlerName)}'");
                    EmitServiceBrokerMethodHandler(handler);
                }
                _sql.Line("ELSE");
                _sql.Line("BEGIN");
                using (_sql.Indent())
                {
                    _sql.Line($"THROW {ServiceBrokerHandlerMissingError}, 'The SharpSql async handler was not found.', 1;");
                }
                _sql.Line("END;");
            }
            _sql.Line("END TRY");
            _sql.Line("BEGIN CATCH");
            using (_sql.Indent())
            {
                _sql.Line("DECLARE @__sharpsql_error_number INT = ERROR_NUMBER();");
                _sql.Line("DECLARE @__sharpsql_error_message NVARCHAR(MAX) = ERROR_MESSAGE();");
                _sql.Line($"IF @__sharpsql_error_number IN (1205, {ServiceBrokerWorkerDispatcherSqlEmitter.RetryableWorkerDeadlockErrorNumber}) THROW;");
                // CompleteTask writes durable fault state. If the activation transaction
                // was doomed or rolled back by the original error, let the dispatcher
                // unwind it and persist that original error from a fresh transaction.
                _sql.Line("IF XACT_STATE() <> 1 THROW;");
                _sql.Line($"IF @__sharpsql_handler = N'{ServiceBrokerEntryHandler}' THROW;");
                _sql.Line("EXEC [SharpSql].[CompleteTask]");
                using (_sql.Indent())
                {
                    _sql.Line($"@ExecutionId = {RuntimeExecutionId},");
                    _sql.Line("@TaskId = @__sharpsql_task_id,");
                    _sql.Line("@State = 5,");
                    _sql.Line("@ErrorNumber = @__sharpsql_error_number,");
                    _sql.Line("@ErrorMessage = @__sharpsql_error_message;");
                }
            }
            _sql.Line("END CATCH;");
        }
        _sql.Line("END;");
    }

    private void EmitServiceBrokerRootHandler(ServiceBrokerProgramPlan plan)
    {
        _sql.Line("BEGIN");
        using (_sql.Indent())
        {
            _sql.Line("IF @__sharpsql_continuation_state = 0");
            _sql.Line("BEGIN");
            using (_sql.Indent())
            {
                var scope = new VariableScope();
                foreach (var statement in plan.RootBefore)
                {
                    if (statement is ProceduralDeclarationStatement declaration &&
                        declaration.Declaration.Variables.Any(variable => variable.Symbol.Id == plan.TaskListSymbol.Id))
                    {
                        EmitAsyncTaskListDeclaration(declaration, plan.TaskCreation, scope);
                        continue;
                    }
                    EmitStatement(statement, scope, inlineReturn: null, loop: null, namePrefix: "async_root_0");
                }
                EmitWhenAllSuspension(plan, scope);
            }
            _sql.Line("END;");
            _sql.Line("ELSE IF @__sharpsql_continuation_state = 1");
            _sql.Line("BEGIN");
            using (_sql.Indent())
            {
                var scope = new VariableScope();
                var taskIds = _names.Allocate("_async_task_ids");
                _sql.Line($"DECLARE {taskIds} NVARCHAR(MAX) = COALESCE(JSON_QUERY(@__sharpsql_payload, '$.tasks'), N'[]');");
                scope.Add(plan.TaskListSymbol, new AsyncTaskListBinding(
                    plan.TaskListSymbol.Type,
                    taskIds,
                    AsyncResultType(plan.TaskCreation.Handler.Method.ReturnType)));
                EmitAwaitedTaskFaultPropagation(taskIds);
                foreach (var statement in plan.RootAfter)
                {
                    if (statement is ProceduralReturn)
                        break;
                    if (statement is ProceduralDeclarationStatement declaration &&
                        TryEmitTaskResultsDeclaration(declaration, scope))
                        continue;
                    EmitStatement(statement, scope, inlineReturn: null, loop: null, namePrefix: "async_root_1");
                }
                EmitSuccessfulTaskCompletion(IrType.Void, resultSql: null, completeExecution: true);
                _sql.Line("RETURN;");
            }
            _sql.Line("END;");
            _sql.Line($"THROW {ServiceBrokerHandlerMissingError}, 'The SharpSql entry continuation state was not found.', 1;");
        }
        _sql.Line("END;");
    }

    private void EmitAsyncTaskListDeclaration(
        ProceduralDeclarationStatement statement,
        ServiceBrokerTaskCreationPlan creation,
        VariableScope scope)
    {
        var variable = statement.Declaration.Variables.Single(item => item.Symbol.Id == creation.TaskListSymbol.Id);
        EmitVmExpression(creation.Source, scope, context: null, collection =>
        {
            var taskIds = _names.Allocate("_async_task_ids");
            var index = _names.Allocate("_async_task_index");
            var count = _names.Allocate("_async_task_count");
            var item = _names.Allocate("_async_task_item");
            var childPayload = _names.Allocate("_async_child_payload");
            var childTask = _names.Allocate("_async_child_task");
            var delayMilliseconds = _names.Allocate("_async_delay_milliseconds");
            var childErrorNumber = _names.Allocate("_async_child_error_number");
            var childErrorMessage = _names.Allocate("_async_child_error_message");
            var parameter = creation.Handler.Method.Parameters[0];
            var methodScope = scope.Child();
            methodScope.Add(parameter.Symbol, new ScalarVariableBinding(item, parameter.Type));
            var payloadValues = new List<(IrSymbol Symbol, string Sql)>
            {
                (parameter.Symbol, item)
            };
            foreach (var capture in creation.Handler.Captures)
            {
                if (scope.Find(capture) is not ScalarVariableBinding binding)
                {
                    AddDiagnostic(
                        "SS7004",
                        $"Captured async variable '{capture.Name}' is not a durable scalar.",
                        creation.Handler.Method.Source);
                    continue;
                }
                payloadValues.Add((capture, binding.SqlName));
            }
            _sql.Line($"DECLARE {taskIds} NVARCHAR(MAX) = N'[]';");
            _sql.Line($"DECLARE {index} INT = 0;");
            _sql.Line($"DECLARE {count} INT = {SequenceCountSql(collection)};");
            _sql.Line($"DECLARE {item} {parameter.Type.SqlType()};");
            _sql.Line($"DECLARE {childPayload} NVARCHAR(MAX);");
            _sql.Line($"DECLARE {childTask} BIGINT;");
            _sql.Line($"DECLARE {delayMilliseconds} INT;");
            _sql.Line($"DECLARE {childErrorNumber} INT;");
            _sql.Line($"DECLARE {childErrorMessage} NVARCHAR(2048);");
            _sql.Line($"WHILE {index} < {count}");
            _sql.Line("BEGIN");
            using (_sql.Indent())
            {
                _sql.Line($"SET {item} = {SequenceElementSql(collection, index, parameter.Type)};");
                // Calling an async method runs synchronously through its first incomplete
                // await. Allocate its task first so an unhandled prefix error faults only
                // that task, then evaluate each prefix in source enumeration order.
                _sql.Line($"SET {childTask} = NULL;");
                _sql.Line("EXEC [SharpSql].[ScheduleTask]");
                using (_sql.Indent())
                {
                    _sql.Line($"@ExecutionId = {RuntimeExecutionId},");
                    _sql.Line($"@ProgramId = N'{_serviceBrokerProgramId}',");
                    _sql.Line($"@HandlerName = N'{EscapeSqlString(creation.Handler.HandlerName)}',");
                    _sql.Line("@ContinuationState = 0,");
                    _sql.Line("@StartSuspended = 1,");
                    _sql.Line($"@TaskId = {childTask} OUTPUT;");
                }
                _sql.Line("BEGIN TRY");
                using (_sql.Indent())
                {
                    foreach (var beforeDelay in creation.Handler.BeforeDelay)
                    {
                        EmitStatement(
                            beforeDelay,
                            methodScope,
                            inlineReturn: null,
                            loop: null,
                            namePrefix: $"async_{creation.Handler.Method.Name}_start");
                    }
                    var delay = (IrInvocationExpression)creation.Handler.Delay.Operand;
                    EmitVmExpression(delay.Arguments[0], methodScope, context: null, milliseconds =>
                    {
                        _sql.Line($"SET {delayMilliseconds} = {milliseconds};");
                        _sql.Line($"SET {childPayload} = {PayloadJsonSql(payloadValues)};");
                        _sql.Line("EXEC [SharpSql].[SuspendTaskForDelay]");
                        using (_sql.Indent())
                        {
                            _sql.Line($"@ExecutionId = {RuntimeExecutionId},");
                            _sql.Line($"@TaskId = {childTask},");
                            _sql.Line("@ContinuationState = 1,");
                            _sql.Line($"@PayloadJson = {childPayload},");
                            _sql.Line($"@DelayMilliseconds = {delayMilliseconds};");
                        }
                    });
                }
                _sql.Line("END TRY");
                _sql.Line("BEGIN CATCH");
                using (_sql.Indent())
                {
                    _sql.Line($"IF ERROR_NUMBER() IN (1205, {ServiceBrokerWorkerDispatcherSqlEmitter.RetryableWorkerDeadlockErrorNumber}) THROW;");
                    _sql.Line("IF XACT_STATE() <> 1 THROW;");
                    _sql.Line($"SET {childErrorNumber} = ERROR_NUMBER();");
                    _sql.Line($"SET {childErrorMessage} = LEFT(ERROR_MESSAGE(), 2048);");
                    _sql.Line("EXEC [SharpSql].[CompleteTask]");
                    using (_sql.Indent())
                    {
                        _sql.Line($"@ExecutionId = {RuntimeExecutionId},");
                        _sql.Line($"@TaskId = {childTask},");
                        _sql.Line("@State = 5,");
                        _sql.Line($"@ErrorNumber = {childErrorNumber},");
                        _sql.Line($"@ErrorMessage = {childErrorMessage};");
                    }
                }
                _sql.Line("END CATCH;");
                _sql.Line($"SET {taskIds} = JSON_MODIFY({taskIds}, 'append $', {childTask});");
                _sql.Line($"SET {index} = {index} + 1;");
            }
            _sql.Line("END;");
            scope.Add(variable.Symbol, new AsyncTaskListBinding(
                variable.DeclaredType,
                taskIds,
                AsyncResultType(creation.Handler.Method.ReturnType)));
        });
    }

    private void EmitWhenAllSuspension(ServiceBrokerProgramPlan plan, VariableScope scope)
    {
        if (scope.Find(plan.TaskListSymbol) is not AsyncTaskListBinding tasks)
        {
            AddDiagnostic("SS7005", "Task.WhenAll did not resolve to a durable task list.", plan.RootAwait.Source);
            _sql.Line($"THROW {ServiceBrokerHandlerMissingError}, 'Task.WhenAll did not resolve to a durable task list.', 1;");
            return;
        }

        var count = _names.Allocate("_async_dependency_count");
        var payload = _names.Allocate("_async_root_payload");
        var generation = _names.Allocate("_async_suspension_generation");
        var index = _names.Allocate("_async_dependency_index");
        var dependency = _names.Allocate("_async_dependency_task");
        _sql.Line($"DECLARE {count} INT = (SELECT COUNT(*) FROM OPENJSON({tasks.TaskIdsJsonSql}));");
        _sql.Line($"DECLARE {payload} NVARCHAR(MAX) = (SELECT JSON_QUERY({tasks.TaskIdsJsonSql}) AS [tasks] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER);");
        _sql.Line("EXEC [SharpSql].[SuspendTaskForDependencies]");
        using (_sql.Indent())
        {
            _sql.Line($"@ExecutionId = {RuntimeExecutionId},");
            _sql.Line("@TaskId = @__sharpsql_task_id,");
            _sql.Line("@ContinuationState = 1,");
            _sql.Line($"@PayloadJson = {payload},");
            _sql.Line($"@ExpectedDependencyCount = {count};");
        }
        _sql.Line($"DECLARE {generation} INT = (SELECT [SuspensionGeneration] FROM [SharpSql].[Tasks] WHERE [ExecutionId] = {RuntimeExecutionId} AND [TaskId] = @__sharpsql_task_id);");
        _sql.Line($"DECLARE {index} INT = 0;");
        _sql.Line($"DECLARE {dependency} BIGINT;");
        _sql.Line($"WHILE {index} < {count}");
        _sql.Line("BEGIN");
        using (_sql.Indent())
        {
            _sql.Line($"SELECT {dependency} = CONVERT(BIGINT, [value]) FROM OPENJSON({tasks.TaskIdsJsonSql}) WHERE CONVERT(INT, [key]) = {index};");
            _sql.Line("EXEC [SharpSql].[RegisterTaskDependency]");
            using (_sql.Indent())
            {
                _sql.Line($"@ExecutionId = {RuntimeExecutionId},");
                _sql.Line("@ContinuationTaskId = @__sharpsql_task_id,");
                _sql.Line($"@DependencyTaskId = {dependency},");
                _sql.Line($"@SuspensionGeneration = {generation};");
            }
            _sql.Line($"SET {index} = {index} + 1;");
        }
        _sql.Line("END;");
        _sql.Line("RETURN;");
    }

    private bool TryEmitTaskResultsDeclaration(
        ProceduralDeclarationStatement declaration,
        VariableScope scope)
    {
        if (declaration.Declaration.Variables.Count != 1)
            return false;
        var variable = declaration.Declaration.Variables[0];
        if (variable.Initializer is not IrInvocationExpression
            {
                Target: IrMemberExpression
                {
                    Receiver: IrVariableExpression tasksVariable,
                    MemberName: "Select"
                },
                Arguments: [IrLambdaExpression
                {
                    Parameters.Count: 1,
                    ExpressionBody: IrMemberExpression { MemberName: "Result" }
                }]
            } || scope.Find(tasksVariable.Symbol) is not AsyncTaskListBinding tasks)
            return false;

        scope.Add(variable.Symbol, new QueryVariableBinding(
            variable.DeclaredType,
            new SqlLinqQueryPlan(
                new SqlLinqTaskResultQuerySource(tasks.TaskIdsJsonSql, RuntimeExecutionId),
                tasks.ResultType,
                tasks.ResultType,
                [])));
        return true;
    }

    private void EmitAwaitedTaskFaultPropagation(string taskIds)
    {
        var state = _names.Allocate("_async_child_state");
        var errorNumber = _names.Allocate("_async_child_error_number");
        var errorMessage = _names.Allocate("_async_child_error_message");
        _sql.Line($"DECLARE {state} TINYINT;");
        _sql.Line($"DECLARE {errorNumber} INT;");
        _sql.Line($"DECLARE {errorMessage} NVARCHAR(2048);");
        _sql.Line("SELECT TOP (1)");
        using (_sql.Indent())
        {
            _sql.Line($"{state} = [task].[State],");
            _sql.Line($"{errorNumber} = [task].[ErrorNumber],");
            _sql.Line($"{errorMessage} = LEFT(COALESCE([task].[ErrorMessage], N'An asynchronous task failed.'), 2048)");
        }
        _sql.Line($"FROM OPENJSON({taskIds}) AS [task_id]");
        _sql.Line($"INNER JOIN [SharpSql].[Tasks] AS [task] ON [task].[ExecutionId] = {RuntimeExecutionId} AND [task].[TaskId] = CONVERT(BIGINT, [task_id].[value])");
        _sql.Line("WHERE [task].[State] IN (5, 6)");
        _sql.Line("ORDER BY CONVERT(INT, [task_id].[key]);");
        _sql.Line($"IF {state} = 6 THROW {ServiceBrokerCanceledError}, 'An asynchronous task was canceled.', 1;");
        _sql.Line($"IF {errorNumber} >= 50000 THROW {errorNumber}, {errorMessage}, 1;");
        _sql.Line($"IF {errorNumber} IS NOT NULL RAISERROR({errorMessage}, 16, 1);");
        _sql.Line($"IF {state} = 5 THROW {ServiceBrokerUnhandledDatabaseError}, {errorMessage}, 1;");
    }

    private void EmitServiceBrokerMethodHandler(ServiceBrokerMethodPlan handler)
    {
        _sql.Line("BEGIN");
        using (_sql.Indent())
        {
            _sql.Line("IF @__sharpsql_continuation_state = 0");
            _sql.Line("BEGIN");
            using (_sql.Indent())
            {
                var scope = EmitPayloadBindings(handler.PayloadSymbols);
                foreach (var statement in handler.BeforeDelay)
                    EmitStatement(statement, scope, inlineReturn: null, loop: null, namePrefix: $"async_{handler.Method.Name}_0");
                var delay = (IrInvocationExpression)handler.Delay.Operand;
                var delayArgument = delay.Arguments[0];
                EmitVmExpression(delayArgument, scope, context: null, milliseconds =>
                {
                    var payload = _names.Allocate("_async_method_payload");
                    var delayMilliseconds = _names.Allocate("_async_delay_milliseconds");
                    _sql.Line($"DECLARE {delayMilliseconds} INT = {milliseconds};");
                    _sql.Line($"DECLARE {payload} NVARCHAR(MAX) = {PayloadJsonSql(handler.PayloadSymbols.Select(symbol => (symbol, PayloadValueSql(scope, symbol))).ToArray())};");
                    _sql.Line("EXEC [SharpSql].[SuspendTaskForDelay]");
                    using (_sql.Indent())
                    {
                        _sql.Line($"@ExecutionId = {RuntimeExecutionId},");
                        _sql.Line("@TaskId = @__sharpsql_task_id,");
                        _sql.Line("@ContinuationState = 1,");
                        _sql.Line($"@PayloadJson = {payload},");
                        _sql.Line($"@DelayMilliseconds = {delayMilliseconds};");
                    }
                    _sql.Line("RETURN;");
                });
            }
            _sql.Line("END;");
            _sql.Line("ELSE IF @__sharpsql_continuation_state = 1");
            _sql.Line("BEGIN");
            using (_sql.Indent())
            {
                var scope = EmitPayloadBindings(handler.PayloadSymbols);
                var resultType = AsyncResultType(handler.Method.ReturnType);
                string? result = null;
                if (resultType.Name != "void")
                {
                    result = _names.Allocate($"_async_{handler.Method.Name}_result");
                    _sql.Line($"DECLARE {result} {resultType.SqlType()};");
                }

                var returned = false;
                foreach (var statement in handler.AfterDelay)
                {
                    if (statement is ProceduralReturn @return)
                    {
                        returned = true;
                        if (@return.Expression is not null && result is not null)
                            EmitVmExpression(@return.Expression, scope, context: null, value => _sql.Line($"SET {result} = {value};"));
                        break;
                    }
                    EmitStatement(statement, scope, inlineReturn: null, loop: null, namePrefix: $"async_{handler.Method.Name}_1");
                }
                if (resultType.Name != "void" && !returned)
                    _sql.Line($"THROW {ServiceBrokerHandlerMissingError}, 'The async handler did not produce a result.', 1;");
                else
                {
                    EmitSuccessfulTaskCompletion(resultType, result, completeExecution: false);
                    _sql.Line("RETURN;");
                }
            }
            _sql.Line("END;");
            _sql.Line($"THROW {ServiceBrokerHandlerMissingError}, 'The async method continuation state was not found.', 1;");
        }
        _sql.Line("END;");
    }

    private VariableScope EmitPayloadBindings(IReadOnlyList<IrSymbol> symbols)
    {
        var scope = new VariableScope();
        var bindings = new List<(IrSymbol Symbol, string Sql)>();
        foreach (var symbol in symbols)
        {
            var sqlName = _names.Allocate($"_async_{symbol.Name}");
            _sql.Line($"DECLARE {sqlName} {symbol.Type.SqlType()};");
            scope.Add(symbol, new ScalarVariableBinding(sqlName, symbol.Type));
            bindings.Add((symbol, sqlName));
        }
        if (bindings.Count > 0)
        {
            _sql.Line("SELECT");
            using (_sql.Indent())
            {
                for (var index = 0; index < bindings.Count; index++)
                {
                    var binding = bindings[index];
                    _sql.Line($"{binding.Sql} = [payload].[{EscapeSqlIdentifier(binding.Symbol.Name)}]{(index + 1 == bindings.Count ? string.Empty : ",")}");
                }
            }
            _sql.Line("FROM OPENJSON(@__sharpsql_payload) WITH (");
            using (_sql.Indent())
            {
                for (var index = 0; index < bindings.Count; index++)
                {
                    var binding = bindings[index];
                    _sql.Line($"[{EscapeSqlIdentifier(binding.Symbol.Name)}] {binding.Symbol.Type.SqlType()} '$.{EscapeJsonPathName(binding.Symbol.Name)}'{(index + 1 == bindings.Count ? string.Empty : ",")}");
                }
            }
            _sql.Line(") AS [payload];");
        }
        return scope;
    }

    private void EmitSuccessfulTaskCompletion(IrType resultType, string? resultSql, bool completeExecution)
    {
        _sql.Line("EXEC [SharpSql].[CompleteTask]");
        using (_sql.Indent())
        {
            _sql.Line($"@ExecutionId = {RuntimeExecutionId},");
            _sql.Line("@TaskId = @__sharpsql_task_id,");
            _sql.Line("@State = 4" + (resultSql is null ? ";" : ","));
            if (resultSql is not null)
            {
                var kind = TaskResultKind(resultType);
                _sql.Line($"@ResultKind = {kind},");
                var parameter = kind switch
                {
                    2 => "@ResultText",
                    3 => "@ResultBinary",
                    4 => "@ResultReferenceId",
                    _ => "@ResultScalar"
                };
                _sql.Line($"{parameter} = {resultSql};");
            }
        }
        if (!completeExecution)
            return;
        _sql.Line("EXEC [SharpSql].[CompleteExecution]");
        using (_sql.Indent())
        {
            _sql.Line($"@ExecutionId = {RuntimeExecutionId},");
            _sql.Line("@State = 2;");
        }
    }

    private void EmitServiceBrokerLauncher(string programId)
    {
        _sql.Line("-- The entry connection orchestrates workers, drains output, and pumps due timers.");
        _sql.Line("IF OBJECT_ID(N'[SharpSql].[Tasks]', N'U') IS NULL OR OBJECT_ID(N'[SharpSql].[ScheduleTask]', N'P') IS NULL");
        using (_sql.Indent())
            _sql.Line($"THROW {ServiceBrokerProgramMissingError}, 'Run SharpSqlServiceBrokerRuntime.GenerateProvisioningSql() before executing an async program.', 1;");
        _sql.Line("DECLARE @__sharpsql_lease_id UNIQUEIDENTIFIER;");
        _sql.Line($"EXEC [SharpSql].[{ExecutionInfrastructureSqlEmitter.StartExecutionProcedureName}]");
        using (_sql.Indent())
        {
            _sql.Line($"@ExecutionId = {RuntimeExecutionId},");
            _sql.Line($"@ProgramId = N'{programId}',");
            _sql.Line("@LeaseDurationSeconds = 30,");
            _sql.Line("@LeaseId = @__sharpsql_lease_id OUTPUT;");
        }
        _sql.Line("DECLARE @__sharpsql_root_task_id BIGINT;");
        _sql.Line("EXEC [SharpSql].[ScheduleTask]");
        using (_sql.Indent())
        {
            _sql.Line($"@ExecutionId = {RuntimeExecutionId},");
            _sql.Line($"@ProgramId = N'{programId}',");
            _sql.Line($"@HandlerName = N'{ServiceBrokerEntryHandler}',");
            _sql.Line("@TaskId = @__sharpsql_root_task_id OUTPUT;");
        }
        _sql.Line("DECLARE @__sharpsql_execution_state TINYINT = 1;");
        _sql.Line("DECLARE @__sharpsql_next_output_sequence BIGINT;");
        _sql.Line("DECLARE @__sharpsql_output_text NVARCHAR(MAX);");
        _sql.Line("DECLARE @__sharpsql_drained_output TABLE ([SequenceNumber] BIGINT NOT NULL PRIMARY KEY);");
        _sql.Line("DECLARE @__sharpsql_next_heartbeat_at DATETIME2(7) = DATEADD(SECOND, 5, SYSUTCDATETIME());");
        _sql.Line("DECLARE @__sharpsql_lease_renewed BIT;");
        _sql.Line("WHILE @__sharpsql_execution_state NOT IN (2, 3, 4)");
        _sql.Line("BEGIN");
        using (_sql.Indent())
        {
            _sql.Line("BEGIN TRY");
            using (_sql.Indent())
                _sql.Line("EXEC [SharpSql].[ClaimDueContinuations] @BatchSize = 100;");
            _sql.Line("END TRY");
            _sql.Line("BEGIN CATCH");
            using (_sql.Indent())
                _sql.Line("IF ERROR_NUMBER() <> 1205 THROW;");
            _sql.Line("END CATCH;");
            _sql.Line("IF SYSUTCDATETIME() >= @__sharpsql_next_heartbeat_at");
            _sql.Line("BEGIN");
            using (_sql.Indent())
            {
                _sql.Line("SET @__sharpsql_lease_renewed = 0;");
                _sql.Line($"EXEC [SharpSql].[{ExecutionInfrastructureSqlEmitter.HeartbeatExecutionProcedureName}]");
                using (_sql.Indent())
                {
                    _sql.Line($"@ExecutionId = {RuntimeExecutionId},");
                    _sql.Line("@LeaseId = @__sharpsql_lease_id,");
                    _sql.Line("@LeaseDurationSeconds = 30,");
                    _sql.Line("@Renewed = @__sharpsql_lease_renewed OUTPUT;");
                }
                _sql.Line("SET @__sharpsql_next_heartbeat_at = DATEADD(SECOND, 5, SYSUTCDATETIME());");
            }
            _sql.Line("END;");
            EmitServiceBrokerOutputDrain();
            _sql.Line("SET @__sharpsql_execution_state = NULL;");
            _sql.Line($"SELECT @__sharpsql_execution_state = [State] FROM [SharpSql].[Executions] WHERE [ExecutionId] = {RuntimeExecutionId};");
            _sql.Line($"IF @__sharpsql_execution_state IS NULL THROW {ServiceBrokerExecutionLostError}, 'The SharpSql execution row disappeared while awaiting completion.', 1;");
            _sql.Line("IF @__sharpsql_execution_state NOT IN (2, 3, 4) WAITFOR DELAY '00:00:00.050';");
        }
        _sql.Line("END;");
        // Completion and the last output writes normally become visible together, but
        // activated worker commits and client message delivery can cross the launcher's
        // first terminal read under load. Require a short bounded quiescence window
        // before execution cleanup so committed tail output is not lost.
        _sql.Line("DECLARE @__sharpsql_terminal_drain_pass INT = 0;");
        _sql.Line("WHILE @__sharpsql_terminal_drain_pass < 3");
        _sql.Line("BEGIN");
        using (_sql.Indent())
        {
            EmitServiceBrokerOutputDrain();
            _sql.Line("SET @__sharpsql_terminal_drain_pass += 1;");
            _sql.Line("IF @__sharpsql_terminal_drain_pass < 3 WAITFOR DELAY '00:00:00.050';");
        }
        _sql.Line("END;");
        _sql.Line("IF @__sharpsql_execution_state = 3");
        _sql.Line("BEGIN");
        using (_sql.Indent())
        {
            _sql.Line("DECLARE @__sharpsql_execution_error_number INT;");
            _sql.Line("DECLARE @__sharpsql_execution_error_message NVARCHAR(2048);");
            _sql.Line("SELECT @__sharpsql_execution_error_number = [ErrorNumber], @__sharpsql_execution_error_message = LEFT(COALESCE([ErrorMessage], N'The asynchronous execution failed.'), 2048)");
            _sql.Line($"FROM [SharpSql].[Executions] WHERE [ExecutionId] = {RuntimeExecutionId};");
            _sql.Line("IF @__sharpsql_execution_error_number >= 50000 THROW @__sharpsql_execution_error_number, @__sharpsql_execution_error_message, 1;");
            _sql.Line($"THROW {ServiceBrokerUnhandledDatabaseError}, @__sharpsql_execution_error_message, 1;");
        }
        _sql.Line("END;");
        _sql.Line($"IF @__sharpsql_execution_state = 4 THROW {ServiceBrokerCanceledError}, 'The asynchronous execution was canceled.', 1;");
    }

    private void EmitServiceBrokerOutputDrain()
    {
        _sql.Line("WHILE 1 = 1");
        _sql.Line("BEGIN");
        using (_sql.Indent())
        {
            _sql.Line("SET @__sharpsql_next_output_sequence = NULL;");
            _sql.Line("SET @__sharpsql_output_text = NULL;");
            _sql.Line("SELECT TOP (1) @__sharpsql_next_output_sequence = [SequenceNumber], @__sharpsql_output_text = [OutputText]");
            // Sequence values are allocated outside the worker transaction and can
            // therefore become visible out of order. Remember individual drained rows
            // so a late commit with a lower value is never skipped, without mutating the
            // durable table while workers are still inserting into it.
            _sql.Line($"FROM [SharpSql].[OutputEvents] AS [output] WHERE [ExecutionId] = {RuntimeExecutionId}");
            using (_sql.Indent())
                _sql.Line("AND NOT EXISTS (SELECT 1 FROM @__sharpsql_drained_output AS [drained] WHERE [drained].[SequenceNumber] = [output].[SequenceNumber])");
            _sql.Line("ORDER BY [output].[SequenceNumber];");
            _sql.Line("IF @__sharpsql_next_output_sequence IS NULL BREAK;");
            // PRINT can buffer several kilobytes before sending an InfoMessage. A constant
            // format string keeps user '%' characters safe while NOWAIT streams ordinary
            // lines. Keep PRINT's larger payload limit rather than truncating long lines.
            _sql.Line("IF DATALENGTH(@__sharpsql_output_text) <= 4000");
            using (_sql.Indent())
                _sql.Line("RAISERROR(N'%s', 0, 1, @__sharpsql_output_text) WITH NOWAIT;");
            _sql.Line("ELSE");
            using (_sql.Indent())
                _sql.Line("PRINT @__sharpsql_output_text;");
            _sql.Line("INSERT INTO @__sharpsql_drained_output ([SequenceNumber]) VALUES (@__sharpsql_next_output_sequence);");
        }
        _sql.Line("END;");
    }

    private void EmitServiceBrokerRegistryCleanup()
    {
        if (_serviceBrokerProgramEmitted)
            _sql.Line($"DELETE FROM [SharpSql].[Executions] WHERE [ExecutionId] = {RuntimeExecutionId};");
    }

    private string CaptureServiceBrokerWorkerSql(Action emit)
    {
        var outerWriter = _sql;
        var outerWorker = _emittingServiceBrokerWorker;
        _sql = new SqlWriter();
        _emittingServiceBrokerWorker = true;
        try
        {
            emit();
            return _sql.ToString();
        }
        finally
        {
            _sql = outerWriter;
            _emittingServiceBrokerWorker = outerWorker;
        }
    }

    private string ComputeServiceBrokerProgramId(IrProgram program)
    {
        var identity = new StringBuilder();
        identity.Append("abi|").Append(ServiceBrokerProgramAbi).Append('\n');
        identity.Append("compiler|")
            .Append(typeof(SharpSqlCompiler).Assembly.GetName().Version?.ToString() ?? "unknown")
            .Append('\n');
        identity.Append("options|")
            .Append(_options.MaxInlineStatements).Append('|')
            .Append(_options.MaxInlineCallSites).Append('|')
            .Append(_options.EmitNoCount).Append('|')
            .Append(_options.EmitRuntimeDiagnostics).Append('|')
            .Append((int)_effectiveRuntime.Execution).Append('|')
            .Append((int)_effectiveRuntime.Durability).Append('|')
            .Append(_effectiveRuntime.UseMemoryOptimizedTables)
            .Append('\n');
        if (_compilation is not null)
        {
            foreach (var tree in _compilation.SyntaxTrees.OrderBy(tree => tree.FilePath, StringComparer.Ordinal))
                identity.Append(tree.FilePath).Append('\n').Append(tree.GetText().ToString()).Append('\n');
        }

        identity.Append("entry|")
            .Append(program.EntryPoint.Source.Span.Start).Append('|')
            .Append(program.EntryPoint.Source.Span.Length)
            .Append('\n');
        identity.Append("entry-symbol|")
            .Append(_selectedEntryPointIdentity ?? "<ir-entry>")
            .Append('\n');
        foreach (var method in program.Methods.OrderBy(method => method.Id.Value, StringComparer.Ordinal))
        {
            identity.Append("method|")
                .Append(method.Id.Value).Append('|')
                .Append(method.Source.Span.Start).Append('|')
                .Append(method.Source.Span.Length).Append('|')
                .Append(method.IsAsync).Append('|')
                .Append(method.ReturnType.Name)
                .Append('\n');
        }
        foreach (var type in program.HeapTypes.OrderBy(type => type.Id.Value, StringComparer.Ordinal))
        {
            identity.Append("type|")
                .Append(type.Id.Value).Append('|')
                .Append(type.Name).Append('|')
                .Append(type.Source.Span.Start).Append('|')
                .Append(type.Source.Span.Length)
                .Append('\n');
        }

        using var algorithm = SHA256.Create();
        var hash = algorithm.ComputeHash(Encoding.UTF8.GetBytes(identity.ToString()));
        return string.Concat(hash.Take(16).Select(value => value.ToString("x2", CultureInfo.InvariantCulture)));
    }

    private static string ServiceBrokerProcedureName(string programId) => $"Program_{programId}";

    private static string ServiceBrokerHandlerName(MethodDefinition method, int index) =>
        $"method_{index}_{method.Name}";

    private static bool IsSupportedTaskDelay(IrAwaitExpression delay)
    {
        if (delay.Operand is not IrInvocationExpression { Arguments.Count: 1 } invocation)
            return false;
        if (invocation.TargetMethodId.Value == "M:System.Threading.Tasks.Task.Delay(System.Int32)")
            return true;
        return invocation.TargetMethodId.IsNone &&
               invocation.Arguments[0].Facts.Type.Name == IrType.Int.Name &&
               invocation.Target is IrMemberExpression
               {
                   MemberName: "Delay",
                   Receiver: IrVariableExpression { Symbol.Name: "Task" }
               };
    }

    private static IrType AsyncResultType(IrType taskType) =>
        taskType.Name.StartsWith("Task<", StringComparison.Ordinal) && GenericArguments(taskType.Name) is [var result]
            ? result
            : IrType.Void;

    private static int TaskResultKind(IrType type) =>
        type.IsString ? 2 : type.Name == "byte[]" ? 3 : type.IsReference ? 4 : 1;

    private static string PayloadJsonSql(IReadOnlyList<(IrSymbol Symbol, string Sql)> values)
    {
        if (values.Count == 0)
            return "N'{}'";
        return "(SELECT " + string.Join(", ", values.Select(value =>
            $"{value.Sql} AS [{EscapeSqlIdentifier(value.Symbol.Name)}]")) +
            " FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES)";
    }

    private static string PayloadValueSql(VariableScope scope, IrSymbol symbol) =>
        scope.Find(symbol) is ScalarVariableBinding binding
            ? binding.SqlName
            : throw new InvalidOperationException($"Async payload symbol '{symbol.Name}' has no scalar binding.");

    private static string EscapeSqlIdentifier(string name) =>
        name.Replace("]", "]]", StringComparison.Ordinal);

    private static string EscapeJsonPathName(string name) =>
        name.All(character => char.IsLetterOrDigit(character) || character == '_')
            ? name
            : $"\"{name.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";

    private bool TryResolveAsyncMethodReference(IrExpression expression, out MethodDefinition method)
    {
        method = null!;
        var methodId = expression switch
        {
            IrVariableExpression variable => variable.Symbol.ReferencedMethodId,
            IrMemberExpression member => member.ReferencedMethodId,
            _ => IrMethodId.None
        };
        return !methodId.IsNone && _methods.TryGetValue(methodId, out method) && method.IsAsync;
    }

    private static bool TrySplitRootAtWhenAll(
        ProceduralBlock root,
        out IReadOnlyList<ProceduralStatement> before,
        out IrAwaitExpression awaitExpression,
        out IReadOnlyList<ProceduralStatement> after)
    {
        for (var index = 0; index < root.Statements.Count; index++)
        {
            if (root.Statements[index] is not ProceduralExpressionStatement
                {
                    Expression: IrAwaitExpression
                    {
                        Operand: IrInvocationExpression { MethodName: "WhenAll" }
                    } candidate
                })
                continue;
            before = root.Statements.Take(index).ToArray();
            awaitExpression = candidate;
            after = root.Statements.Skip(index + 1).ToArray();
            return true;
        }
        before = [];
        awaitExpression = null!;
        after = [];
        return false;
    }

    private static bool TryGetWhenAllTaskSymbol(IrAwaitExpression awaitExpression, out IrSymbol symbol)
    {
        if (awaitExpression.Operand is IrInvocationExpression
            {
                Arguments: [IrVariableExpression variable]
            })
        {
            symbol = variable.Symbol;
            return true;
        }
        symbol = null!;
        return false;
    }

    private static bool TrySplitMethodAtDelay(
        MethodDefinition method,
        out IReadOnlyList<ProceduralStatement> before,
        out IrAwaitExpression delay,
        out IReadOnlyList<ProceduralStatement> after)
    {
        before = [];
        delay = null!;
        after = [];
        if (method.Body is null)
            return false;

        for (var index = 0; index < method.Body.Statements.Count; index++)
        {
            var statement = method.Body.Statements[index];
            if (statement is ProceduralExpressionStatement
                {
                    Expression: IrAwaitExpression
                    {
                        Operand: IrInvocationExpression { MethodName: "Delay" }
                    } directDelay
                })
            {
                before = method.Body.Statements.Take(index).ToArray();
                delay = directDelay;
                after = method.Body.Statements.Skip(index + 1).ToArray();
                return true;
            }

            if (statement is not ProceduralTry @try)
                continue;
            for (var tryIndex = 0; tryIndex < @try.Body.Statements.Count; tryIndex++)
            {
                if (@try.Body.Statements[tryIndex] is not ProceduralExpressionStatement
                    {
                        Expression: IrAwaitExpression
                        {
                            Operand: IrInvocationExpression { MethodName: "Delay" }
                        } tryDelay
                    })
                    continue;
                if (tryIndex != 0)
                    return false;
                before = method.Body.Statements.Take(index).ToArray();
                delay = tryDelay;
                var resumedTry = @try with
                {
                    Body = @try.Body with { Statements = @try.Body.Statements.Skip(tryIndex + 1).ToArray() }
                };
                after = new ProceduralStatement[] { resumedTry }
                    .Concat(method.Body.Statements.Skip(index + 1))
                    .ToArray();
                return true;
            }
        }
        return false;
    }

    private bool TryFindTaskListCreation(
        IReadOnlyList<ProceduralStatement> statements,
        IrSymbol taskListSymbol,
        IReadOnlyList<ServiceBrokerMethodPlan> handlers,
        out ServiceBrokerTaskCreationPlan creation)
    {
        foreach (var declaration in statements.OfType<ProceduralDeclarationStatement>())
        {
            foreach (var variable in declaration.Declaration.Variables)
            {
                if (variable.Symbol.Id != taskListSymbol.Id ||
                    variable.Initializer is not IrInvocationExpression
                    {
                        Target: IrMemberExpression
                        {
                            Receiver: IrInvocationExpression
                            {
                                Target: IrMemberExpression
                                {
                                    Receiver: var source,
                                    MemberName: "Select"
                                },
                                Arguments: [var selector]
                            },
                            MemberName: "ToList"
                        },
                        Arguments.Count: 0
                    } || !TryResolveAsyncMethodReference(selector, out var method))
                    continue;
                var handler = handlers.SingleOrDefault(candidate => candidate.Method.Id == method.Id);
                if (handler is null || handler.Method.Parameters.Count != 1)
                    continue;
                creation = new ServiceBrokerTaskCreationPlan(taskListSymbol, source, handler);
                return true;
            }
        }
        creation = null!;
        return false;
    }

    private static IReadOnlyList<IrSymbol> CollectDeclaredSymbols(ProceduralStatement statement)
    {
        var symbols = new List<IrSymbol>();
        Visit(statement);
        return symbols;

        void Visit(ProceduralStatement current)
        {
            switch (current)
            {
                case ProceduralBlock block:
                    foreach (var child in block.Statements) Visit(child);
                    break;
                case ProceduralDeclarationStatement declaration:
                    symbols.AddRange(declaration.Declaration.Variables.Select(variable => variable.Symbol));
                    break;
                case ProceduralFor { Declaration: not null } @for:
                    symbols.AddRange(@for.Declaration.Variables.Select(variable => variable.Symbol));
                    Visit(@for.Body);
                    break;
                case ProceduralForEach forEach:
                    symbols.Add(forEach.Element);
                    Visit(forEach.Body);
                    break;
                case ProceduralIf @if:
                    Visit(@if.Then);
                    if (@if.Else is not null) Visit(@if.Else);
                    break;
                case ProceduralWhile @while:
                    Visit(@while.Body);
                    break;
                case ProceduralDo @do:
                    Visit(@do.Body);
                    break;
                case ProceduralTry @try:
                    Visit(@try.Body);
                    foreach (var @catch in @try.Catches)
                    {
                        if (@catch.Exception is not null) symbols.Add(@catch.Exception);
                        Visit(@catch.Body);
                    }
                    break;
            }
        }
    }

    private static bool ContainsNestedReturn(IReadOnlyList<ProceduralStatement> statements) =>
        statements.Any(statement => statement is not ProceduralReturn && ContainsAnyReturn(statement));

    private static bool ContainsAnyReturn(IEnumerable<ProceduralStatement> statements) =>
        statements.Any(ContainsAnyReturn);

    private static bool ContainsAnyReturn(ProceduralStatement statement) =>
        statement switch
        {
            ProceduralReturn => true,
            ProceduralBlock block => ContainsAnyReturn(block.Statements),
            ProceduralIf @if => ContainsAnyReturn(@if.Then) ||
                                @if.Else is not null && ContainsAnyReturn(@if.Else),
            ProceduralWhile @while => ContainsAnyReturn(@while.Body),
            ProceduralDo @do => ContainsAnyReturn(@do.Body),
            ProceduralFor @for => ContainsAnyReturn(@for.Body),
            ProceduralForEach forEach => ContainsAnyReturn(forEach.Body),
            ProceduralTry @try => ContainsAnyReturn(@try.Body) ||
                                  @try.Catches.Any(@catch => ContainsAnyReturn(@catch.Body)),
            _ => false
        };

    private static IReadOnlyList<IrSymbol> CollectReferencedSymbols(MethodDefinition method)
    {
        var symbols = new List<IrSymbol>();
        if (method.Body is not null)
            VisitStatement(method.Body);
        if (method.ExpressionBody is not null)
            VisitExpression(method.ExpressionBody);
        return symbols;

        void VisitStatement(ProceduralStatement statement)
        {
            switch (statement)
            {
                case ProceduralBlock block:
                    foreach (var child in block.Statements) VisitStatement(child);
                    break;
                case ProceduralDeclarationStatement declaration:
                    foreach (var variable in declaration.Declaration.Variables)
                        if (variable.Initializer is not null) VisitExpression(variable.Initializer);
                    break;
                case ProceduralExpressionStatement expression: VisitExpression(expression.Expression); break;
                case ProceduralIf @if:
                    VisitExpression(@if.Condition); VisitStatement(@if.Then);
                    if (@if.Else is not null) VisitStatement(@if.Else);
                    break;
                case ProceduralWhile @while: VisitExpression(@while.Condition); VisitStatement(@while.Body); break;
                case ProceduralDo @do: VisitStatement(@do.Body); VisitExpression(@do.Condition); break;
                case ProceduralFor @for:
                    if (@for.Declaration is not null)
                        foreach (var variable in @for.Declaration.Variables)
                            if (variable.Initializer is not null) VisitExpression(variable.Initializer);
                    foreach (var expression in @for.Initializers) VisitExpression(expression);
                    if (@for.Condition is not null) VisitExpression(@for.Condition);
                    VisitStatement(@for.Body);
                    foreach (var expression in @for.Incrementors) VisitExpression(expression);
                    break;
                case ProceduralForEach forEach: VisitExpression(forEach.SourceExpression); VisitStatement(forEach.Body); break;
                case ProceduralTry @try:
                    VisitStatement(@try.Body);
                    foreach (var @catch in @try.Catches)
                    {
                        if (@catch.Filter is not null) VisitExpression(@catch.Filter);
                        VisitStatement(@catch.Body);
                    }
                    break;
                case ProceduralThrow { Expression: not null } @throw: VisitExpression(@throw.Expression); break;
                case ProceduralReturn { Expression: not null } @return: VisitExpression(@return.Expression); break;
            }
        }

        void VisitExpression(IrExpression expression)
        {
            if (expression is IrVariableExpression variable)
            {
                symbols.Add(variable.Symbol);
                return;
            }
            switch (expression)
            {
                case IrBinaryExpression binary: VisitExpression(binary.Left); VisitExpression(binary.Right); break;
                case IrUnaryExpression unary: VisitExpression(unary.Operand); break;
                case IrConversionExpression conversion: VisitExpression(conversion.Operand); break;
                case IrAwaitExpression awaitExpression: VisitExpression(awaitExpression.Operand); break;
                case IrConditionalExpression conditional:
                    VisitExpression(conditional.Condition); VisitExpression(conditional.WhenTrue); VisitExpression(conditional.WhenFalse); break;
                case IrMemberExpression member: VisitExpression(member.Receiver); break;
                case IrElementExpression element:
                    VisitExpression(element.Receiver); foreach (var argument in element.Arguments) VisitExpression(argument); break;
                case IrInvocationExpression invocation:
                    VisitExpression(invocation.Target); foreach (var argument in invocation.Arguments) VisitExpression(argument); break;
                case IrObjectCreationExpression creation:
                    foreach (var argument in creation.Arguments) VisitExpression(argument);
                    foreach (var initializer in creation.Initializers) VisitExpression(initializer);
                    break;
                case IrWithExpression withExpression:
                    VisitExpression(withExpression.Receiver); foreach (var initializer in withExpression.Initializers) VisitExpression(initializer); break;
                case IrArrayCreationExpression array:
                    if (array.Length is not null) VisitExpression(array.Length);
                    foreach (var item in array.Elements) VisitExpression(item);
                    break;
                case IrInterpolatedStringExpression interpolated:
                    foreach (var item in interpolated.Parts.OfType<IrInterpolation>()) VisitExpression(item.Expression);
                    break;
                case IrAssignmentExpression assignment: VisitExpression(assignment.Target); VisitExpression(assignment.Value); break;
                case IrLambdaExpression lambda:
                    if (lambda.ExpressionBody is not null) VisitExpression(lambda.ExpressionBody);
                    if (lambda.StatementBody is not null) VisitStatement(lambda.StatementBody);
                    break;
                case IrQueryExpression query:
                    VisitExpression(query.SourceExpression);
                    foreach (var clause in query.Clauses)
                    {
                        if (clause is IrWhereClause where) VisitExpression(where.Predicate);
                        else if (clause is IrOrderClause order) VisitExpression(order.Key);
                        else if (clause is IrSelectClause select) VisitExpression(select.Projection);
                        else if (clause is IrGroupClause group) { VisitExpression(group.Element); VisitExpression(group.Key); }
                    }
                    break;
            }
        }
    }

    private static IrSymbol? FindDirectlyMutatedSymbol(
        MethodDefinition method,
        ISet<IrSymbolId> candidateIds)
    {
        IrSymbol? mutated = null;
        if (method.Body is not null)
            VisitStatement(method.Body);
        if (mutated is null && method.ExpressionBody is not null)
            VisitExpression(method.ExpressionBody);
        return mutated;

        void VisitStatement(ProceduralStatement statement)
        {
            if (mutated is not null)
                return;
            switch (statement)
            {
                case ProceduralBlock block:
                    foreach (var child in block.Statements) VisitStatement(child);
                    break;
                case ProceduralDeclarationStatement declaration:
                    foreach (var variable in declaration.Declaration.Variables)
                        if (variable.Initializer is not null) VisitExpression(variable.Initializer);
                    break;
                case ProceduralExpressionStatement expression: VisitExpression(expression.Expression); break;
                case ProceduralIf @if:
                    VisitExpression(@if.Condition); VisitStatement(@if.Then);
                    if (@if.Else is not null) VisitStatement(@if.Else);
                    break;
                case ProceduralWhile @while: VisitExpression(@while.Condition); VisitStatement(@while.Body); break;
                case ProceduralDo @do: VisitStatement(@do.Body); VisitExpression(@do.Condition); break;
                case ProceduralFor @for:
                    if (@for.Declaration is not null)
                        foreach (var variable in @for.Declaration.Variables)
                            if (variable.Initializer is not null) VisitExpression(variable.Initializer);
                    foreach (var expression in @for.Initializers) VisitExpression(expression);
                    if (@for.Condition is not null) VisitExpression(@for.Condition);
                    VisitStatement(@for.Body);
                    foreach (var expression in @for.Incrementors) VisitExpression(expression);
                    break;
                case ProceduralForEach forEach: VisitExpression(forEach.SourceExpression); VisitStatement(forEach.Body); break;
                case ProceduralTry @try:
                    VisitStatement(@try.Body);
                    foreach (var @catch in @try.Catches)
                    {
                        if (@catch.Filter is not null) VisitExpression(@catch.Filter);
                        VisitStatement(@catch.Body);
                    }
                    break;
                case ProceduralThrow { Expression: not null } @throw: VisitExpression(@throw.Expression); break;
                case ProceduralReturn { Expression: not null } @return: VisitExpression(@return.Expression); break;
            }
        }

        void VisitExpression(IrExpression expression)
        {
            if (mutated is not null)
                return;
            if (expression is IrAssignmentExpression { Target: IrVariableExpression variable } &&
                candidateIds.Contains(variable.Symbol.Id))
            {
                mutated = variable.Symbol;
                return;
            }
            if (expression is IrUnaryExpression
                {
                    Operator: IrUnaryOperator.PreIncrement or IrUnaryOperator.PreDecrement or
                              IrUnaryOperator.PostIncrement or IrUnaryOperator.PostDecrement,
                    Operand: IrVariableExpression unaryVariable
                } && candidateIds.Contains(unaryVariable.Symbol.Id))
            {
                mutated = unaryVariable.Symbol;
                return;
            }
            switch (expression)
            {
                case IrBinaryExpression binary: VisitExpression(binary.Left); VisitExpression(binary.Right); break;
                case IrUnaryExpression unary: VisitExpression(unary.Operand); break;
                case IrConversionExpression conversion: VisitExpression(conversion.Operand); break;
                case IrAwaitExpression awaitExpression: VisitExpression(awaitExpression.Operand); break;
                case IrConditionalExpression conditional:
                    VisitExpression(conditional.Condition); VisitExpression(conditional.WhenTrue); VisitExpression(conditional.WhenFalse); break;
                case IrMemberExpression member: VisitExpression(member.Receiver); break;
                case IrElementExpression element:
                    VisitExpression(element.Receiver); foreach (var argument in element.Arguments) VisitExpression(argument); break;
                case IrInvocationExpression invocation:
                    VisitExpression(invocation.Target); foreach (var argument in invocation.Arguments) VisitExpression(argument); break;
                case IrObjectCreationExpression creation:
                    foreach (var argument in creation.Arguments) VisitExpression(argument);
                    foreach (var initializer in creation.Initializers) VisitExpression(initializer);
                    break;
                case IrWithExpression withExpression:
                    VisitExpression(withExpression.Receiver); foreach (var initializer in withExpression.Initializers) VisitExpression(initializer); break;
                case IrArrayCreationExpression array:
                    if (array.Length is not null) VisitExpression(array.Length);
                    foreach (var item in array.Elements) VisitExpression(item);
                    break;
                case IrInterpolatedStringExpression interpolated:
                    foreach (var item in interpolated.Parts.OfType<IrInterpolation>()) VisitExpression(item.Expression);
                    break;
                case IrAssignmentExpression assignment: VisitExpression(assignment.Target); VisitExpression(assignment.Value); break;
                case IrLambdaExpression lambda:
                    if (lambda.ExpressionBody is not null) VisitExpression(lambda.ExpressionBody);
                    if (lambda.StatementBody is not null) VisitStatement(lambda.StatementBody);
                    break;
                case IrQueryExpression query:
                    VisitExpression(query.SourceExpression);
                    foreach (var clause in query.Clauses)
                    {
                        if (clause is IrWhereClause where) VisitExpression(where.Predicate);
                        else if (clause is IrOrderClause order) VisitExpression(order.Key);
                        else if (clause is IrSelectClause select) VisitExpression(select.Projection);
                        else if (clause is IrGroupClause group) { VisitExpression(group.Element); VisitExpression(group.Key); }
                    }
                    break;
            }
        }
    }

    private sealed record ServiceBrokerProgramPlan(
        IReadOnlyList<ProceduralStatement> RootBefore,
        IrAwaitExpression RootAwait,
        IReadOnlyList<ProceduralStatement> RootAfter,
        IrSymbol TaskListSymbol,
        ServiceBrokerTaskCreationPlan TaskCreation,
        IReadOnlyList<ServiceBrokerMethodPlan> Methods);

    private sealed record ServiceBrokerTaskCreationPlan(
        IrSymbol TaskListSymbol,
        IrExpression Source,
        ServiceBrokerMethodPlan Handler);

    private sealed record ServiceBrokerMethodPlan(
        MethodDefinition Method,
        string HandlerName,
        IReadOnlyList<ProceduralStatement> BeforeDelay,
        IrAwaitExpression Delay,
        IReadOnlyList<ProceduralStatement> AfterDelay,
        IReadOnlyList<IrSymbol> Captures)
    {
        public IReadOnlyList<IrSymbol> PayloadSymbols => Method.Parameters
            .Select(parameter => parameter.Symbol)
            .Concat(Captures)
            .GroupBy(symbol => symbol.Id)
            .Select(group => group.First())
            .ToArray();
    }
}
