namespace SharpSql;

internal static partial class ExecutionInfrastructureSqlEmitter
{
    private static void EmitRegisterTaskDependencyProcedure(SqlWriter sql)
    {
        var procedure = new SqlWriter();
        procedure.Line($"CREATE OR ALTER PROCEDURE [{SchemaName}].[{RegisterTaskDependencyProcedureName}]");
        using (procedure.Indent())
        {
            procedure.Line("@ExecutionId UNIQUEIDENTIFIER,");
            procedure.Line("@ContinuationTaskId BIGINT,");
            procedure.Line("@DependencyTaskId BIGINT,");
            procedure.Line("@SuspensionGeneration INT = NULL");
        }
        procedure.Line("AS");
        procedure.Line("BEGIN");
        using (procedure.Indent())
        {
            procedure.Line("SET NOCOUNT ON;");
            EmitProcedureTransactionDeclaration(procedure);
            procedure.Line();
            procedure.Line("IF @ContinuationTaskId = @DependencyTaskId");
            using (procedure.Indent())
                procedure.Line($"THROW {InvalidTaskJoinErrorNumber}, 'A SharpSql task cannot depend on itself.', 1;");
            procedure.Line();
            procedure.Line("DECLARE @DependencyState TINYINT;");
            procedure.Line("DECLARE @CurrentGeneration INT;");
            procedure.Line("DECLARE @ExpectedDependencyCount INT;");
            procedure.Line("DECLARE @RegisteredDependencyCount INT;");
            procedure.Line("DECLARE @Inserted BIT = 0;");
            procedure.Line("DECLARE @JoinReady BIT = 0;");
            procedure.Line("DECLARE @Enqueued BIT = 0;");
            procedure.Line("DECLARE @NowUtc DATETIME2(7) = SYSUTCDATETIME();");
            procedure.Line("DECLARE @Ready TABLE ([ContinuationTaskId] BIGINT NOT NULL, [SuspensionGeneration] INT NOT NULL);");
            procedure.Line("DECLARE @CoordinationLockResult INT;");
            procedure.Line("DECLARE @CoordinationLockName NVARCHAR(255) = N'SharpSql.TaskJoin.' + CONVERT(NVARCHAR(36), @ExecutionId);");
            procedure.Line();
            procedure.Line("BEGIN TRY");
            using (procedure.Indent())
            {
                EmitProcedureTransactionStart(procedure, "SharpSqlRegisterDependency");
                procedure.Line();
                EmitTaskJoinCoordinationLock(procedure);
                procedure.Line();
                procedure.Line($"SELECT @CurrentGeneration = [SuspensionGeneration] FROM [{SchemaName}].[{TasksTableName}]");
                procedure.Line("WHERE [ExecutionId] = @ExecutionId AND [TaskId] = @ContinuationTaskId;");
                procedure.Line("IF @CurrentGeneration IS NULL");
                using (procedure.Indent())
                    procedure.Line($"THROW {TaskNotFoundErrorNumber}, 'The SharpSql continuation task does not exist.', 1;");
                procedure.Line("SET @SuspensionGeneration = COALESCE(@SuspensionGeneration, @CurrentGeneration);");
                procedure.Line("IF @SuspensionGeneration <> @CurrentGeneration");
                using (procedure.Indent())
                    procedure.Line($"THROW {InvalidTaskJoinErrorNumber}, 'The SharpSql dependency targets a stale suspension generation.', 1;");
                procedure.Line();
                procedure.Line($"SELECT @DependencyState = [State] FROM [{SchemaName}].[{TasksTableName}] WITH (UPDLOCK, HOLDLOCK)");
                procedure.Line("WHERE [ExecutionId] = @ExecutionId AND [TaskId] = @DependencyTaskId;");
                procedure.Line("IF @DependencyState IS NULL");
                using (procedure.Indent())
                    procedure.Line($"THROW {TaskNotFoundErrorNumber}, 'The SharpSql dependency task does not exist.', 1;");
                procedure.Line();
                procedure.Line("SELECT");
                using (procedure.Indent())
                {
                    procedure.Line("@ExpectedDependencyCount = [ExpectedDependencyCount],");
                    procedure.Line("@RegisteredDependencyCount = [RegisteredDependencyCount]");
                }
                procedure.Line($"FROM [{SchemaName}].[{TaskJoinsTableName}] WITH (UPDLOCK, HOLDLOCK)");
                procedure.Line("WHERE [ExecutionId] = @ExecutionId");
                using (procedure.Indent())
                {
                    procedure.Line("AND [ContinuationTaskId] = @ContinuationTaskId");
                    procedure.Line("AND [SuspensionGeneration] = @SuspensionGeneration;");
                }
                procedure.Line("IF @ExpectedDependencyCount IS NULL");
                using (procedure.Indent())
                    procedure.Line($"THROW {InvalidTaskJoinErrorNumber}, 'The SharpSql task join does not exist.', 1;");
                procedure.Line();
                procedure.Line($"IF NOT EXISTS (SELECT 1 FROM [{SchemaName}].[{TaskDependenciesTableName}] WITH (UPDLOCK, HOLDLOCK)");
                using (procedure.Indent())
                {
                    procedure.Line("WHERE [ExecutionId] = @ExecutionId");
                    procedure.Line("AND [ContinuationTaskId] = @ContinuationTaskId");
                    procedure.Line("AND [SuspensionGeneration] = @SuspensionGeneration");
                    procedure.Line("AND [DependencyTaskId] = @DependencyTaskId)");
                }
                procedure.Line("BEGIN");
                using (procedure.Indent())
                {
                    procedure.Line("IF @RegisteredDependencyCount >= @ExpectedDependencyCount");
                    using (procedure.Indent())
                        procedure.Line($"THROW {InvalidTaskJoinErrorNumber}, 'The SharpSql task join already has its expected dependency count.', 1;");
                    procedure.Line();
                    procedure.Line($"INSERT INTO [{SchemaName}].[{TaskDependenciesTableName}] (");
                    using (procedure.Indent())
                    {
                        procedure.Line("[ExecutionId], [ContinuationTaskId], [SuspensionGeneration], [DependencyTaskId],");
                        procedure.Line("[DependencyState], [CompletedAtUtc]");
                    }
                    procedure.Line(") VALUES (");
                    using (procedure.Indent())
                    {
                        procedure.Line("@ExecutionId, @ContinuationTaskId, @SuspensionGeneration, @DependencyTaskId,");
                        procedure.Line("CASE WHEN @DependencyState BETWEEN 4 AND 6 THEN @DependencyState END,");
                        procedure.Line("CASE WHEN @DependencyState BETWEEN 4 AND 6 THEN @NowUtc END");
                    }
                    procedure.Line(");");
                    procedure.Line();
                    procedure.Line($"UPDATE [{SchemaName}].[{TaskJoinsTableName}]");
                    procedure.Line("SET");
                    using (procedure.Indent())
                    {
                        procedure.Line("[RegisteredDependencyCount] = [RegisteredDependencyCount] + 1,");
                        procedure.Line("[CompletedDependencyCount] = [CompletedDependencyCount] + CASE WHEN @DependencyState BETWEEN 4 AND 6 THEN 1 ELSE 0 END");
                    }
                    procedure.Line("WHERE [ExecutionId] = @ExecutionId");
                    using (procedure.Indent())
                    {
                        procedure.Line("AND [ContinuationTaskId] = @ContinuationTaskId");
                        procedure.Line("AND [SuspensionGeneration] = @SuspensionGeneration;");
                    }
                    procedure.Line("SET @Inserted = 1;");
                }
                procedure.Line("END;");
                procedure.Line();
                procedure.Line($"UPDATE [{SchemaName}].[{TaskJoinsTableName}] WITH (UPDLOCK, ROWLOCK)");
                procedure.Line("SET [ReadyAtUtc] = @NowUtc");
                procedure.Line("OUTPUT INSERTED.[ContinuationTaskId], INSERTED.[SuspensionGeneration]");
                procedure.Line("INTO @Ready ([ContinuationTaskId], [SuspensionGeneration])");
                procedure.Line("WHERE [ExecutionId] = @ExecutionId");
                using (procedure.Indent())
                {
                    procedure.Line("AND [ContinuationTaskId] = @ContinuationTaskId");
                    procedure.Line("AND [SuspensionGeneration] = @SuspensionGeneration");
                    procedure.Line("AND [ReadyAtUtc] IS NULL");
                    procedure.Line("AND [RegisteredDependencyCount] = [ExpectedDependencyCount]");
                    procedure.Line("AND [CompletedDependencyCount] = [ExpectedDependencyCount];");
                }
                procedure.Line();
                procedure.Line("IF EXISTS (SELECT 1 FROM @Ready)");
                procedure.Line("BEGIN");
                using (procedure.Indent())
                {
                    procedure.Line("SET @JoinReady = 1;");
                    procedure.Line($"UPDATE [{SchemaName}].[{TasksTableName}] WITH (UPDLOCK, ROWLOCK)");
                    procedure.Line("SET [State] = 1, [ReadyAtUtc] = @NowUtc");
                    procedure.Line("WHERE [ExecutionId] = @ExecutionId AND [TaskId] = @ContinuationTaskId");
                    using (procedure.Indent())
                        procedure.Line("AND [SuspensionGeneration] = @SuspensionGeneration AND [State] = 0;");
                    procedure.Line();
                    procedure.Line($"EXEC [{SchemaName}].[{EnqueueTaskProcedureName}]");
                    using (procedure.Indent())
                    {
                        procedure.Line("@ExecutionId = @ExecutionId,");
                        procedure.Line("@TaskId = @ContinuationTaskId,");
                        procedure.Line("@Enqueued = @Enqueued OUTPUT;");
                    }
                    procedure.Line($"UPDATE [{SchemaName}].[{TaskJoinsTableName}]");
                    procedure.Line("SET [EnqueuedAtUtc] = CASE WHEN @Enqueued = 1 THEN SYSUTCDATETIME() ELSE [EnqueuedAtUtc] END");
                    procedure.Line("WHERE [ExecutionId] = @ExecutionId AND [ContinuationTaskId] = @ContinuationTaskId AND [SuspensionGeneration] = @SuspensionGeneration;");
                }
                procedure.Line("END;");
                procedure.Line();
                EmitProcedureTransactionCommit(procedure);
                procedure.Line("SELECT @Inserted AS [Registered], @JoinReady AS [JoinReady], @Enqueued AS [Enqueued], @SuspensionGeneration AS [SuspensionGeneration];");
            }
            procedure.Line("END TRY");
            procedure.Line("BEGIN CATCH");
            using (procedure.Indent())
            {
                EmitProcedureTransactionRollback(procedure, "SharpSqlRegisterDependency");
                procedure.Line("THROW;");
            }
            procedure.Line("END CATCH;");
        }
        procedure.Line("END;");

        EmitProcedureBatch(sql, procedure);
    }

    private static void EmitCompleteTaskProcedure(SqlWriter sql)
    {
        var procedure = new SqlWriter();
        procedure.Line($"CREATE OR ALTER PROCEDURE [{SchemaName}].[{CompleteTaskProcedureName}]");
        using (procedure.Indent())
        {
            procedure.Line("@ExecutionId UNIQUEIDENTIFIER,");
            procedure.Line("@TaskId BIGINT,");
            procedure.Line("@State TINYINT,");
            procedure.Line("@ResultKind TINYINT = 0,");
            procedure.Line("@ResultScalar SQL_VARIANT = NULL,");
            procedure.Line("@ResultText NVARCHAR(MAX) = NULL,");
            procedure.Line("@ResultBinary VARBINARY(MAX) = NULL,");
            procedure.Line("@ResultReferenceId BIGINT = NULL,");
            procedure.Line("@ErrorNumber INT = NULL,");
            procedure.Line("@ErrorMessage NVARCHAR(MAX) = NULL");
        }
        procedure.Line("AS");
        procedure.Line("BEGIN");
        using (procedure.Indent())
        {
            procedure.Line("SET NOCOUNT ON;");
            EmitProcedureTransactionDeclaration(procedure);
            procedure.Line();
            procedure.Line("IF @State IS NULL OR @State NOT IN (4, 5, 6)");
            using (procedure.Indent())
                procedure.Line($"THROW {InvalidTaskStateErrorNumber}, 'A terminal task state must be succeeded (4), faulted (5), or canceled (6).', 1;");
            procedure.Line("IF @ResultKind IS NULL OR @ResultKind NOT BETWEEN 0 AND 4");
            using (procedure.Indent())
                procedure.Line($"THROW {InvalidTaskResultErrorNumber}, 'A SharpSql task result kind must be none (0), scalar (1), text (2), binary (3), or reference (4).', 1;");
            procedure.Line("IF @State <> 4 AND (@ResultKind <> 0 OR @ResultScalar IS NOT NULL OR @ResultText IS NOT NULL OR @ResultBinary IS NOT NULL OR @ResultReferenceId IS NOT NULL)");
            using (procedure.Indent())
                procedure.Line($"THROW {InvalidTaskResultErrorNumber}, 'Only a succeeded SharpSql task can store a result.', 1;");
            procedure.Line("IF (@ResultKind <> 1 AND @ResultScalar IS NOT NULL)");
            using (procedure.Indent())
                procedure.Line($"THROW {InvalidTaskResultErrorNumber}, 'The scalar result column does not match the task result kind.', 1;");
            procedure.Line("IF (@ResultKind <> 2 AND @ResultText IS NOT NULL)");
            using (procedure.Indent())
                procedure.Line($"THROW {InvalidTaskResultErrorNumber}, 'The text result column does not match the task result kind.', 1;");
            procedure.Line("IF (@ResultKind <> 3 AND @ResultBinary IS NOT NULL)");
            using (procedure.Indent())
                procedure.Line($"THROW {InvalidTaskResultErrorNumber}, 'The binary result column does not match the task result kind.', 1;");
            procedure.Line("IF (@ResultKind <> 4 AND @ResultReferenceId IS NOT NULL)");
            using (procedure.Indent())
                procedure.Line($"THROW {InvalidTaskResultErrorNumber}, 'The reference result column does not match the task result kind.', 1;");
            procedure.Line();
            procedure.Line("DECLARE @NowUtc DATETIME2(7) = SYSUTCDATETIME();");
            procedure.Line("DECLARE @Completion TABLE ([SuspensionGeneration] INT NOT NULL);");
            procedure.Line("DECLARE @BytecodeFrames TABLE ([FrameId] BIGINT NOT NULL PRIMARY KEY);");
            procedure.Line("DECLARE @Affected TABLE (");
            using (procedure.Indent())
            {
                procedure.Line("[ContinuationTaskId] BIGINT NOT NULL,");
                procedure.Line("[SuspensionGeneration] INT NOT NULL,");
                procedure.Line("PRIMARY KEY ([ContinuationTaskId], [SuspensionGeneration])");
            }
            procedure.Line(");");
            procedure.Line("DECLARE @Ready TABLE (");
            using (procedure.Indent())
            {
                procedure.Line("[ContinuationTaskId] BIGINT NOT NULL,");
                procedure.Line("[SuspensionGeneration] INT NOT NULL,");
                procedure.Line("PRIMARY KEY ([ContinuationTaskId], [SuspensionGeneration])");
            }
            procedure.Line(");");
            procedure.Line("DECLARE @ContinuationTaskId BIGINT;");
            procedure.Line("DECLARE @SuspensionGeneration INT;");
            procedure.Line("DECLARE @Enqueued BIT;");
            procedure.Line("DECLARE @CoordinationLockResult INT;");
            procedure.Line("DECLARE @CoordinationLockName NVARCHAR(255) = N'SharpSql.TaskJoin.' + CONVERT(NVARCHAR(36), @ExecutionId);");
            procedure.Line();
            procedure.Line("BEGIN TRY");
            using (procedure.Indent())
            {
                EmitProcedureTransactionStart(procedure, "SharpSqlCompleteTask");
                procedure.Line();
                EmitTaskJoinCoordinationLock(procedure);
                procedure.Line();
                procedure.Line($"UPDATE [{SchemaName}].[{TasksTableName}] WITH (UPDLOCK, ROWLOCK)");
                procedure.Line("SET");
                using (procedure.Indent())
                {
                    procedure.Line("[State] = @State,");
                    procedure.Line("[ResultKind] = CASE WHEN @State = 4 THEN @ResultKind ELSE 0 END,");
                    procedure.Line("[ResultScalar] = CASE WHEN @State = 4 AND @ResultKind = 1 THEN @ResultScalar END,");
                    procedure.Line("[ResultText] = CASE WHEN @State = 4 AND @ResultKind = 2 THEN @ResultText END,");
                    procedure.Line("[ResultBinary] = CASE WHEN @State = 4 AND @ResultKind = 3 THEN @ResultBinary END,");
                    procedure.Line("[ResultReferenceId] = CASE WHEN @State = 4 AND @ResultKind = 4 THEN @ResultReferenceId END,");
                    procedure.Line("[ErrorNumber] = CASE WHEN @State = 4 THEN NULL ELSE @ErrorNumber END,");
                    procedure.Line("[ErrorMessage] = CASE WHEN @State = 4 THEN NULL ELSE @ErrorMessage END,");
                    procedure.Line("[CompletedAtUtc] = @NowUtc");
                }
                procedure.Line("OUTPUT INSERTED.[SuspensionGeneration] INTO @Completion ([SuspensionGeneration])");
                procedure.Line("WHERE [ExecutionId] = @ExecutionId AND [TaskId] = @TaskId AND [State] NOT BETWEEN 4 AND 6;");
                procedure.Line();
                procedure.Line("IF NOT EXISTS (SELECT 1 FROM @Completion)");
                procedure.Line("BEGIN");
                using (procedure.Indent())
                {
                    procedure.Line($"IF NOT EXISTS (SELECT 1 FROM [{SchemaName}].[{TasksTableName}] WITH (UPDLOCK, HOLDLOCK) WHERE [ExecutionId] = @ExecutionId AND [TaskId] = @TaskId)");
                    using (procedure.Indent())
                        procedure.Line($"THROW {TaskNotFoundErrorNumber}, 'The SharpSql task does not exist.', 1;");
                    EmitProcedureTransactionCommit(procedure);
                    procedure.Line("SELECT CAST(0 AS BIT) AS [Completed], CAST(0 AS INT) AS [ContinuationsEnqueued];");
                    procedure.Line("RETURN;");
                }
                procedure.Line("END;");
                procedure.Line();
                procedure.Line(";WITH [BytecodeFrameChain] AS (");
                using (procedure.Indent())
                {
                    procedure.Line("SELECT [activation].[CurrentFrameId] AS [FrameId]");
                    procedure.Line($"FROM [{SchemaName}].[{BytecodeActivationsTableName}] AS [activation]");
                    procedure.Line("WHERE [activation].[ExecutionId] = @ExecutionId AND [activation].[TaskId] = @TaskId");
                    procedure.Line("UNION ALL");
                    procedure.Line("SELECT CONVERT(BIGINT, [frame].[__caller_id])");
                    procedure.Line($"FROM {RegisterBytecodeRuntimeSqlEmitter.FramesTable} AS [frame]");
                    procedure.Line("INNER JOIN [BytecodeFrameChain] AS [chain] ON [chain].[FrameId] = [frame].[__id]");
                    procedure.Line("WHERE [frame].[__execution_id] = @ExecutionId AND [frame].[__caller_id] IS NOT NULL");
                }
                procedure.Line(")");
                procedure.Line("INSERT INTO @BytecodeFrames ([FrameId]) SELECT [FrameId] FROM [BytecodeFrameChain] OPTION (MAXRECURSION 32767);");
                procedure.Line($"DELETE FROM [{SchemaName}].[{BytecodeActivationsTableName}] WHERE [ExecutionId] = @ExecutionId AND [TaskId] = @TaskId;");
                procedure.Line($"DELETE [register] FROM {RegisterBytecodeRuntimeSqlEmitter.RegistersTable} AS [register] INNER JOIN @BytecodeFrames AS [frame] ON [frame].[FrameId] = [register].[__frame_id] WHERE [register].[__execution_id] = @ExecutionId;");
                procedure.Line($"DELETE [stored] FROM {RegisterBytecodeRuntimeSqlEmitter.FramesTable} AS [stored] INNER JOIN @BytecodeFrames AS [frame] ON [frame].[FrameId] = [stored].[__id] WHERE [stored].[__execution_id] = @ExecutionId;");
                procedure.Line();
                procedure.Line($"UPDATE [{SchemaName}].[{TaskTimersTableName}]");
                procedure.Line("SET [State] = 3, [ClaimedAtUtc] = COALESCE([ClaimedAtUtc], @NowUtc)");
                procedure.Line("WHERE [ExecutionId] = @ExecutionId AND [TaskId] = @TaskId AND [State] = 0;");
                procedure.Line();
                procedure.Line($"UPDATE [{SchemaName}].[{TaskDependenciesTableName}] WITH (UPDLOCK, ROWLOCK)");
                procedure.Line("SET [DependencyState] = @State, [CompletedAtUtc] = @NowUtc");
                procedure.Line("OUTPUT INSERTED.[ContinuationTaskId], INSERTED.[SuspensionGeneration]");
                procedure.Line("INTO @Affected ([ContinuationTaskId], [SuspensionGeneration])");
                procedure.Line("WHERE [ExecutionId] = @ExecutionId AND [DependencyTaskId] = @TaskId AND [CompletedAtUtc] IS NULL;");
                procedure.Line();
                procedure.Line(";WITH [AffectedCounts] AS (");
                using (procedure.Indent())
                {
                    procedure.Line("SELECT [ContinuationTaskId], [SuspensionGeneration], COUNT_BIG(*) AS [CompletedCount]");
                    procedure.Line("FROM @Affected");
                    procedure.Line("GROUP BY [ContinuationTaskId], [SuspensionGeneration]");
                }
                procedure.Line(")");
                procedure.Line("UPDATE [join]");
                procedure.Line("SET [CompletedDependencyCount] = [join].[CompletedDependencyCount] + CONVERT(INT, [affected].[CompletedCount])");
                procedure.Line($"FROM [{SchemaName}].[{TaskJoinsTableName}] AS [join] WITH (UPDLOCK, ROWLOCK)");
                procedure.Line("INNER JOIN [AffectedCounts] AS [affected]");
                using (procedure.Indent())
                {
                    procedure.Line("ON [affected].[ContinuationTaskId] = [join].[ContinuationTaskId]");
                    procedure.Line("AND [affected].[SuspensionGeneration] = [join].[SuspensionGeneration]");
                }
                procedure.Line("WHERE [join].[ExecutionId] = @ExecutionId;");
                procedure.Line();
                procedure.Line($"UPDATE [join]");
                procedure.Line("SET [ReadyAtUtc] = @NowUtc");
                procedure.Line("OUTPUT INSERTED.[ContinuationTaskId], INSERTED.[SuspensionGeneration]");
                procedure.Line("INTO @Ready ([ContinuationTaskId], [SuspensionGeneration])");
                procedure.Line($"FROM [{SchemaName}].[{TaskJoinsTableName}] AS [join] WITH (UPDLOCK, ROWLOCK)");
                procedure.Line("WHERE [join].[ExecutionId] = @ExecutionId");
                using (procedure.Indent())
                {
                    procedure.Line("AND [join].[ReadyAtUtc] IS NULL");
                    procedure.Line("AND [join].[RegisteredDependencyCount] = [join].[ExpectedDependencyCount]");
                    procedure.Line("AND [join].[CompletedDependencyCount] = [join].[ExpectedDependencyCount]");
                    procedure.Line("AND EXISTS (");
                    using (procedure.Indent())
                    {
                        procedure.Line("SELECT 1 FROM @Affected AS [affected]");
                        procedure.Line("WHERE [affected].[ContinuationTaskId] = [join].[ContinuationTaskId]");
                        using (procedure.Indent())
                            procedure.Line("AND [affected].[SuspensionGeneration] = [join].[SuspensionGeneration]");
                    }
                    procedure.Line(");");
                }
                procedure.Line();
                procedure.Line($"UPDATE [task]");
                procedure.Line("SET [State] = 1, [ReadyAtUtc] = @NowUtc");
                procedure.Line($"FROM [{SchemaName}].[{TasksTableName}] AS [task] WITH (UPDLOCK, ROWLOCK)");
                procedure.Line("INNER JOIN @Ready AS [ready]");
                using (procedure.Indent())
                {
                    procedure.Line("ON [ready].[ContinuationTaskId] = [task].[TaskId]");
                    procedure.Line("AND [ready].[SuspensionGeneration] = [task].[SuspensionGeneration]");
                }
                procedure.Line("WHERE [task].[ExecutionId] = @ExecutionId AND [task].[State] = 0;");
                procedure.Line();
                procedure.Line("DECLARE [ready_cursor] CURSOR LOCAL FAST_FORWARD FOR");
                procedure.Line("SELECT [ContinuationTaskId], [SuspensionGeneration] FROM @Ready ORDER BY [ContinuationTaskId];");
                procedure.Line("OPEN [ready_cursor];");
                procedure.Line("FETCH NEXT FROM [ready_cursor] INTO @ContinuationTaskId, @SuspensionGeneration;");
                procedure.Line("WHILE @@FETCH_STATUS = 0");
                procedure.Line("BEGIN");
                using (procedure.Indent())
                {
                    procedure.Line("SET @Enqueued = 0;");
                    procedure.Line($"EXEC [{SchemaName}].[{EnqueueTaskProcedureName}]");
                    using (procedure.Indent())
                    {
                        procedure.Line("@ExecutionId = @ExecutionId,");
                        procedure.Line("@TaskId = @ContinuationTaskId,");
                        procedure.Line("@Enqueued = @Enqueued OUTPUT;");
                    }
                    procedure.Line($"UPDATE [{SchemaName}].[{TaskJoinsTableName}]");
                    procedure.Line("SET [EnqueuedAtUtc] = CASE WHEN @Enqueued = 1 THEN SYSUTCDATETIME() ELSE [EnqueuedAtUtc] END");
                    procedure.Line("WHERE [ExecutionId] = @ExecutionId AND [ContinuationTaskId] = @ContinuationTaskId AND [SuspensionGeneration] = @SuspensionGeneration;");
                    procedure.Line("FETCH NEXT FROM [ready_cursor] INTO @ContinuationTaskId, @SuspensionGeneration;");
                }
                procedure.Line("END;");
                procedure.Line("CLOSE [ready_cursor];");
                procedure.Line("DEALLOCATE [ready_cursor];");
                procedure.Line();
                EmitProcedureTransactionCommit(procedure);
                procedure.Line("SELECT CAST(1 AS BIT) AS [Completed], (SELECT COUNT(*) FROM @Ready) AS [ContinuationsEnqueued];");
            }
            procedure.Line("END TRY");
            procedure.Line("BEGIN CATCH");
            using (procedure.Indent())
            {
                procedure.Line("IF CURSOR_STATUS('local', 'ready_cursor') >= 0 CLOSE [ready_cursor];");
                procedure.Line("IF CURSOR_STATUS('local', 'ready_cursor') >= -1 DEALLOCATE [ready_cursor];");
                EmitProcedureTransactionRollback(procedure, "SharpSqlCompleteTask");
                procedure.Line("THROW;");
            }
            procedure.Line("END CATCH;");
        }
        procedure.Line("END;");

        EmitProcedureBatch(sql, procedure);
    }

    private static void EmitClaimDueContinuationsProcedure(SqlWriter sql)
    {
        var procedure = new SqlWriter();
        procedure.Line($"CREATE OR ALTER PROCEDURE [{SchemaName}].[{ClaimDueContinuationsProcedureName}]");
        using (procedure.Indent())
            procedure.Line("@BatchSize INT = 100");
        procedure.Line("AS");
        procedure.Line("BEGIN");
        using (procedure.Indent())
        {
            procedure.Line("SET NOCOUNT ON;");
            EmitProcedureTransactionDeclaration(procedure);
            procedure.Line();
            procedure.Line("IF @BatchSize IS NULL OR @BatchSize NOT BETWEEN 1 AND 1000");
            using (procedure.Indent())
                procedure.Line($"THROW {InvalidClaimBatchSizeErrorNumber}, 'The SharpSql continuation claim batch size must be between 1 and 1000.', 1;");
            procedure.Line();
            procedure.Line("DECLARE @NowUtc DATETIME2(7) = SYSUTCDATETIME();");
            procedure.Line("DECLARE @Candidates TABLE (");
            using (procedure.Indent())
            {
                procedure.Line("[ExecutionId] UNIQUEIDENTIFIER NOT NULL,");
                procedure.Line("[TaskId] BIGINT NOT NULL,");
                procedure.Line("[SuspensionGeneration] INT NOT NULL,");
                procedure.Line("[Source] TINYINT NOT NULL,");
                procedure.Line("[OrderAtUtc] DATETIME2(7) NOT NULL,");
                procedure.Line("PRIMARY KEY ([ExecutionId], [TaskId])");
            }
            procedure.Line(");");
            procedure.Line("DECLARE @Results TABLE (");
            using (procedure.Indent())
            {
                procedure.Line("[ExecutionId] UNIQUEIDENTIFIER NOT NULL,");
                procedure.Line("[TaskId] BIGINT NOT NULL,");
                procedure.Line("[SuspensionGeneration] INT NOT NULL,");
                procedure.Line("[Enqueued] BIT NOT NULL");
            }
            procedure.Line(");");
            procedure.Line("DECLARE @ExecutionId UNIQUEIDENTIFIER;");
            procedure.Line("DECLARE @TaskId BIGINT;");
            procedure.Line("DECLARE @SuspensionGeneration INT;");
            procedure.Line("DECLARE @Source TINYINT;");
            procedure.Line("DECLARE @Enqueued BIT;");
            procedure.Line();
            procedure.Line("BEGIN TRY");
            using (procedure.Indent())
            {
                EmitProcedureTransactionStart(procedure, "SharpSqlClaimDue");
                procedure.Line();
                procedure.Line(";WITH [Due] AS (");
                using (procedure.Indent())
                {
                    procedure.Line("SELECT TOP (@BatchSize)");
                    using (procedure.Indent())
                        procedure.Line("[timer].[ExecutionId], [timer].[TaskId], [timer].[SuspensionGeneration]");
                    procedure.Line($"FROM [{SchemaName}].[{TaskTimersTableName}] AS [timer] WITH (UPDLOCK, READPAST, ROWLOCK)");
                    procedure.Line($"INNER JOIN [{SchemaName}].[{TasksTableName}] AS [task]");
                    using (procedure.Indent())
                    {
                        procedure.Line("ON [task].[ExecutionId] = [timer].[ExecutionId]");
                        procedure.Line("AND [task].[TaskId] = [timer].[TaskId]");
                        procedure.Line("AND [task].[SuspensionGeneration] = [timer].[SuspensionGeneration]");
                    }
                    procedure.Line("WHERE [timer].[State] = 0 AND [timer].[DueAtUtc] <= CONVERT(DATETIME2(3), @NowUtc) AND [task].[State] = 0");
                    procedure.Line("ORDER BY [timer].[DueAtUtc], [timer].[ExecutionId], [timer].[TaskId]");
                }
                procedure.Line(")");
                procedure.Line("UPDATE [timer]");
                procedure.Line("SET [State] = 1, [ClaimedAtUtc] = @NowUtc");
                procedure.Line("OUTPUT INSERTED.[ExecutionId], INSERTED.[TaskId], INSERTED.[SuspensionGeneration], CAST(1 AS TINYINT), INSERTED.[DueAtUtc]");
                procedure.Line("INTO @Candidates ([ExecutionId], [TaskId], [SuspensionGeneration], [Source], [OrderAtUtc])");
                procedure.Line($"FROM [{SchemaName}].[{TaskTimersTableName}] AS [timer]");
                procedure.Line("INNER JOIN [Due] AS [due]");
                using (procedure.Indent())
                {
                    procedure.Line("ON [due].[ExecutionId] = [timer].[ExecutionId]");
                    procedure.Line("AND [due].[TaskId] = [timer].[TaskId]");
                    procedure.Line("AND [due].[SuspensionGeneration] = [timer].[SuspensionGeneration];");
                }
                procedure.Line();
                procedure.Line($"UPDATE [task]");
                procedure.Line("SET [State] = 1, [ReadyAtUtc] = @NowUtc");
                procedure.Line($"FROM [{SchemaName}].[{TasksTableName}] AS [task] WITH (UPDLOCK, ROWLOCK)");
                procedure.Line("INNER JOIN @Candidates AS [candidate]");
                using (procedure.Indent())
                {
                    procedure.Line("ON [candidate].[ExecutionId] = [task].[ExecutionId]");
                    procedure.Line("AND [candidate].[TaskId] = [task].[TaskId]");
                    procedure.Line("AND [candidate].[SuspensionGeneration] = [task].[SuspensionGeneration]");
                }
                procedure.Line("WHERE [task].[State] = 0;");
                procedure.Line();
                procedure.Line("INSERT INTO @Candidates ([ExecutionId], [TaskId], [SuspensionGeneration], [Source], [OrderAtUtc])");
                procedure.Line("SELECT TOP (@BatchSize - (SELECT COUNT(*) FROM @Candidates))");
                using (procedure.Indent())
                    procedure.Line("[task].[ExecutionId], [task].[TaskId], [task].[SuspensionGeneration], CAST(0 AS TINYINT), COALESCE([task].[ReadyAtUtc], @NowUtc)");
                procedure.Line($"FROM [{SchemaName}].[{TasksTableName}] AS [task] WITH (UPDLOCK, READPAST, ROWLOCK)");
                procedure.Line("WHERE [task].[State] = 1");
                using (procedure.Indent())
                {
                    procedure.Line("AND NOT EXISTS (");
                    using (procedure.Indent())
                    {
                        procedure.Line("SELECT 1 FROM @Candidates AS [candidate]");
                        procedure.Line("WHERE [candidate].[ExecutionId] = [task].[ExecutionId] AND [candidate].[TaskId] = [task].[TaskId]");
                    }
                    procedure.Line(")");
                }
                procedure.Line("ORDER BY [task].[ReadyAtUtc], [task].[ExecutionId], [task].[TaskId];");
                procedure.Line();
                procedure.Line("DECLARE [due_cursor] CURSOR LOCAL FAST_FORWARD FOR");
                procedure.Line("SELECT [ExecutionId], [TaskId], [SuspensionGeneration], [Source]");
                procedure.Line("FROM @Candidates ORDER BY [Source] DESC, [OrderAtUtc], [ExecutionId], [TaskId];");
                procedure.Line("OPEN [due_cursor];");
                procedure.Line("FETCH NEXT FROM [due_cursor] INTO @ExecutionId, @TaskId, @SuspensionGeneration, @Source;");
                procedure.Line("WHILE @@FETCH_STATUS = 0");
                procedure.Line("BEGIN");
                using (procedure.Indent())
                {
                    procedure.Line("SET @Enqueued = 0;");
                    procedure.Line($"EXEC [{SchemaName}].[{EnqueueTaskProcedureName}]");
                    using (procedure.Indent())
                    {
                        procedure.Line("@ExecutionId = @ExecutionId,");
                        procedure.Line("@TaskId = @TaskId,");
                        procedure.Line("@Enqueued = @Enqueued OUTPUT;");
                    }
                    procedure.Line();
                    procedure.Line("IF @Source = 1");
                    procedure.Line("BEGIN");
                    using (procedure.Indent())
                    {
                        procedure.Line($"UPDATE [{SchemaName}].[{TaskTimersTableName}]");
                        procedure.Line("SET");
                        using (procedure.Indent())
                        {
                            procedure.Line("[State] = CASE WHEN @Enqueued = 1 THEN 2 ELSE 3 END,");
                            procedure.Line("[EnqueuedAtUtc] = CASE WHEN @Enqueued = 1 THEN SYSUTCDATETIME() ELSE NULL END");
                        }
                        procedure.Line("WHERE [ExecutionId] = @ExecutionId AND [TaskId] = @TaskId AND [SuspensionGeneration] = @SuspensionGeneration;");
                    }
                    procedure.Line("END;");
                    procedure.Line();
                    procedure.Line($"UPDATE [{SchemaName}].[{TaskJoinsTableName}]");
                    procedure.Line("SET [EnqueuedAtUtc] = CASE WHEN @Enqueued = 1 THEN COALESCE([EnqueuedAtUtc], SYSUTCDATETIME()) ELSE [EnqueuedAtUtc] END");
                    procedure.Line("WHERE [ExecutionId] = @ExecutionId AND [ContinuationTaskId] = @TaskId AND [SuspensionGeneration] = @SuspensionGeneration AND [ReadyAtUtc] IS NOT NULL;");
                    procedure.Line();
                    procedure.Line("INSERT INTO @Results ([ExecutionId], [TaskId], [SuspensionGeneration], [Enqueued])");
                    procedure.Line("VALUES (@ExecutionId, @TaskId, @SuspensionGeneration, @Enqueued);");
                    procedure.Line("FETCH NEXT FROM [due_cursor] INTO @ExecutionId, @TaskId, @SuspensionGeneration, @Source;");
                }
                procedure.Line("END;");
                procedure.Line("CLOSE [due_cursor];");
                procedure.Line("DEALLOCATE [due_cursor];");
                procedure.Line();
                EmitProcedureTransactionCommit(procedure);
                procedure.Line("SELECT [ExecutionId], [TaskId], [SuspensionGeneration], [Enqueued] FROM @Results ORDER BY [ExecutionId], [TaskId];");
            }
            procedure.Line("END TRY");
            procedure.Line("BEGIN CATCH");
            using (procedure.Indent())
            {
                procedure.Line("IF CURSOR_STATUS('local', 'due_cursor') >= 0 CLOSE [due_cursor];");
                procedure.Line("IF CURSOR_STATUS('local', 'due_cursor') >= -1 DEALLOCATE [due_cursor];");
                EmitProcedureTransactionRollback(procedure, "SharpSqlClaimDue");
                procedure.Line("THROW;");
            }
            procedure.Line("END CATCH;");
        }
        procedure.Line("END;");

        EmitProcedureBatch(sql, procedure);
    }

    private static void EmitTaskJoinCoordinationLock(SqlWriter procedure)
    {
        procedure.Line("EXEC @CoordinationLockResult = sys.sp_getapplock");
        using (procedure.Indent())
        {
            procedure.Line("@Resource = @CoordinationLockName,");
            procedure.Line("@LockMode = N'Exclusive',");
            procedure.Line("@LockOwner = N'Transaction',");
            procedure.Line("@LockTimeout = 60000,");
            procedure.Line("@DbPrincipal = N'public';");
        }
        procedure.Line("IF @CoordinationLockResult < 0");
        using (procedure.Indent())
            procedure.Line($"THROW {InvalidTaskJoinErrorNumber}, 'Could not acquire the SharpSql task-join coordination lock.', 1;");
    }

    private static void EmitProcedureTransactionDeclaration(SqlWriter procedure)
    {
        // Runtime procedures can execute inside a Service Broker activation transaction.
        // XACT_ABORT must be OFF so catchable statement errors can roll back to the
        // procedure savepoint without silently dooming the activation transaction.
        procedure.Line("SET XACT_ABORT OFF;");
        procedure.Line("DECLARE @OwnTransaction BIT = 0;");
    }

    private static void EmitProcedureTransactionStart(SqlWriter procedure, string savepointName)
    {
        procedure.Line("IF @@TRANCOUNT = 0");
        procedure.Line("BEGIN");
        using (procedure.Indent())
        {
            procedure.Line("SET @OwnTransaction = 1;");
            procedure.Line("BEGIN TRANSACTION;");
        }
        procedure.Line("END");
        procedure.Line("ELSE");
        using (procedure.Indent())
            procedure.Line($"SAVE TRANSACTION [{savepointName}];");
    }

    private static void EmitProcedureTransactionCommit(SqlWriter procedure)
    {
        procedure.Line("IF @OwnTransaction = 1 COMMIT TRANSACTION;");
    }

    private static void EmitProcedureTransactionRollback(SqlWriter procedure, string savepointName)
    {
        procedure.Line("IF @OwnTransaction = 1 AND XACT_STATE() <> 0");
        using (procedure.Indent())
            procedure.Line("ROLLBACK TRANSACTION;");
        procedure.Line("ELSE IF @OwnTransaction = 0 AND XACT_STATE() = 1");
        using (procedure.Indent())
            procedure.Line($"ROLLBACK TRANSACTION [{savepointName}];");
    }

    private static void EmitProcedureBatch(SqlWriter sql, SqlWriter procedure)
    {
        var batch = procedure.ToString().TrimEnd().Replace("'", "''");
        sql.Line($"EXEC(N'{batch}');");
    }
}
