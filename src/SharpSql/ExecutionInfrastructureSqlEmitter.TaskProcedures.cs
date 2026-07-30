namespace SharpSql;

internal static partial class ExecutionInfrastructureSqlEmitter
{
    private static void EmitEnqueueTaskProcedure(SqlWriter sql)
    {
        var procedure = new SqlWriter();
        procedure.Line($"CREATE OR ALTER PROCEDURE [{SchemaName}].[{EnqueueTaskProcedureName}]");
        using (procedure.Indent())
        {
            procedure.Line("@ExecutionId UNIQUEIDENTIFIER,");
            procedure.Line("@TaskId BIGINT,");
            procedure.Line("@Enqueued BIT OUTPUT");
        }
        procedure.Line("AS");
        procedure.Line("BEGIN");
        using (procedure.Indent())
        {
            procedure.Line("SET NOCOUNT ON;");
            EmitProcedureTransactionDeclaration(procedure);
            procedure.Line("SET @Enqueued = 0;");
            procedure.Line();
            procedure.Line("DECLARE @Dispatch TABLE (");
            using (procedure.Indent())
            {
                procedure.Line("[ProgramId] NVARCHAR(128) NOT NULL,");
                procedure.Line("[HandlerName] NVARCHAR(450) NOT NULL,");
                procedure.Line("[ContinuationState] INT NOT NULL,");
                procedure.Line("[PayloadJson] NVARCHAR(MAX) NULL");
            }
            procedure.Line(");");
            procedure.Line("DECLARE @ProgramId NVARCHAR(128);");
            procedure.Line("DECLARE @HandlerName NVARCHAR(450);");
            procedure.Line("DECLARE @ContinuationState INT;");
            procedure.Line("DECLARE @PayloadJson NVARCHAR(MAX);");
            procedure.Line("DECLARE @ConversationHandle UNIQUEIDENTIFIER;");
            procedure.Line("DECLARE @ConversationId UNIQUEIDENTIFIER;");
            procedure.Line("DECLARE @Notification NVARCHAR(MAX);");
            procedure.Line("DECLARE @MessageBody VARBINARY(MAX);");
            procedure.Line();
            procedure.Line("BEGIN TRY");
            using (procedure.Indent())
            {
                EmitProcedureTransactionStart(procedure, "SharpSqlEnqueueTask");
                procedure.Line();
                procedure.Line($"UPDATE [{SchemaName}].[{TasksTableName}] WITH (UPDLOCK, ROWLOCK)");
                procedure.Line("SET");
                using (procedure.Indent())
                {
                    procedure.Line("[State] = 2,");
                    procedure.Line("[EnqueuedAtUtc] = SYSUTCDATETIME(),");
                    procedure.Line("[DispatchConversationHandle] = NULL,");
                    procedure.Line("[DispatchConversationId] = NULL");
                }
                procedure.Line("OUTPUT");
                using (procedure.Indent())
                    procedure.Line("INSERTED.[ProgramId], INSERTED.[HandlerName], INSERTED.[ContinuationState], INSERTED.[PayloadJson]");
                procedure.Line("INTO @Dispatch ([ProgramId], [HandlerName], [ContinuationState], [PayloadJson])");
                procedure.Line("WHERE [ExecutionId] = @ExecutionId AND [TaskId] = @TaskId AND [State] = 1;");
                procedure.Line();
                procedure.Line("IF NOT EXISTS (SELECT 1 FROM @Dispatch)");
                procedure.Line("BEGIN");
                using (procedure.Indent())
                {
                    EmitProcedureTransactionCommit(procedure);
                    procedure.Line("RETURN;");
                }
                procedure.Line("END;");
                procedure.Line();
                procedure.Line("SELECT");
                using (procedure.Indent())
                {
                    procedure.Line("@ProgramId = [ProgramId],");
                    procedure.Line("@HandlerName = [HandlerName],");
                    procedure.Line("@ContinuationState = [ContinuationState],");
                    procedure.Line("@PayloadJson = [PayloadJson]");
                }
                procedure.Line("FROM @Dispatch;");
                procedure.Line();
                procedure.Line("BEGIN DIALOG CONVERSATION @ConversationHandle");
                using (procedure.Indent())
                {
                    procedure.Line($"FROM SERVICE [{WorkerServiceName}]");
                    procedure.Line($"TO SERVICE N'{WorkerServiceName}'");
                    procedure.Line($"ON CONTRACT [{ContractName}]");
                    procedure.Line("WITH ENCRYPTION = OFF;");
                }
                procedure.Line();
                procedure.Line("SELECT @ConversationId = [conversation_id]");
                procedure.Line("FROM sys.conversation_endpoints");
                procedure.Line("WHERE [conversation_handle] = @ConversationHandle;");
                procedure.Line("IF @ConversationId IS NULL");
                using (procedure.Indent())
                    procedure.Line($"THROW {InvalidTaskRouteErrorNumber}, 'The SharpSql task dialog has no durable conversation ID.', 1;");
                procedure.Line();
                procedure.Line("SELECT @Notification = (");
                using (procedure.Indent())
                {
                    procedure.Line("SELECT");
                    using (procedure.Indent())
                    {
                        procedure.Line("CONVERT(NVARCHAR(36), @ExecutionId) AS [executionId],");
                        procedure.Line("@TaskId AS [taskId],");
                        procedure.Line("@ProgramId AS [programId],");
                        procedure.Line("@HandlerName AS [handlerName],");
                        procedure.Line("@ContinuationState AS [continuationState],");
                        procedure.Line("@PayloadJson AS [payloadJson]");
                    }
                    procedure.Line("FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES");
                }
                procedure.Line(");");
                procedure.Line("SET @MessageBody = CONVERT(VARBINARY(MAX), @Notification);");
                procedure.Line("SEND ON CONVERSATION @ConversationHandle");
                using (procedure.Indent())
                    procedure.Line($"MESSAGE TYPE [{RequestMessageTypeName}] (@MessageBody);");
                procedure.Line();
                procedure.Line($"UPDATE [{SchemaName}].[{TasksTableName}]");
                procedure.Line("SET [DispatchConversationHandle] = @ConversationHandle, [DispatchConversationId] = @ConversationId");
                procedure.Line("WHERE [ExecutionId] = @ExecutionId AND [TaskId] = @TaskId;");
                procedure.Line();
                procedure.Line("SET @Enqueued = 1;");
                EmitProcedureTransactionCommit(procedure);
            }
            procedure.Line("END TRY");
            procedure.Line("BEGIN CATCH");
            using (procedure.Indent())
            {
                EmitProcedureTransactionRollback(procedure, "SharpSqlEnqueueTask");
                procedure.Line("THROW;");
            }
            procedure.Line("END CATCH;");
        }
        procedure.Line("END;");

        EmitProcedureBatch(sql, procedure);
    }

    private static void EmitScheduleTaskProcedure(SqlWriter sql)
    {
        var procedure = new SqlWriter();
        procedure.Line($"CREATE OR ALTER PROCEDURE [{SchemaName}].[{ScheduleTaskProcedureName}]");
        using (procedure.Indent())
        {
            procedure.Line("@ExecutionId UNIQUEIDENTIFIER,");
            procedure.Line("@ProgramId NVARCHAR(128),");
            procedure.Line("@HandlerName NVARCHAR(450),");
            procedure.Line("@ContinuationState INT = 0,");
            procedure.Line("@PayloadJson NVARCHAR(MAX) = NULL,");
            procedure.Line("@DelayMilliseconds INT = NULL,");
            procedure.Line("@StartSuspended BIT = 0,");
            procedure.Line("@TaskId BIGINT = NULL OUTPUT");
        }
        procedure.Line("AS");
        procedure.Line("BEGIN");
        using (procedure.Indent())
        {
            procedure.Line("SET NOCOUNT ON;");
            EmitProcedureTransactionDeclaration(procedure);
            procedure.Line();
            procedure.Line("IF NULLIF(LTRIM(RTRIM(@ProgramId)), N'') IS NULL OR NULLIF(LTRIM(RTRIM(@HandlerName)), N'') IS NULL");
            using (procedure.Indent())
                procedure.Line($"THROW {InvalidTaskRouteErrorNumber}, 'A SharpSql task requires a program ID and handler name.', 1;");
            procedure.Line("IF @PayloadJson IS NOT NULL AND ISJSON(@PayloadJson) <> 1");
            using (procedure.Indent())
                procedure.Line($"THROW {InvalidTaskStateErrorNumber}, 'A SharpSql task payload must contain valid JSON.', 1;");
            procedure.Line("IF @DelayMilliseconds IS NOT NULL AND @DelayMilliseconds < 0");
            using (procedure.Indent())
                procedure.Line($"THROW {InvalidDelayErrorNumber}, 'A SharpSql delay cannot be negative.', 1;");
            procedure.Line("IF @StartSuspended = 1 AND @DelayMilliseconds IS NOT NULL");
            using (procedure.Indent())
                procedure.Line($"THROW {InvalidTaskStateErrorNumber}, 'A task cannot be both explicitly suspended and delay-scheduled.', 1;");
            procedure.Line();
            procedure.Line("DECLARE @Allocation TABLE ([TaskId] BIGINT NOT NULL);");
            procedure.Line("DECLARE @NowUtc DATETIME2(7) = SYSUTCDATETIME();");
            procedure.Line("DECLARE @DueAtUtc DATETIME2(3);");
            procedure.Line("DECLARE @InitialState TINYINT;");
            procedure.Line("DECLARE @SuspensionGeneration INT = 0;");
            procedure.Line("DECLARE @Enqueued BIT = 0;");
            procedure.Line();
            procedure.Line("SET @DueAtUtc = CASE WHEN COALESCE(@DelayMilliseconds, 0) > 0 THEN DATEADD(MILLISECOND, @DelayMilliseconds, CONVERT(DATETIME2(3), @NowUtc)) END;");
            procedure.Line("SET @InitialState = CASE WHEN @StartSuspended = 1 OR @DueAtUtc IS NOT NULL THEN 0 ELSE 1 END;");
            procedure.Line("SET @SuspensionGeneration = CASE WHEN @DueAtUtc IS NOT NULL THEN 1 ELSE 0 END;");
            procedure.Line();
            procedure.Line("BEGIN TRY");
            using (procedure.Indent())
            {
                EmitProcedureTransactionStart(procedure, "SharpSqlScheduleTask");
                procedure.Line();
                procedure.Line($"UPDATE [{SchemaName}].[{ExecutionsTableName}] WITH (UPDLOCK, ROWLOCK)");
                procedure.Line("SET [NextTaskId] = [NextTaskId] + 1");
                procedure.Line("OUTPUT DELETED.[NextTaskId] INTO @Allocation ([TaskId])");
                procedure.Line("WHERE [ExecutionId] = @ExecutionId AND [State] NOT IN (2, 3, 4);");
                procedure.Line();
                procedure.Line("IF NOT EXISTS (SELECT 1 FROM @Allocation)");
                using (procedure.Indent())
                    procedure.Line($"THROW {ExecutionNotFoundErrorNumber}, 'The SharpSql execution does not exist or is terminal.', 1;");
                procedure.Line("SELECT @TaskId = [TaskId] FROM @Allocation;");
                procedure.Line();
                procedure.Line($"INSERT INTO [{SchemaName}].[{TasksTableName}] (");
                using (procedure.Indent())
                {
                    procedure.Line("[ExecutionId], [TaskId], [ProgramId], [HandlerName], [ContinuationState],");
                    procedure.Line("[SuspensionGeneration], [State], [PayloadJson], [ReadyAtUtc]");
                }
                procedure.Line(") VALUES (");
                using (procedure.Indent())
                {
                    procedure.Line("@ExecutionId, @TaskId, @ProgramId, @HandlerName, @ContinuationState,");
                    procedure.Line("@SuspensionGeneration, @InitialState, @PayloadJson, CASE WHEN @InitialState = 1 THEN @NowUtc END");
                }
                procedure.Line(");");
                procedure.Line();
                procedure.Line("IF @DueAtUtc IS NOT NULL");
                procedure.Line("BEGIN");
                using (procedure.Indent())
                {
                    procedure.Line($"INSERT INTO [{SchemaName}].[{TaskTimersTableName}] (");
                    using (procedure.Indent())
                        procedure.Line("[ExecutionId], [TaskId], [SuspensionGeneration], [DelayMilliseconds], [DueAtUtc]");
                    procedure.Line(") VALUES (");
                    using (procedure.Indent())
                        procedure.Line("@ExecutionId, @TaskId, @SuspensionGeneration, @DelayMilliseconds, @DueAtUtc");
                    procedure.Line(");");
                }
                procedure.Line("END;");
                procedure.Line("ELSE IF @InitialState = 1");
                procedure.Line("BEGIN");
                using (procedure.Indent())
                {
                    procedure.Line($"EXEC [{SchemaName}].[{EnqueueTaskProcedureName}]");
                    using (procedure.Indent())
                    {
                        procedure.Line("@ExecutionId = @ExecutionId,");
                        procedure.Line("@TaskId = @TaskId,");
                        procedure.Line("@Enqueued = @Enqueued OUTPUT;");
                    }
                }
                procedure.Line("END;");
                procedure.Line();
                EmitProcedureTransactionCommit(procedure);
                procedure.Line("SELECT @TaskId AS [TaskId], @InitialState AS [InitialState], @Enqueued AS [Enqueued], @DueAtUtc AS [DueAtUtc];");
            }
            procedure.Line("END TRY");
            procedure.Line("BEGIN CATCH");
            using (procedure.Indent())
            {
                EmitProcedureTransactionRollback(procedure, "SharpSqlScheduleTask");
                procedure.Line("THROW;");
            }
            procedure.Line("END CATCH;");
        }
        procedure.Line("END;");

        EmitProcedureBatch(sql, procedure);
    }

    private static void EmitSuspendTaskForDelayProcedure(SqlWriter sql)
    {
        var procedure = new SqlWriter();
        procedure.Line($"CREATE OR ALTER PROCEDURE [{SchemaName}].[{SuspendTaskForDelayProcedureName}]");
        using (procedure.Indent())
        {
            procedure.Line("@ExecutionId UNIQUEIDENTIFIER,");
            procedure.Line("@TaskId BIGINT,");
            procedure.Line("@ContinuationState INT,");
            procedure.Line("@PayloadJson NVARCHAR(MAX) = NULL,");
            procedure.Line("@DelayMilliseconds INT");
        }
        procedure.Line("AS");
        procedure.Line("BEGIN");
        using (procedure.Indent())
        {
            procedure.Line("SET NOCOUNT ON;");
            EmitProcedureTransactionDeclaration(procedure);
            procedure.Line();
            procedure.Line("IF @DelayMilliseconds IS NULL OR @DelayMilliseconds < 0");
            using (procedure.Indent())
                procedure.Line($"THROW {InvalidDelayErrorNumber}, 'A SharpSql delay cannot be negative.', 1;");
            procedure.Line("IF @PayloadJson IS NOT NULL AND ISJSON(@PayloadJson) <> 1");
            using (procedure.Indent())
                procedure.Line($"THROW {InvalidTaskStateErrorNumber}, 'A SharpSql task payload must contain valid JSON.', 1;");
            procedure.Line();
            procedure.Line("DECLARE @NowUtc DATETIME2(7) = SYSUTCDATETIME();");
            procedure.Line("DECLARE @DueAtUtc DATETIME2(3);");
            procedure.Line("DECLARE @Generation TABLE ([SuspensionGeneration] INT NOT NULL);");
            procedure.Line("DECLARE @SuspensionGeneration INT;");
            procedure.Line("DECLARE @Enqueued BIT = 0;");
            procedure.Line();
            procedure.Line("SET @DueAtUtc = CASE WHEN @DelayMilliseconds > 0 THEN DATEADD(MILLISECOND, @DelayMilliseconds, CONVERT(DATETIME2(3), @NowUtc)) END;");
            procedure.Line();
            procedure.Line("BEGIN TRY");
            using (procedure.Indent())
            {
                EmitProcedureTransactionStart(procedure, "SharpSqlSuspendDelay");
                procedure.Line();
                procedure.Line($"UPDATE [{SchemaName}].[{TasksTableName}] WITH (UPDLOCK, ROWLOCK)");
                procedure.Line("SET");
                using (procedure.Indent())
                {
                    procedure.Line("[ContinuationState] = @ContinuationState,");
                    procedure.Line("[PayloadJson] = @PayloadJson,");
                    procedure.Line("[SuspensionGeneration] = [SuspensionGeneration] + 1,");
                    procedure.Line("[State] = CASE WHEN @DelayMilliseconds = 0 THEN 1 ELSE 0 END,");
                    procedure.Line("[ReadyAtUtc] = CASE WHEN @DelayMilliseconds = 0 THEN @NowUtc END,");
                    procedure.Line("[EnqueuedAtUtc] = NULL,");
                    procedure.Line("[StartedAtUtc] = NULL,");
                    procedure.Line("[DispatchConversationHandle] = NULL,");
                    procedure.Line("[DispatchConversationId] = NULL");
                }
                procedure.Line("OUTPUT INSERTED.[SuspensionGeneration] INTO @Generation ([SuspensionGeneration])");
                procedure.Line("WHERE [ExecutionId] = @ExecutionId AND [TaskId] = @TaskId AND [State] IN (0, 2, 3);");
                procedure.Line();
                procedure.Line("IF NOT EXISTS (SELECT 1 FROM @Generation)");
                procedure.Line("BEGIN");
                using (procedure.Indent())
                {
                    procedure.Line($"IF NOT EXISTS (SELECT 1 FROM [{SchemaName}].[{TasksTableName}] WITH (UPDLOCK, HOLDLOCK) WHERE [ExecutionId] = @ExecutionId AND [TaskId] = @TaskId)");
                    using (procedure.Indent())
                        procedure.Line($"THROW {TaskNotFoundErrorNumber}, 'The SharpSql task does not exist.', 1;");
                    procedure.Line($"THROW {InvalidTaskStateErrorNumber}, 'Only a waiting, enqueued, or running SharpSql task can suspend.', 1;");
                }
                procedure.Line("END;");
                procedure.Line("SELECT @SuspensionGeneration = [SuspensionGeneration] FROM @Generation;");
                procedure.Line();
                procedure.Line("IF @DueAtUtc IS NOT NULL");
                procedure.Line("BEGIN");
                using (procedure.Indent())
                {
                    procedure.Line($"INSERT INTO [{SchemaName}].[{TaskTimersTableName}] (");
                    using (procedure.Indent())
                        procedure.Line("[ExecutionId], [TaskId], [SuspensionGeneration], [DelayMilliseconds], [DueAtUtc]");
                    procedure.Line(") VALUES (");
                    using (procedure.Indent())
                        procedure.Line("@ExecutionId, @TaskId, @SuspensionGeneration, @DelayMilliseconds, @DueAtUtc");
                    procedure.Line(");");
                }
                procedure.Line("END;");
                procedure.Line("ELSE");
                procedure.Line("BEGIN");
                using (procedure.Indent())
                {
                    procedure.Line($"EXEC [{SchemaName}].[{EnqueueTaskProcedureName}]");
                    using (procedure.Indent())
                    {
                        procedure.Line("@ExecutionId = @ExecutionId,");
                        procedure.Line("@TaskId = @TaskId,");
                        procedure.Line("@Enqueued = @Enqueued OUTPUT;");
                    }
                }
                procedure.Line("END;");
                procedure.Line();
                EmitProcedureTransactionCommit(procedure);
                procedure.Line("SELECT @SuspensionGeneration AS [SuspensionGeneration], @Enqueued AS [Enqueued], @DueAtUtc AS [DueAtUtc];");
            }
            procedure.Line("END TRY");
            procedure.Line("BEGIN CATCH");
            using (procedure.Indent())
            {
                EmitProcedureTransactionRollback(procedure, "SharpSqlSuspendDelay");
                procedure.Line("THROW;");
            }
            procedure.Line("END CATCH;");
        }
        procedure.Line("END;");

        EmitProcedureBatch(sql, procedure);
    }

    private static void EmitSuspendTaskForDependenciesProcedure(SqlWriter sql)
    {
        var procedure = new SqlWriter();
        procedure.Line($"CREATE OR ALTER PROCEDURE [{SchemaName}].[{SuspendTaskForDependenciesProcedureName}]");
        using (procedure.Indent())
        {
            procedure.Line("@ExecutionId UNIQUEIDENTIFIER,");
            procedure.Line("@TaskId BIGINT,");
            procedure.Line("@ContinuationState INT,");
            procedure.Line("@PayloadJson NVARCHAR(MAX) = NULL,");
            procedure.Line("@ExpectedDependencyCount INT");
        }
        procedure.Line("AS");
        procedure.Line("BEGIN");
        using (procedure.Indent())
        {
            procedure.Line("SET NOCOUNT ON;");
            EmitProcedureTransactionDeclaration(procedure);
            procedure.Line();
            procedure.Line("IF @ExpectedDependencyCount IS NULL OR @ExpectedDependencyCount < 0");
            using (procedure.Indent())
                procedure.Line($"THROW {InvalidTaskJoinErrorNumber}, 'A WhenAll dependency count cannot be negative.', 1;");
            procedure.Line("IF @PayloadJson IS NOT NULL AND ISJSON(@PayloadJson) <> 1");
            using (procedure.Indent())
                procedure.Line($"THROW {InvalidTaskStateErrorNumber}, 'A SharpSql task payload must contain valid JSON.', 1;");
            procedure.Line();
            procedure.Line("DECLARE @NowUtc DATETIME2(7) = SYSUTCDATETIME();");
            procedure.Line("DECLARE @Generation TABLE ([SuspensionGeneration] INT NOT NULL);");
            procedure.Line("DECLARE @SuspensionGeneration INT;");
            procedure.Line("DECLARE @Enqueued BIT = 0;");
            procedure.Line("DECLARE @CoordinationLockResult INT;");
            procedure.Line("DECLARE @CoordinationLockName NVARCHAR(255) = N'SharpSql.TaskJoin.' + CONVERT(NVARCHAR(36), @ExecutionId);");
            procedure.Line();
            procedure.Line("BEGIN TRY");
            using (procedure.Indent())
            {
                EmitProcedureTransactionStart(procedure, "SharpSqlSuspendDependencies");
                procedure.Line();
                EmitTaskJoinCoordinationLock(procedure);
                procedure.Line();
                procedure.Line($"UPDATE [{SchemaName}].[{TasksTableName}] WITH (UPDLOCK, ROWLOCK)");
                procedure.Line("SET");
                using (procedure.Indent())
                {
                    procedure.Line("[ContinuationState] = @ContinuationState,");
                    procedure.Line("[PayloadJson] = @PayloadJson,");
                    procedure.Line("[SuspensionGeneration] = [SuspensionGeneration] + 1,");
                    procedure.Line("[State] = CASE WHEN @ExpectedDependencyCount = 0 THEN 1 ELSE 0 END,");
                    procedure.Line("[ReadyAtUtc] = CASE WHEN @ExpectedDependencyCount = 0 THEN @NowUtc END,");
                    procedure.Line("[EnqueuedAtUtc] = NULL,");
                    procedure.Line("[StartedAtUtc] = NULL,");
                    procedure.Line("[DispatchConversationHandle] = NULL,");
                    procedure.Line("[DispatchConversationId] = NULL");
                }
                procedure.Line("OUTPUT INSERTED.[SuspensionGeneration] INTO @Generation ([SuspensionGeneration])");
                procedure.Line("WHERE [ExecutionId] = @ExecutionId AND [TaskId] = @TaskId AND [State] IN (0, 2, 3);");
                procedure.Line();
                procedure.Line("IF NOT EXISTS (SELECT 1 FROM @Generation)");
                procedure.Line("BEGIN");
                using (procedure.Indent())
                {
                    procedure.Line($"IF NOT EXISTS (SELECT 1 FROM [{SchemaName}].[{TasksTableName}] WITH (UPDLOCK, HOLDLOCK) WHERE [ExecutionId] = @ExecutionId AND [TaskId] = @TaskId)");
                    using (procedure.Indent())
                        procedure.Line($"THROW {TaskNotFoundErrorNumber}, 'The SharpSql task does not exist.', 1;");
                    procedure.Line($"THROW {InvalidTaskStateErrorNumber}, 'Only a waiting, enqueued, or running SharpSql task can suspend.', 1;");
                }
                procedure.Line("END;");
                procedure.Line("SELECT @SuspensionGeneration = [SuspensionGeneration] FROM @Generation;");
                procedure.Line();
                procedure.Line($"INSERT INTO [{SchemaName}].[{TaskJoinsTableName}] (");
                using (procedure.Indent())
                {
                    procedure.Line("[ExecutionId], [ContinuationTaskId], [SuspensionGeneration],");
                    procedure.Line("[ExpectedDependencyCount], [ReadyAtUtc]");
                }
                procedure.Line(") VALUES (");
                using (procedure.Indent())
                {
                    procedure.Line("@ExecutionId, @TaskId, @SuspensionGeneration,");
                    procedure.Line("@ExpectedDependencyCount, CASE WHEN @ExpectedDependencyCount = 0 THEN @NowUtc END");
                }
                procedure.Line(");");
                procedure.Line();
                procedure.Line("IF @ExpectedDependencyCount = 0");
                procedure.Line("BEGIN");
                using (procedure.Indent())
                {
                    procedure.Line($"EXEC [{SchemaName}].[{EnqueueTaskProcedureName}]");
                    using (procedure.Indent())
                    {
                        procedure.Line("@ExecutionId = @ExecutionId,");
                        procedure.Line("@TaskId = @TaskId,");
                        procedure.Line("@Enqueued = @Enqueued OUTPUT;");
                    }
                    procedure.Line($"UPDATE [{SchemaName}].[{TaskJoinsTableName}]");
                    procedure.Line("SET [EnqueuedAtUtc] = CASE WHEN @Enqueued = 1 THEN SYSUTCDATETIME() ELSE [EnqueuedAtUtc] END");
                    procedure.Line("WHERE [ExecutionId] = @ExecutionId AND [ContinuationTaskId] = @TaskId AND [SuspensionGeneration] = @SuspensionGeneration;");
                }
                procedure.Line("END;");
                procedure.Line();
                EmitProcedureTransactionCommit(procedure);
                procedure.Line("SELECT @SuspensionGeneration AS [SuspensionGeneration], @Enqueued AS [Enqueued];");
            }
            procedure.Line("END TRY");
            procedure.Line("BEGIN CATCH");
            using (procedure.Indent())
            {
                EmitProcedureTransactionRollback(procedure, "SharpSqlSuspendDependencies");
                procedure.Line("THROW;");
            }
            procedure.Line("END CATCH;");
        }
        procedure.Line("END;");

        EmitProcedureBatch(sql, procedure);
    }

}

