using Xunit;

namespace SharpSql.Tests;

public sealed class ExecutionInfrastructureSqlEmitterTests
{
    [Fact]
    public void PublicRuntimeApiExposesTheProvisioningScript()
    {
        var sql = SharpSqlServiceBrokerRuntime.GenerateProvisioningSql();

        Assert.StartsWith(ExecutionInfrastructureSqlEmitter.Emit(), sql, StringComparison.Ordinal);
        Assert.Contains("CREATE SERVICE [//sharpsql/v1/worker]", sql);
        Assert.Contains("PROCEDURE_NAME = [SharpSql].[DispatchWorker]", sql);
    }

    [Fact]
    public void EmitsConcurrencySafeIdempotentProvisioning()
    {
        var sql = ExecutionInfrastructureSqlEmitter.Emit();

        Assert.Contains("SET ANSI_NULLS ON;", sql);
        Assert.Contains("SET QUOTED_IDENTIFIER ON;", sql);
        Assert.Contains("SET NUMERIC_ROUNDABORT OFF;", sql);
        Assert.Equal(51904, ExecutionInfrastructureSqlEmitter.AmbientTransactionErrorNumber);
        Assert.Contains("IF @@TRANCOUNT > 0 THROW 51904", sql);
        Assert.Contains("WHERE [database_id] = DB_ID() AND [is_broker_enabled] = 1", sql);
        Assert.Equal(51902, ExecutionInfrastructureSqlEmitter.BrokerDisabledErrorNumber);
        Assert.Contains("THROW 51902, 'Service Broker is not enabled in the current database.'", sql);
        Assert.Contains("BEGIN TRANSACTION;", sql);
        Assert.Contains("sys.sp_getapplock", sql);
        Assert.Contains("@Resource = N'SharpSql.Runtime.Provisioning'", sql);
        Assert.Contains("@LockMode = N'Exclusive'", sql);
        Assert.Contains("@LockOwner = N'Transaction'", sql);
        Assert.Equal(51900, ExecutionInfrastructureSqlEmitter.ProvisioningLockErrorNumber);
        Assert.Contains("THROW 51900, 'Could not acquire the SharpSql infrastructure provisioning lock.'", sql);
        Assert.Contains("IF SCHEMA_ID(N'SharpSql') IS NULL", sql);
        Assert.Contains("IF OBJECT_ID(N'[SharpSql].[OutputSequence]', N'SO') IS NULL", sql);
        Assert.Contains("CREATE SEQUENCE [SharpSql].[OutputSequence] AS BIGINT", sql);
        Assert.Contains("IF OBJECT_ID(N'[SharpSql].[Executions]', N'U') IS NULL", sql);
        Assert.Contains("IF OBJECT_ID(N'[SharpSql].[OutputEvents]', N'U') IS NULL", sql);
        Assert.Contains("IF NOT EXISTS (SELECT 1 FROM sys.service_message_types", sql);
        Assert.Contains("IF NOT EXISTS (SELECT 1 FROM sys.service_contracts", sql);
        Assert.Contains("IF OBJECT_ID(N'[SharpSql].[LauncherQueue]', N'SQ') IS NULL", sql);
        Assert.Contains("IF NOT EXISTS (SELECT 1 FROM sys.services", sql);
        Assert.Contains("COMMIT TRANSACTION;", sql);
        Assert.Contains("IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;", sql);
    }

    [Fact]
    public void ExecutionAndOutputTablesScopeOrderingToAnExecution()
    {
        var sql = ExecutionInfrastructureSqlEmitter.Emit();

        Assert.Contains("CONSTRAINT [PK_sharpsql_Executions] PRIMARY KEY ([ExecutionId])", sql);
        Assert.Contains("[NextOutputSequence] BIGINT NOT NULL", sql);
        Assert.Contains("NEXT VALUE FOR [SharpSql].[OutputSequence]", sql);
        Assert.Contains("CREATE INDEX [IX_sharpsql_Executions_ConversationHandle]", sql);
        Assert.DoesNotContain("CREATE UNIQUE INDEX", sql);
        Assert.DoesNotContain("WHERE [ConversationHandle] IS NOT NULL", sql);
        Assert.Contains("CONSTRAINT [PK_sharpsql_OutputEvents] PRIMARY KEY CLUSTERED ([ExecutionId], [SequenceNumber])", sql);
        Assert.Contains("FOREIGN KEY ([ExecutionId])", sql);
        Assert.Contains("REFERENCES [SharpSql].[Executions] ([ExecutionId]) ON DELETE CASCADE", sql);
        Assert.Contains("CHECK ([SequenceNumber] > 0)", sql);
        Assert.DoesNotContain("IDENTITY", sql);
    }

    [Fact]
    public void BrokerContractRoutesWorkerNotificationsBackToTheLauncher()
    {
        var sql = ExecutionInfrastructureSqlEmitter.Emit();

        Assert.Contains("CREATE MESSAGE TYPE [//sharpsql/v1/execution/request] VALIDATION = NONE;", sql);
        Assert.Contains("CREATE MESSAGE TYPE [//sharpsql/v1/execution/output] VALIDATION = NONE;", sql);
        Assert.Contains("CREATE MESSAGE TYPE [//sharpsql/v1/execution/completed] VALIDATION = NONE;", sql);
        Assert.Contains("[//sharpsql/v1/execution/request] SENT BY INITIATOR", sql);
        Assert.Contains("[//sharpsql/v1/execution/output] SENT BY TARGET", sql);
        Assert.Contains("[//sharpsql/v1/execution/completed] SENT BY TARGET", sql);
        Assert.Contains("CREATE SERVICE [//sharpsql/v1/launcher] ON QUEUE [SharpSql].[LauncherQueue];", sql);
        Assert.Contains("CREATE SERVICE [//sharpsql/v1/worker]", sql);
        Assert.Contains("ON QUEUE [SharpSql].[WorkerQueue]", sql);
        Assert.Contains("([//sharpsql/v1/execution/contract]);", sql);
        Assert.DoesNotContain("ACTIVATION", sql);
        Assert.DoesNotContain("PROCEDURE_NAME", sql);
    }

    [Fact]
    public void AppendOutputAllocatesAndPersistsWithoutSerializingWorkerTransactions()
    {
        var sql = ExecutionInfrastructureSqlEmitter.Emit();

        Assert.Contains("CREATE OR ALTER PROCEDURE [SharpSql].[AppendOutput]", sql);
        Assert.Contains("@ExecutionId UNIQUEIDENTIFIER", sql);
        Assert.Contains("@OutputText NVARCHAR(MAX)", sql);
        Assert.Contains("NEXT VALUE FOR [SharpSql].[OutputSequence]", sql);
        Assert.Contains("INSERT INTO [SharpSql].[OutputEvents]", sql);
        Assert.DoesNotContain("SET [NextOutputSequence] = [NextOutputSequence] + 1", sql);
        Assert.DoesNotContain("MESSAGE TYPE [//sharpsql/v1/execution/output] (@MessageBody)", sql);
        Assert.Contains("COMMIT TRANSACTION;", sql);
        Assert.Equal(51901, ExecutionInfrastructureSqlEmitter.ExecutionNotFoundErrorNumber);
        Assert.Contains("THROW 51901, ''The SharpSql execution does not exist.''", sql);
    }

    [Fact]
    public void CompleteExecutionTransitionsOnceSendsPayloadAndEndsTheTargetDialog()
    {
        var sql = ExecutionInfrastructureSqlEmitter.Emit();

        Assert.Contains("CREATE OR ALTER PROCEDURE [SharpSql].[CompleteExecution]", sql);
        Assert.Contains("@State TINYINT", sql);
        Assert.Contains("@ErrorNumber INT = NULL", sql);
        Assert.Contains("IF @State IS NULL OR @State NOT IN (2, 3, 4)", sql);
        Assert.Equal(51903, ExecutionInfrastructureSqlEmitter.InvalidTerminalStateErrorNumber);
        Assert.Contains("THROW 51903, ''A terminal SharpSql state must be succeeded (2), failed (3), or canceled (4).''", sql);
        Assert.Contains("WHERE [ExecutionId] = @ExecutionId AND [State] NOT IN (2, 3, 4)", sql);
        Assert.Contains("[ErrorNumber] = CASE WHEN @State = 2 THEN NULL ELSE @ErrorNumber END", sql);
        Assert.Contains("@State AS [state]", sql);
        Assert.Contains("@StoredErrorNumber AS [errorNumber]", sql);
        Assert.Contains("@StoredErrorMessage AS [errorMessage]", sql);
        Assert.Contains("FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES", sql);
        Assert.Contains("MESSAGE TYPE [//sharpsql/v1/execution/completed] (@MessageBody)", sql);
        Assert.Contains("END CONVERSATION @ConversationHandle;", sql);
        Assert.Contains("SELECT CAST(0 AS BIT) AS [Completed]", sql);
        Assert.Contains("SELECT CAST(1 AS BIT) AS [Completed]", sql);
        Assert.DoesNotContain("DELETE FROM [SharpSql].[Executions]", sql);
        Assert.DoesNotContain("DELETE FROM [SharpSql].[OutputEvents]", sql);
    }

    [Fact]
    public void RuntimeProceduresOwnOrSaveTransactionsWithoutDoomingCatchableAmbientErrors()
    {
        var sql = ExecutionInfrastructureSqlEmitter.Emit();
        var savepoints = new[]
        {
            "SharpSqlAppendOutput",
            "SharpSqlCompleteExecution",
            "SharpSqlEnqueueTask",
            "SharpSqlScheduleTask",
            "SharpSqlSuspendDelay",
            "SharpSqlSuspendDependencies",
            "SharpSqlRegisterDependency",
            "SharpSqlCompleteTask",
            "SharpSqlClaimDue"
        };

        Assert.Equal(9, CountOccurrences(sql, "SET XACT_ABORT OFF;"));
        Assert.Equal(9, CountOccurrences(sql, "DECLARE @OwnTransaction BIT = 0;"));
        Assert.DoesNotContain("SET XACT_ABORT ON;", sql);
        Assert.Contains("IF @OwnTransaction = 1 COMMIT TRANSACTION;", sql);
        Assert.Contains("IF @OwnTransaction = 1 AND XACT_STATE() <> 0", sql);
        Assert.Contains("ELSE IF @OwnTransaction = 0 AND XACT_STATE() = 1", sql);
        Assert.Equal(1, CountOccurrences(sql, "IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;"));
        Assert.All(savepoints, savepoint =>
        {
            Assert.Contains($"SAVE TRANSACTION [{savepoint}];", sql);
            Assert.Contains($"ROLLBACK TRANSACTION [{savepoint}];", sql);
        });
    }

    [Fact]
    public void DurableTaskTablesPartitionStateResultsJoinsAndMillisecondTimers()
    {
        var sql = ExecutionInfrastructureSqlEmitter.Emit();

        Assert.Contains("[NextTaskId] BIGINT NOT NULL", sql);
        Assert.Contains("IF COL_LENGTH(N'[SharpSql].[Executions]', N'NextTaskId') IS NULL", sql);
        Assert.Contains("CREATE TABLE [SharpSql].[Tasks]", sql);
        Assert.Contains("PRIMARY KEY CLUSTERED ([ExecutionId], [TaskId])", sql);
        Assert.Contains("[ProgramId] NVARCHAR(128) NOT NULL", sql);
        Assert.Contains("[HandlerName] NVARCHAR(450) NOT NULL", sql);
        Assert.Contains("[ContinuationState] INT NOT NULL", sql);
        Assert.Contains("[SuspensionGeneration] INT NOT NULL", sql);
        Assert.Contains("[PayloadJson] NVARCHAR(MAX) NULL", sql);
        Assert.Contains("[ResultScalar] SQL_VARIANT NULL", sql);
        Assert.Contains("[ResultText] NVARCHAR(MAX) NULL", sql);
        Assert.Contains("[ResultBinary] VARBINARY(MAX) NULL", sql);
        Assert.Contains("[ResultReferenceId] BIGINT NULL", sql);
        Assert.Contains("[DispatchConversationId] UNIQUEIDENTIFIER NULL", sql);
        Assert.Contains("IF COL_LENGTH(N'[SharpSql].[Tasks]', N'DispatchConversationId') IS NULL", sql);
        Assert.Contains("CREATE INDEX [IX_sharpsql_Tasks_DispatchConversationHandle]", sql);
        Assert.Contains("CREATE TABLE [SharpSql].[TaskTimers]", sql);
        Assert.Contains("[DueAtUtc] DATETIME2(3) NOT NULL", sql);
        Assert.Contains("[DelayMilliseconds] INT NOT NULL", sql);
        Assert.Contains("CREATE INDEX [IX_sharpsql_TaskTimers_Due]", sql);
        Assert.Contains("CREATE TABLE [SharpSql].[TaskJoins]", sql);
        Assert.Contains("[ExpectedDependencyCount] INT NOT NULL", sql);
        Assert.Contains("[CompletedDependencyCount] INT NOT NULL", sql);
        Assert.Contains("CREATE TABLE [SharpSql].[TaskDependencies]", sql);
        Assert.Contains("FOREIGN KEY ([ExecutionId], [DependencyTaskId])", sql);
    }

    [Fact]
    public void TaskSchedulingAndSuspensionRequeueTheSameSafelyRoutedTask()
    {
        var sql = ExecutionInfrastructureSqlEmitter.Emit();

        Assert.Contains("CREATE OR ALTER PROCEDURE [SharpSql].[ScheduleTask]", sql);
        Assert.Contains("SET [NextTaskId] = [NextTaskId] + 1", sql);
        Assert.Contains("OUTPUT DELETED.[NextTaskId] INTO @Allocation ([TaskId])", sql);
        Assert.Contains("CREATE OR ALTER PROCEDURE [SharpSql].[EnqueueTask]", sql);
        Assert.Contains("WHERE [ExecutionId] = @ExecutionId AND [TaskId] = @TaskId AND [State] = 1", sql);
        Assert.Contains("BEGIN DIALOG CONVERSATION @ConversationHandle", sql);
        Assert.Contains("SELECT @ConversationId = [conversation_id]", sql);
        Assert.Contains("IF @ConversationId IS NULL", sql);
        Assert.Contains("THROW 51911, ''The SharpSql task dialog has no durable conversation ID.''", sql);
        Assert.Contains("SET [DispatchConversationHandle] = @ConversationHandle, [DispatchConversationId] = @ConversationId", sql);
        Assert.Equal(3, CountOccurrences(sql, "[DispatchConversationId] = NULL"));
        Assert.Contains("@TaskId AS [taskId]", sql);
        Assert.Contains("@ProgramId AS [programId]", sql);
        Assert.Contains("@HandlerName AS [handlerName]", sql);
        Assert.Contains("@ContinuationState AS [continuationState]", sql);
        Assert.Contains("MESSAGE TYPE [//sharpsql/v1/execution/request] (@MessageBody)", sql);
        Assert.Contains("CREATE OR ALTER PROCEDURE [SharpSql].[SuspendTaskForDelay]", sql);
        Assert.Contains("DATEADD(MILLISECOND, @DelayMilliseconds", sql);
        Assert.Contains("[SuspensionGeneration] = [SuspensionGeneration] + 1", sql);
        Assert.Contains("CREATE OR ALTER PROCEDURE [SharpSql].[SuspendTaskForDependencies]", sql);
        Assert.DoesNotContain("sp_executesql", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PROCEDURE_NAME", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DependencyCompletionAndDueClaimsUseAtomicExactlyOnceTransitions()
    {
        var sql = ExecutionInfrastructureSqlEmitter.Emit();

        Assert.Contains("CREATE OR ALTER PROCEDURE [SharpSql].[RegisterTaskDependency]", sql);
        Assert.Contains("WITH (UPDLOCK, HOLDLOCK)", sql);
        Assert.Contains("[RegisteredDependencyCount] = [RegisteredDependencyCount] + 1", sql);
        Assert.Contains("[CompletedDependencyCount] = [CompletedDependencyCount] +", sql);
        Assert.Contains("AND [ReadyAtUtc] IS NULL", sql);
        Assert.Contains("CREATE OR ALTER PROCEDURE [SharpSql].[CompleteTask]", sql);
        Assert.Contains("WHERE [ExecutionId] = @ExecutionId AND [TaskId] = @TaskId AND [State] NOT BETWEEN 4 AND 6", sql);
        Assert.Contains("[DependencyState] = @State, [CompletedAtUtc] = @NowUtc", sql);
        Assert.Contains("CREATE OR ALTER PROCEDURE [SharpSql].[ClaimDueContinuations]", sql);
        Assert.Contains("WITH (UPDLOCK, READPAST, ROWLOCK)", sql);
        Assert.Contains("[DueAtUtc] <= CONVERT(DATETIME2(3), @NowUtc)", sql);
        Assert.Contains("SET [State] = 1, [ClaimedAtUtc] = @NowUtc", sql);
    }

    [Fact]
    public void DueClaimsPreserveEnqueueOrderWithoutSerializingAnExecution()
    {
        var sql = ExecutionInfrastructureSqlEmitter.Emit();

        Assert.Contains("[OrderAtUtc] DATETIME2(7) NOT NULL", sql);
        Assert.Contains("CAST(1 AS TINYINT), INSERTED.[DueAtUtc]", sql);
        Assert.Contains(
            "INTO @Candidates ([ExecutionId], [TaskId], [SuspensionGeneration], [Source], [OrderAtUtc])",
            sql);
        Assert.Contains("CAST(0 AS TINYINT), COALESCE([task].[ReadyAtUtc], @NowUtc)", sql);
        Assert.DoesNotContain("FROM [SharpSql].[TaskTimers] AS [earlier_timer]", sql);
        Assert.Contains(
            "FROM @Candidates ORDER BY [Source] DESC, [OrderAtUtc], [ExecutionId], [TaskId];",
            sql);
    }

    [Fact]
    public void FixedBrokerIdentifiersAreBracketSafeAndWithinSqlServerLimits()
    {
        var identifiers = new[]
        {
            ExecutionInfrastructureSqlEmitter.SchemaName,
            ExecutionInfrastructureSqlEmitter.ExecutionsTableName,
            ExecutionInfrastructureSqlEmitter.OutputEventsTableName,
            ExecutionInfrastructureSqlEmitter.OutputSequenceName,
            ExecutionInfrastructureSqlEmitter.TasksTableName,
            ExecutionInfrastructureSqlEmitter.TaskTimersTableName,
            ExecutionInfrastructureSqlEmitter.TaskJoinsTableName,
            ExecutionInfrastructureSqlEmitter.TaskDependenciesTableName,
            ExecutionInfrastructureSqlEmitter.AppendOutputProcedureName,
            ExecutionInfrastructureSqlEmitter.CompleteExecutionProcedureName,
            ExecutionInfrastructureSqlEmitter.EnqueueTaskProcedureName,
            ExecutionInfrastructureSqlEmitter.ScheduleTaskProcedureName,
            ExecutionInfrastructureSqlEmitter.SuspendTaskForDelayProcedureName,
            ExecutionInfrastructureSqlEmitter.SuspendTaskForDependenciesProcedureName,
            ExecutionInfrastructureSqlEmitter.RegisterTaskDependencyProcedureName,
            ExecutionInfrastructureSqlEmitter.CompleteTaskProcedureName,
            ExecutionInfrastructureSqlEmitter.ClaimDueContinuationsProcedureName,
            ExecutionInfrastructureSqlEmitter.LauncherQueueName,
            ExecutionInfrastructureSqlEmitter.WorkerQueueName,
            ExecutionInfrastructureSqlEmitter.RequestMessageTypeName,
            ExecutionInfrastructureSqlEmitter.OutputMessageTypeName,
            ExecutionInfrastructureSqlEmitter.CompletedMessageTypeName,
            ExecutionInfrastructureSqlEmitter.ContractName,
            ExecutionInfrastructureSqlEmitter.LauncherServiceName,
            ExecutionInfrastructureSqlEmitter.WorkerServiceName
        };

        Assert.All(identifiers, identifier =>
        {
            Assert.InRange(identifier.Length, 1, 128);
            Assert.DoesNotContain("]", identifier);
            Assert.DoesNotContain("'", identifier);
        });
    }

    private static int CountOccurrences(string value, string needle) =>
        value.Split([needle], StringSplitOptions.None).Length - 1;
}
