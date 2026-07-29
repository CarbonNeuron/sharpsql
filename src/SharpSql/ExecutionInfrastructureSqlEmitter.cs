namespace SharpSql;

/// <summary>
/// Emits the permanent, database-scoped objects used to coordinate SharpSql executions.
/// This is intentionally separate from program lowering so provisioning can happen once
/// without making an individual transpiled batch own shared infrastructure.
/// </summary>
internal static class ExecutionInfrastructureSqlEmitter
{
    internal const string SchemaName = "SharpSql";
    internal const string ExecutionsTableName = "Executions";
    internal const string OutputEventsTableName = "OutputEvents";
    internal const string TasksTableName = "Tasks";
    internal const string TaskTimersTableName = "TaskTimers";
    internal const string TaskJoinsTableName = "TaskJoins";
    internal const string TaskDependenciesTableName = "TaskDependencies";
    internal const string AppendOutputProcedureName = "AppendOutput";
    internal const string CompleteExecutionProcedureName = "CompleteExecution";
    internal const string EnqueueTaskProcedureName = "EnqueueTask";
    internal const string ScheduleTaskProcedureName = "ScheduleTask";
    internal const string SuspendTaskForDelayProcedureName = "SuspendTaskForDelay";
    internal const string SuspendTaskForDependenciesProcedureName = "SuspendTaskForDependencies";
    internal const string RegisterTaskDependencyProcedureName = "RegisterTaskDependency";
    internal const string CompleteTaskProcedureName = "CompleteTask";
    internal const string ClaimDueContinuationsProcedureName = "ClaimDueContinuations";
    internal const string LauncherQueueName = "LauncherQueue";
    internal const string WorkerQueueName = "WorkerQueue";

    internal const string RequestMessageTypeName = "//sharpsql/v1/execution/request";
    internal const string OutputMessageTypeName = "//sharpsql/v1/execution/output";
    internal const string CompletedMessageTypeName = "//sharpsql/v1/execution/completed";
    internal const string ContractName = "//sharpsql/v1/execution/contract";
    internal const string LauncherServiceName = "//sharpsql/v1/launcher";
    internal const string WorkerServiceName = "//sharpsql/v1/worker";

    // Shared with the durable runtime because both provision objects in the same schema.
    private const string ProvisioningLockName = "SharpSql.Runtime.Provisioning";
    internal const int ProvisioningLockErrorNumber = 51900;
    internal const int ExecutionNotFoundErrorNumber = 51901;
    internal const int BrokerDisabledErrorNumber = 51902;
    internal const int InvalidTerminalStateErrorNumber = 51903;
    internal const int AmbientTransactionErrorNumber = 51904;
    internal const int TaskNotFoundErrorNumber = 51905;
    internal const int InvalidTaskStateErrorNumber = 51906;
    internal const int InvalidTaskResultErrorNumber = 51907;
    internal const int InvalidDelayErrorNumber = 51908;
    internal const int InvalidTaskJoinErrorNumber = 51909;
    internal const int InvalidClaimBatchSizeErrorNumber = 51910;
    internal const int InvalidTaskRouteErrorNumber = 51911;

    internal static string Emit()
    {
        var sql = new SqlWriter();

        sql.Line("SET ANSI_NULLS ON;");
        sql.Line("SET ANSI_PADDING ON;");
        sql.Line("SET ANSI_WARNINGS ON;");
        sql.Line("SET ARITHABORT ON;");
        sql.Line("SET CONCAT_NULL_YIELDS_NULL ON;");
        sql.Line("SET QUOTED_IDENTIFIER ON;");
        sql.Line("SET NUMERIC_ROUNDABORT OFF;");
        sql.Line();
        sql.Line($"IF @@TRANCOUNT > 0 THROW {AmbientTransactionErrorNumber}, 'SharpSql Service Broker provisioning must run outside an existing transaction.', 1;");
        sql.Line();
        sql.Line("IF NOT EXISTS (");
        using (sql.Indent())
        {
            sql.Line("SELECT 1");
            sql.Line("FROM sys.databases");
            sql.Line("WHERE [database_id] = DB_ID() AND [is_broker_enabled] = 1");
        }
        sql.Line(")");
        using (sql.Indent())
        {
            sql.Line(
                $"THROW {BrokerDisabledErrorNumber}, 'Service Broker is not enabled in the current database.', 1;");
        }
        sql.Line();
        sql.Line("DECLARE @__sharpsql_provision_lock_result INT;");
        sql.Line();
        sql.Line("BEGIN TRY");
        using (sql.Indent())
        {
            sql.Line("BEGIN TRANSACTION;");
            sql.Line();
            sql.Line("EXEC @__sharpsql_provision_lock_result = sys.sp_getapplock");
            using (sql.Indent())
            {
                sql.Line($"@Resource = N'{ProvisioningLockName}',");
                sql.Line("@LockMode = N'Exclusive',");
                sql.Line("@LockOwner = N'Transaction',");
                sql.Line("@LockTimeout = 60000,");
                sql.Line("@DbPrincipal = N'public';");
            }
            sql.Line("IF @__sharpsql_provision_lock_result < 0");
            using (sql.Indent())
                sql.Line($"THROW {ProvisioningLockErrorNumber}, 'Could not acquire the SharpSql infrastructure provisioning lock.', 1;");
            sql.Line();

            EmitSchema(sql);
            sql.Line();
            EmitExecutionRegistry(sql);
            sql.Line();
            EmitTasks(sql);
            sql.Line();
            EmitTaskTimers(sql);
            sql.Line();
            EmitTaskJoins(sql);
            sql.Line();
            EmitTaskDependencies(sql);
            sql.Line();
            EmitOutputEvents(sql);
            sql.Line();
            EmitMessageTypes(sql);
            sql.Line();
            EmitContract(sql);
            sql.Line();
            EmitQueues(sql);
            sql.Line();
            EmitServices(sql);
            sql.Line();
            EmitAppendOutputProcedure(sql);
            sql.Line();
            EmitCompleteExecutionProcedure(sql);
            sql.Line();
            EmitEnqueueTaskProcedure(sql);
            sql.Line();
            EmitScheduleTaskProcedure(sql);
            sql.Line();
            EmitSuspendTaskForDelayProcedure(sql);
            sql.Line();
            EmitSuspendTaskForDependenciesProcedure(sql);
            sql.Line();
            EmitRegisterTaskDependencyProcedure(sql);
            sql.Line();
            EmitCompleteTaskProcedure(sql);
            sql.Line();
            EmitClaimDueContinuationsProcedure(sql);
            sql.Line();
            sql.Line("COMMIT TRANSACTION;");
        }
        sql.Line("END TRY");
        sql.Line("BEGIN CATCH");
        using (sql.Indent())
        {
            sql.Line("IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;");
            sql.Line("THROW;");
        }
        sql.Line("END CATCH;");

        return sql.ToString();
    }

    private static void EmitSchema(SqlWriter sql)
    {
        sql.Line($"IF SCHEMA_ID(N'{SchemaName}') IS NULL");
        using (sql.Indent())
            sql.Line($"EXEC(N'CREATE SCHEMA [{SchemaName}] AUTHORIZATION [dbo];');");
    }

    private static void EmitExecutionRegistry(SqlWriter sql)
    {
        sql.Line($"IF OBJECT_ID(N'[{SchemaName}].[{ExecutionsTableName}]', N'U') IS NULL");
        sql.Line("BEGIN");
        using (sql.Indent())
        {
            sql.Line($"CREATE TABLE [{SchemaName}].[{ExecutionsTableName}] (");
            using (sql.Indent())
            {
                sql.Line("[ExecutionId] UNIQUEIDENTIFIER NOT NULL,");
                sql.Line("[ConversationHandle] UNIQUEIDENTIFIER NULL,");
                sql.Line("[State] TINYINT NOT NULL CONSTRAINT [DF_sharpsql_Executions_State] DEFAULT (0),");
                sql.Line("[NextOutputSequence] BIGINT NOT NULL CONSTRAINT [DF_sharpsql_Executions_NextOutputSequence] DEFAULT (1),");
                sql.Line("[NextTaskId] BIGINT NOT NULL CONSTRAINT [DF_sharpsql_Executions_NextTaskId] DEFAULT (1),");
                sql.Line("[CreatedAtUtc] DATETIME2(7) NOT NULL CONSTRAINT [DF_sharpsql_Executions_CreatedAtUtc] DEFAULT (SYSUTCDATETIME()),");
                sql.Line("[StartedAtUtc] DATETIME2(7) NULL,");
                sql.Line("[CompletedAtUtc] DATETIME2(7) NULL,");
                sql.Line("[ErrorNumber] INT NULL,");
                sql.Line("[ErrorMessage] NVARCHAR(MAX) NULL,");
                sql.Line("CONSTRAINT [PK_sharpsql_Executions] PRIMARY KEY ([ExecutionId]),");
                sql.Line("CONSTRAINT [CK_sharpsql_Executions_State] CHECK ([State] BETWEEN 0 AND 4),");
                sql.Line("CONSTRAINT [CK_sharpsql_Executions_NextOutputSequence] CHECK ([NextOutputSequence] > 0),");
                sql.Line("CONSTRAINT [CK_sharpsql_Executions_NextTaskId] CHECK ([NextTaskId] > 0)");
            }
            sql.Line(");");
        }
        sql.Line("END;");
        sql.Line();
        sql.Line($"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'[{SchemaName}].[{ExecutionsTableName}]') AND [name] = N'IX_sharpsql_Executions_ConversationHandle')");
        using (sql.Indent())
        {
            sql.Line("CREATE INDEX [IX_sharpsql_Executions_ConversationHandle]");
            sql.Line($"ON [{SchemaName}].[{ExecutionsTableName}] ([ConversationHandle]);");
        }
        sql.Line();
        sql.Line($"IF COL_LENGTH(N'[{SchemaName}].[{ExecutionsTableName}]', N'NextTaskId') IS NULL");
        using (sql.Indent())
        {
            sql.Line($"ALTER TABLE [{SchemaName}].[{ExecutionsTableName}]");
            sql.Line("ADD [NextTaskId] BIGINT NOT NULL");
            using (sql.Indent())
                sql.Line("CONSTRAINT [DF_sharpsql_Executions_NextTaskId] DEFAULT (1) WITH VALUES;");
        }
    }

    private static void EmitTasks(SqlWriter sql)
    {
        sql.Line($"IF OBJECT_ID(N'[{SchemaName}].[{TasksTableName}]', N'U') IS NULL");
        sql.Line("BEGIN");
        using (sql.Indent())
        {
            sql.Line($"CREATE TABLE [{SchemaName}].[{TasksTableName}] (");
            using (sql.Indent())
            {
                sql.Line("[ExecutionId] UNIQUEIDENTIFIER NOT NULL,");
                sql.Line("[TaskId] BIGINT NOT NULL,");
                sql.Line("[ProgramId] NVARCHAR(128) NOT NULL,");
                sql.Line("[HandlerName] NVARCHAR(450) NOT NULL,");
                sql.Line("[ContinuationState] INT NOT NULL CONSTRAINT [DF_sharpsql_Tasks_ContinuationState] DEFAULT (0),");
                sql.Line("[SuspensionGeneration] INT NOT NULL CONSTRAINT [DF_sharpsql_Tasks_SuspensionGeneration] DEFAULT (0),");
                sql.Line("[State] TINYINT NOT NULL CONSTRAINT [DF_sharpsql_Tasks_State] DEFAULT (0),");
                sql.Line("[PayloadJson] NVARCHAR(MAX) NULL,");
                sql.Line("[ResultKind] TINYINT NOT NULL CONSTRAINT [DF_sharpsql_Tasks_ResultKind] DEFAULT (0),");
                sql.Line("[ResultScalar] SQL_VARIANT NULL,");
                sql.Line("[ResultText] NVARCHAR(MAX) NULL,");
                sql.Line("[ResultBinary] VARBINARY(MAX) NULL,");
                sql.Line("[ResultReferenceId] BIGINT NULL,");
                sql.Line("[ErrorNumber] INT NULL,");
                sql.Line("[ErrorMessage] NVARCHAR(MAX) NULL,");
                sql.Line("[DispatchConversationHandle] UNIQUEIDENTIFIER NULL,");
                sql.Line("[DispatchConversationId] UNIQUEIDENTIFIER NULL,");
                sql.Line("[CreatedAtUtc] DATETIME2(7) NOT NULL CONSTRAINT [DF_sharpsql_Tasks_CreatedAtUtc] DEFAULT (SYSUTCDATETIME()),");
                sql.Line("[ReadyAtUtc] DATETIME2(7) NULL,");
                sql.Line("[EnqueuedAtUtc] DATETIME2(7) NULL,");
                sql.Line("[StartedAtUtc] DATETIME2(7) NULL,");
                sql.Line("[CompletedAtUtc] DATETIME2(7) NULL,");
                sql.Line("CONSTRAINT [PK_sharpsql_Tasks] PRIMARY KEY CLUSTERED ([ExecutionId], [TaskId]),");
                sql.Line("CONSTRAINT [FK_sharpsql_Tasks_Executions] FOREIGN KEY ([ExecutionId])");
                using (sql.Indent())
                    sql.Line($"REFERENCES [{SchemaName}].[{ExecutionsTableName}] ([ExecutionId]) ON DELETE CASCADE,");
                sql.Line("CONSTRAINT [CK_sharpsql_Tasks_TaskId] CHECK ([TaskId] > 0),");
                sql.Line("CONSTRAINT [CK_sharpsql_Tasks_Route] CHECK (LEN([ProgramId]) > 0 AND LEN([HandlerName]) > 0),");
                sql.Line("CONSTRAINT [CK_sharpsql_Tasks_SuspensionGeneration] CHECK ([SuspensionGeneration] >= 0),");
                sql.Line("CONSTRAINT [CK_sharpsql_Tasks_State] CHECK ([State] BETWEEN 0 AND 6),");
                sql.Line("CONSTRAINT [CK_sharpsql_Tasks_PayloadJson] CHECK ([PayloadJson] IS NULL OR ISJSON([PayloadJson]) = 1),");
                sql.Line("CONSTRAINT [CK_sharpsql_Tasks_ResultKind] CHECK ([ResultKind] BETWEEN 0 AND 4),");
                sql.Line("CONSTRAINT [CK_sharpsql_Tasks_ResultColumns] CHECK (");
                using (sql.Indent())
                {
                    sql.Line("([ResultKind] = 0 AND [ResultScalar] IS NULL AND [ResultText] IS NULL AND [ResultBinary] IS NULL AND [ResultReferenceId] IS NULL) OR");
                    sql.Line("([ResultKind] = 1 AND [ResultText] IS NULL AND [ResultBinary] IS NULL AND [ResultReferenceId] IS NULL) OR");
                    sql.Line("([ResultKind] = 2 AND [ResultScalar] IS NULL AND [ResultBinary] IS NULL AND [ResultReferenceId] IS NULL) OR");
                    sql.Line("([ResultKind] = 3 AND [ResultScalar] IS NULL AND [ResultText] IS NULL AND [ResultReferenceId] IS NULL) OR");
                    sql.Line("([ResultKind] = 4 AND [ResultScalar] IS NULL AND [ResultText] IS NULL AND [ResultBinary] IS NULL)");
                }
                sql.Line("),");
                sql.Line("CONSTRAINT [CK_sharpsql_Tasks_TerminalValues] CHECK (");
                using (sql.Indent())
                {
                    sql.Line("([State] < 4 AND [CompletedAtUtc] IS NULL AND [ResultKind] = 0 AND [ErrorNumber] IS NULL AND [ErrorMessage] IS NULL) OR");
                    sql.Line("([State] = 4 AND [CompletedAtUtc] IS NOT NULL AND [ErrorNumber] IS NULL AND [ErrorMessage] IS NULL) OR");
                    sql.Line("([State] IN (5, 6) AND [CompletedAtUtc] IS NOT NULL AND [ResultKind] = 0 AND [ResultScalar] IS NULL AND [ResultText] IS NULL AND [ResultBinary] IS NULL AND [ResultReferenceId] IS NULL)");
                }
                sql.Line(")");
            }
            sql.Line(");");
        }
        sql.Line("END;");
        sql.Line();
        sql.Line($"IF COL_LENGTH(N'[{SchemaName}].[{TasksTableName}]', N'DispatchConversationId') IS NULL");
        using (sql.Indent())
            sql.Line($"ALTER TABLE [{SchemaName}].[{TasksTableName}] ADD [DispatchConversationId] UNIQUEIDENTIFIER NULL;");
        sql.Line();
        sql.Line($"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'[{SchemaName}].[{TasksTableName}]') AND [name] = N'IX_sharpsql_Tasks_Dispatch')");
        using (sql.Indent())
        {
            sql.Line("CREATE INDEX [IX_sharpsql_Tasks_Dispatch]");
            sql.Line($"ON [{SchemaName}].[{TasksTableName}] ([State], [ReadyAtUtc], [ExecutionId], [TaskId]);");
        }
        sql.Line();
        sql.Line($"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'[{SchemaName}].[{TasksTableName}]') AND [name] = N'IX_sharpsql_Tasks_DispatchConversationHandle')");
        using (sql.Indent())
        {
            sql.Line("CREATE INDEX [IX_sharpsql_Tasks_DispatchConversationHandle]");
            sql.Line($"ON [{SchemaName}].[{TasksTableName}] ([DispatchConversationHandle])");
            sql.Line("INCLUDE ([ExecutionId], [TaskId], [HandlerName], [State])");
            sql.Line("WHERE [DispatchConversationHandle] IS NOT NULL;");
        }
    }

    private static void EmitTaskTimers(SqlWriter sql)
    {
        sql.Line($"IF OBJECT_ID(N'[{SchemaName}].[{TaskTimersTableName}]', N'U') IS NULL");
        sql.Line("BEGIN");
        using (sql.Indent())
        {
            sql.Line($"CREATE TABLE [{SchemaName}].[{TaskTimersTableName}] (");
            using (sql.Indent())
            {
                sql.Line("[ExecutionId] UNIQUEIDENTIFIER NOT NULL,");
                sql.Line("[TaskId] BIGINT NOT NULL,");
                sql.Line("[SuspensionGeneration] INT NOT NULL,");
                sql.Line("[DelayMilliseconds] INT NOT NULL,");
                sql.Line("[DueAtUtc] DATETIME2(3) NOT NULL,");
                sql.Line("[State] TINYINT NOT NULL CONSTRAINT [DF_sharpsql_TaskTimers_State] DEFAULT (0),");
                sql.Line("[CreatedAtUtc] DATETIME2(7) NOT NULL CONSTRAINT [DF_sharpsql_TaskTimers_CreatedAtUtc] DEFAULT (SYSUTCDATETIME()),");
                sql.Line("[ClaimedAtUtc] DATETIME2(7) NULL,");
                sql.Line("[EnqueuedAtUtc] DATETIME2(7) NULL,");
                sql.Line("CONSTRAINT [PK_sharpsql_TaskTimers] PRIMARY KEY CLUSTERED ([ExecutionId], [TaskId], [SuspensionGeneration]),");
                sql.Line("CONSTRAINT [FK_sharpsql_TaskTimers_Tasks] FOREIGN KEY ([ExecutionId], [TaskId])");
                using (sql.Indent())
                    sql.Line($"REFERENCES [{SchemaName}].[{TasksTableName}] ([ExecutionId], [TaskId]) ON DELETE CASCADE,");
                sql.Line("CONSTRAINT [CK_sharpsql_TaskTimers_Generation] CHECK ([SuspensionGeneration] > 0),");
                sql.Line("CONSTRAINT [CK_sharpsql_TaskTimers_Delay] CHECK ([DelayMilliseconds] > 0),");
                sql.Line("CONSTRAINT [CK_sharpsql_TaskTimers_State] CHECK ([State] BETWEEN 0 AND 3)");
            }
            sql.Line(");");
        }
        sql.Line("END;");
        sql.Line();
        sql.Line($"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'[{SchemaName}].[{TaskTimersTableName}]') AND [name] = N'IX_sharpsql_TaskTimers_Due')");
        using (sql.Indent())
        {
            sql.Line("CREATE INDEX [IX_sharpsql_TaskTimers_Due]");
            sql.Line($"ON [{SchemaName}].[{TaskTimersTableName}] ([State], [DueAtUtc], [ExecutionId], [TaskId], [SuspensionGeneration]);");
        }
    }

    private static void EmitTaskJoins(SqlWriter sql)
    {
        sql.Line($"IF OBJECT_ID(N'[{SchemaName}].[{TaskJoinsTableName}]', N'U') IS NULL");
        sql.Line("BEGIN");
        using (sql.Indent())
        {
            sql.Line($"CREATE TABLE [{SchemaName}].[{TaskJoinsTableName}] (");
            using (sql.Indent())
            {
                sql.Line("[ExecutionId] UNIQUEIDENTIFIER NOT NULL,");
                sql.Line("[ContinuationTaskId] BIGINT NOT NULL,");
                sql.Line("[SuspensionGeneration] INT NOT NULL,");
                sql.Line("[ExpectedDependencyCount] INT NOT NULL,");
                sql.Line("[RegisteredDependencyCount] INT NOT NULL CONSTRAINT [DF_sharpsql_TaskJoins_RegisteredCount] DEFAULT (0),");
                sql.Line("[CompletedDependencyCount] INT NOT NULL CONSTRAINT [DF_sharpsql_TaskJoins_CompletedCount] DEFAULT (0),");
                sql.Line("[CreatedAtUtc] DATETIME2(7) NOT NULL CONSTRAINT [DF_sharpsql_TaskJoins_CreatedAtUtc] DEFAULT (SYSUTCDATETIME()),");
                sql.Line("[ReadyAtUtc] DATETIME2(7) NULL,");
                sql.Line("[EnqueuedAtUtc] DATETIME2(7) NULL,");
                sql.Line("CONSTRAINT [PK_sharpsql_TaskJoins] PRIMARY KEY CLUSTERED ([ExecutionId], [ContinuationTaskId], [SuspensionGeneration]),");
                sql.Line("CONSTRAINT [FK_sharpsql_TaskJoins_Tasks] FOREIGN KEY ([ExecutionId], [ContinuationTaskId])");
                using (sql.Indent())
                    sql.Line($"REFERENCES [{SchemaName}].[{TasksTableName}] ([ExecutionId], [TaskId]) ON DELETE CASCADE,");
                sql.Line("CONSTRAINT [CK_sharpsql_TaskJoins_Generation] CHECK ([SuspensionGeneration] > 0),");
                sql.Line("CONSTRAINT [CK_sharpsql_TaskJoins_Counts] CHECK (");
                using (sql.Indent())
                    sql.Line("[ExpectedDependencyCount] >= 0 AND [RegisteredDependencyCount] BETWEEN 0 AND [ExpectedDependencyCount] AND [CompletedDependencyCount] BETWEEN 0 AND [RegisteredDependencyCount]");
                sql.Line(")");
            }
            sql.Line(");");
        }
        sql.Line("END;");
    }

    private static void EmitTaskDependencies(SqlWriter sql)
    {
        sql.Line($"IF OBJECT_ID(N'[{SchemaName}].[{TaskDependenciesTableName}]', N'U') IS NULL");
        sql.Line("BEGIN");
        using (sql.Indent())
        {
            sql.Line($"CREATE TABLE [{SchemaName}].[{TaskDependenciesTableName}] (");
            using (sql.Indent())
            {
                sql.Line("[ExecutionId] UNIQUEIDENTIFIER NOT NULL,");
                sql.Line("[ContinuationTaskId] BIGINT NOT NULL,");
                sql.Line("[SuspensionGeneration] INT NOT NULL,");
                sql.Line("[DependencyTaskId] BIGINT NOT NULL,");
                sql.Line("[DependencyState] TINYINT NULL,");
                sql.Line("[CreatedAtUtc] DATETIME2(7) NOT NULL CONSTRAINT [DF_sharpsql_TaskDependencies_CreatedAtUtc] DEFAULT (SYSUTCDATETIME()),");
                sql.Line("[CompletedAtUtc] DATETIME2(7) NULL,");
                sql.Line("CONSTRAINT [PK_sharpsql_TaskDependencies] PRIMARY KEY CLUSTERED ([ExecutionId], [ContinuationTaskId], [SuspensionGeneration], [DependencyTaskId]),");
                sql.Line("CONSTRAINT [FK_sharpsql_TaskDependencies_Joins] FOREIGN KEY ([ExecutionId], [ContinuationTaskId], [SuspensionGeneration])");
                using (sql.Indent())
                    sql.Line($"REFERENCES [{SchemaName}].[{TaskJoinsTableName}] ([ExecutionId], [ContinuationTaskId], [SuspensionGeneration]) ON DELETE CASCADE,");
                sql.Line("CONSTRAINT [FK_sharpsql_TaskDependencies_Tasks] FOREIGN KEY ([ExecutionId], [DependencyTaskId])");
                using (sql.Indent())
                    sql.Line($"REFERENCES [{SchemaName}].[{TasksTableName}] ([ExecutionId], [TaskId]),");
                sql.Line("CONSTRAINT [CK_sharpsql_TaskDependencies_Distinct] CHECK ([ContinuationTaskId] <> [DependencyTaskId]),");
                sql.Line("CONSTRAINT [CK_sharpsql_TaskDependencies_State] CHECK ([DependencyState] IS NULL OR [DependencyState] BETWEEN 4 AND 6)");
            }
            sql.Line(");");
        }
        sql.Line("END;");
        sql.Line();
        sql.Line($"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'[{SchemaName}].[{TaskDependenciesTableName}]') AND [name] = N'IX_sharpsql_TaskDependencies_Dependency')");
        using (sql.Indent())
        {
            sql.Line("CREATE INDEX [IX_sharpsql_TaskDependencies_Dependency]");
            sql.Line($"ON [{SchemaName}].[{TaskDependenciesTableName}] ([ExecutionId], [DependencyTaskId], [CompletedAtUtc])");
            using (sql.Indent())
                sql.Line("INCLUDE ([ContinuationTaskId], [SuspensionGeneration]);");
        }
    }

    private static void EmitOutputEvents(SqlWriter sql)
    {
        sql.Line($"IF OBJECT_ID(N'[{SchemaName}].[{OutputEventsTableName}]', N'U') IS NULL");
        sql.Line("BEGIN");
        using (sql.Indent())
        {
            sql.Line($"CREATE TABLE [{SchemaName}].[{OutputEventsTableName}] (");
            using (sql.Indent())
            {
                sql.Line("[ExecutionId] UNIQUEIDENTIFIER NOT NULL,");
                sql.Line("[SequenceNumber] BIGINT NOT NULL,");
                sql.Line("[OutputText] NVARCHAR(MAX) NOT NULL,");
                sql.Line("[CreatedAtUtc] DATETIME2(7) NOT NULL CONSTRAINT [DF_sharpsql_OutputEvents_CreatedAtUtc] DEFAULT (SYSUTCDATETIME()),");
                sql.Line("CONSTRAINT [PK_sharpsql_OutputEvents] PRIMARY KEY CLUSTERED ([ExecutionId], [SequenceNumber]),");
                sql.Line("CONSTRAINT [FK_sharpsql_OutputEvents_Executions] FOREIGN KEY ([ExecutionId])");
                using (sql.Indent())
                    sql.Line($"REFERENCES [{SchemaName}].[{ExecutionsTableName}] ([ExecutionId]) ON DELETE CASCADE,");
                sql.Line("CONSTRAINT [CK_sharpsql_OutputEvents_SequenceNumber] CHECK ([SequenceNumber] > 0)");
            }
            sql.Line(");");
        }
        sql.Line("END;");
    }

    private static void EmitMessageTypes(SqlWriter sql)
    {
        EmitMessageType(sql, RequestMessageTypeName);
        EmitMessageType(sql, OutputMessageTypeName);
        EmitMessageType(sql, CompletedMessageTypeName);
    }

    private static void EmitMessageType(SqlWriter sql, string name)
    {
        sql.Line($"IF NOT EXISTS (SELECT 1 FROM sys.service_message_types WHERE [name] = N'{name}')");
        using (sql.Indent())
            sql.Line($"CREATE MESSAGE TYPE [{name}] VALIDATION = NONE;");
    }

    private static void EmitContract(SqlWriter sql)
    {
        sql.Line($"IF NOT EXISTS (SELECT 1 FROM sys.service_contracts WHERE [name] = N'{ContractName}')");
        sql.Line("BEGIN");
        using (sql.Indent())
        {
            sql.Line($"CREATE CONTRACT [{ContractName}] (");
            using (sql.Indent())
            {
                sql.Line($"[{RequestMessageTypeName}] SENT BY INITIATOR,");
                sql.Line($"[{OutputMessageTypeName}] SENT BY TARGET,");
                sql.Line($"[{CompletedMessageTypeName}] SENT BY TARGET");
            }
            sql.Line(");");
        }
        sql.Line("END;");
    }

    private static void EmitQueues(SqlWriter sql)
    {
        EmitQueue(sql, LauncherQueueName);
        EmitQueue(sql, WorkerQueueName);
    }

    private static void EmitQueue(SqlWriter sql, string name)
    {
        sql.Line($"IF OBJECT_ID(N'[{SchemaName}].[{name}]', N'SQ') IS NULL");
        using (sql.Indent())
            sql.Line($"CREATE QUEUE [{SchemaName}].[{name}] WITH STATUS = ON, RETENTION = OFF;");
    }

    private static void EmitServices(SqlWriter sql)
    {
        sql.Line($"IF NOT EXISTS (SELECT 1 FROM sys.services WHERE [name] = N'{LauncherServiceName}')");
        using (sql.Indent())
            sql.Line($"CREATE SERVICE [{LauncherServiceName}] ON QUEUE [{SchemaName}].[{LauncherQueueName}];");

        sql.Line($"IF NOT EXISTS (SELECT 1 FROM sys.services WHERE [name] = N'{WorkerServiceName}')");
        using (sql.Indent())
        {
            sql.Line($"CREATE SERVICE [{WorkerServiceName}]");
            sql.Line($"ON QUEUE [{SchemaName}].[{WorkerQueueName}]");
            sql.Line($"([{ContractName}]);");
        }
    }

    private static void EmitAppendOutputProcedure(SqlWriter sql)
    {
        var procedure = new SqlWriter();
        procedure.Line($"CREATE OR ALTER PROCEDURE [{SchemaName}].[{AppendOutputProcedureName}]");
        using (procedure.Indent())
        {
            procedure.Line("@ExecutionId UNIQUEIDENTIFIER,");
            procedure.Line("@OutputText NVARCHAR(MAX)");
        }
        procedure.Line("AS");
        procedure.Line("BEGIN");
        using (procedure.Indent())
        {
            procedure.Line("SET NOCOUNT ON;");
            EmitProcedureTransactionDeclaration(procedure);
            procedure.Line("SET @OutputText = COALESCE(@OutputText, N'');");
            procedure.Line();
            procedure.Line("DECLARE @Allocation TABLE (");
            using (procedure.Indent())
            {
                procedure.Line("[SequenceNumber] BIGINT NOT NULL,");
                procedure.Line("[ConversationHandle] UNIQUEIDENTIFIER NULL");
            }
            procedure.Line(");");
            procedure.Line("DECLARE @SequenceNumber BIGINT;");
            procedure.Line("DECLARE @ConversationHandle UNIQUEIDENTIFIER;");
            procedure.Line("DECLARE @Notification NVARCHAR(MAX);");
            procedure.Line("DECLARE @MessageBody VARBINARY(MAX);");
            procedure.Line();
            procedure.Line("BEGIN TRY");
            using (procedure.Indent())
            {
                EmitProcedureTransactionStart(procedure, "SharpSqlAppendOutput");
                procedure.Line();
                procedure.Line($"UPDATE [{SchemaName}].[{ExecutionsTableName}] WITH (UPDLOCK, ROWLOCK)");
                procedure.Line("SET [NextOutputSequence] = [NextOutputSequence] + 1");
                procedure.Line("OUTPUT DELETED.[NextOutputSequence], INSERTED.[ConversationHandle]");
                procedure.Line("INTO @Allocation ([SequenceNumber], [ConversationHandle])");
                procedure.Line("WHERE [ExecutionId] = @ExecutionId;");
                procedure.Line();
                procedure.Line("IF NOT EXISTS (SELECT 1 FROM @Allocation)");
                using (procedure.Indent())
                {
                    procedure.Line(
                        $"THROW {ExecutionNotFoundErrorNumber}, 'The SharpSql execution does not exist.', 1;");
                }
                procedure.Line();
                procedure.Line("SELECT");
                using (procedure.Indent())
                {
                    procedure.Line("@SequenceNumber = [SequenceNumber],");
                    procedure.Line("@ConversationHandle = [ConversationHandle]");
                }
                procedure.Line("FROM @Allocation;");
                procedure.Line();
                procedure.Line($"INSERT INTO [{SchemaName}].[{OutputEventsTableName}] (");
                using (procedure.Indent())
                    procedure.Line("[ExecutionId], [SequenceNumber], [OutputText]");
                procedure.Line(") VALUES (");
                using (procedure.Indent())
                    procedure.Line("@ExecutionId, @SequenceNumber, @OutputText");
                procedure.Line(");");
                procedure.Line();
                procedure.Line("IF @ConversationHandle IS NOT NULL");
                procedure.Line("BEGIN");
                using (procedure.Indent())
                {
                    procedure.Line("SELECT @Notification = (");
                    using (procedure.Indent())
                    {
                        procedure.Line("SELECT");
                        using (procedure.Indent())
                        {
                            procedure.Line("CONVERT(NVARCHAR(36), @ExecutionId) AS [executionId],");
                            procedure.Line("@SequenceNumber AS [sequenceNumber],");
                            procedure.Line("@OutputText AS [output]");
                        }
                        procedure.Line("FOR JSON PATH, WITHOUT_ARRAY_WRAPPER");
                    }
                    procedure.Line(");");
                    procedure.Line("SET @MessageBody = CONVERT(VARBINARY(MAX), @Notification);");
                    procedure.Line("SEND ON CONVERSATION @ConversationHandle");
                    using (procedure.Indent())
                        procedure.Line($"MESSAGE TYPE [{OutputMessageTypeName}] (@MessageBody);");
                }
                procedure.Line("END;");
                procedure.Line();
                EmitProcedureTransactionCommit(procedure);
                procedure.Line("SELECT @SequenceNumber AS [SequenceNumber];");
            }
            procedure.Line("END TRY");
            procedure.Line("BEGIN CATCH");
            using (procedure.Indent())
            {
                EmitProcedureTransactionRollback(procedure, "SharpSqlAppendOutput");
                procedure.Line("THROW;");
            }
            procedure.Line("END CATCH;");
        }
        procedure.Line("END;");

        var batch = procedure.ToString().TrimEnd().Replace("'", "''");
        sql.Line($"EXEC(N'{batch}');");
    }

    private static void EmitCompleteExecutionProcedure(SqlWriter sql)
    {
        var procedure = new SqlWriter();
        procedure.Line($"CREATE OR ALTER PROCEDURE [{SchemaName}].[{CompleteExecutionProcedureName}]");
        using (procedure.Indent())
        {
            procedure.Line("@ExecutionId UNIQUEIDENTIFIER,");
            procedure.Line("@State TINYINT,");
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
            procedure.Line("IF @State IS NULL OR @State NOT IN (2, 3, 4)");
            using (procedure.Indent())
            {
                procedure.Line(
                    $"THROW {InvalidTerminalStateErrorNumber}, 'A terminal SharpSql state must be succeeded (2), failed (3), or canceled (4).', 1;");
            }
            procedure.Line();
            procedure.Line("DECLARE @Completion TABLE (");
            using (procedure.Indent())
            {
                procedure.Line("[ConversationHandle] UNIQUEIDENTIFIER NULL,");
                procedure.Line("[State] TINYINT NOT NULL,");
                procedure.Line("[CompletedAtUtc] DATETIME2(7) NOT NULL,");
                procedure.Line("[ErrorNumber] INT NULL,");
                procedure.Line("[ErrorMessage] NVARCHAR(MAX) NULL");
            }
            procedure.Line(");");
            procedure.Line("DECLARE @ConversationHandle UNIQUEIDENTIFIER;");
            procedure.Line("DECLARE @CompletedAtUtc DATETIME2(7);");
            procedure.Line("DECLARE @StoredErrorNumber INT;");
            procedure.Line("DECLARE @StoredErrorMessage NVARCHAR(MAX);");
            procedure.Line("DECLARE @Notification NVARCHAR(MAX);");
            procedure.Line("DECLARE @MessageBody VARBINARY(MAX);");
            procedure.Line();
            procedure.Line("BEGIN TRY");
            using (procedure.Indent())
            {
                EmitProcedureTransactionStart(procedure, "SharpSqlCompleteExecution");
                procedure.Line();
                procedure.Line($"UPDATE [{SchemaName}].[{ExecutionsTableName}] WITH (UPDLOCK, ROWLOCK)");
                procedure.Line("SET");
                using (procedure.Indent())
                {
                    procedure.Line("[State] = @State,");
                    procedure.Line("[CompletedAtUtc] = SYSUTCDATETIME(),");
                    procedure.Line("[ErrorNumber] = CASE WHEN @State = 2 THEN NULL ELSE @ErrorNumber END,");
                    procedure.Line("[ErrorMessage] = CASE WHEN @State = 2 THEN NULL ELSE @ErrorMessage END");
                }
                procedure.Line("OUTPUT");
                using (procedure.Indent())
                {
                    procedure.Line("INSERTED.[ConversationHandle],");
                    procedure.Line("INSERTED.[State],");
                    procedure.Line("INSERTED.[CompletedAtUtc],");
                    procedure.Line("INSERTED.[ErrorNumber],");
                    procedure.Line("INSERTED.[ErrorMessage]");
                }
                procedure.Line("INTO @Completion (");
                using (procedure.Indent())
                {
                    procedure.Line("[ConversationHandle], [State], [CompletedAtUtc], [ErrorNumber], [ErrorMessage]");
                }
                procedure.Line(")");
                procedure.Line("WHERE [ExecutionId] = @ExecutionId AND [State] NOT IN (2, 3, 4);");
                procedure.Line();
                procedure.Line("IF NOT EXISTS (SELECT 1 FROM @Completion)");
                procedure.Line("BEGIN");
                using (procedure.Indent())
                {
                    procedure.Line($"IF NOT EXISTS (SELECT 1 FROM [{SchemaName}].[{ExecutionsTableName}] WITH (UPDLOCK, HOLDLOCK) WHERE [ExecutionId] = @ExecutionId)");
                    using (procedure.Indent())
                    {
                        procedure.Line(
                            $"THROW {ExecutionNotFoundErrorNumber}, 'The SharpSql execution does not exist.', 1;");
                    }
                    procedure.Line();
                    EmitProcedureTransactionCommit(procedure);
                    procedure.Line("SELECT CAST(0 AS BIT) AS [Completed];");
                    procedure.Line("RETURN;");
                }
                procedure.Line("END;");
                procedure.Line();
                procedure.Line("SELECT");
                using (procedure.Indent())
                {
                    procedure.Line("@ConversationHandle = [ConversationHandle],");
                    procedure.Line("@CompletedAtUtc = [CompletedAtUtc],");
                    procedure.Line("@StoredErrorNumber = [ErrorNumber],");
                    procedure.Line("@StoredErrorMessage = [ErrorMessage]");
                }
                procedure.Line("FROM @Completion;");
                procedure.Line();
                procedure.Line("IF @ConversationHandle IS NOT NULL");
                procedure.Line("BEGIN");
                using (procedure.Indent())
                {
                    procedure.Line("SELECT @Notification = (");
                    using (procedure.Indent())
                    {
                        procedure.Line("SELECT");
                        using (procedure.Indent())
                        {
                            procedure.Line("CONVERT(NVARCHAR(36), @ExecutionId) AS [executionId],");
                            procedure.Line("@State AS [state],");
                            procedure.Line("@CompletedAtUtc AS [completedAtUtc],");
                            procedure.Line("@StoredErrorNumber AS [errorNumber],");
                            procedure.Line("@StoredErrorMessage AS [errorMessage]");
                        }
                        procedure.Line("FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES");
                    }
                    procedure.Line(");");
                    procedure.Line("SET @MessageBody = CONVERT(VARBINARY(MAX), @Notification);");
                    procedure.Line("SEND ON CONVERSATION @ConversationHandle");
                    using (procedure.Indent())
                        procedure.Line($"MESSAGE TYPE [{CompletedMessageTypeName}] (@MessageBody);");
                    procedure.Line("END CONVERSATION @ConversationHandle;");
                }
                procedure.Line("END;");
                procedure.Line();
                EmitProcedureTransactionCommit(procedure);
                procedure.Line("SELECT CAST(1 AS BIT) AS [Completed];");
            }
            procedure.Line("END TRY");
            procedure.Line("BEGIN CATCH");
            using (procedure.Indent())
            {
                EmitProcedureTransactionRollback(procedure, "SharpSqlCompleteExecution");
                procedure.Line("THROW;");
            }
            procedure.Line("END CATCH;");
        }
        procedure.Line("END;");

        var batch = procedure.ToString().TrimEnd().Replace("'", "''");
        sql.Line($"EXEC(N'{batch}');");
    }

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
                    procedure.Line("AND NOT EXISTS (");
                    using (procedure.Indent())
                    {
                        procedure.Line("SELECT 1");
                        procedure.Line($"FROM [{SchemaName}].[{TaskTimersTableName}] AS [earlier_timer]");
                        procedure.Line($"INNER JOIN [{SchemaName}].[{TasksTableName}] AS [earlier_task]");
                        using (procedure.Indent())
                        {
                            procedure.Line("ON [earlier_task].[ExecutionId] = [earlier_timer].[ExecutionId]");
                            procedure.Line("AND [earlier_task].[TaskId] = [earlier_timer].[TaskId]");
                            procedure.Line("AND [earlier_task].[SuspensionGeneration] = [earlier_timer].[SuspensionGeneration]");
                        }
                        procedure.Line("WHERE [earlier_timer].[ExecutionId] = [timer].[ExecutionId]");
                        procedure.Line("AND [earlier_timer].[State] IN (0, 1, 2)");
                        procedure.Line("AND [earlier_task].[State] NOT BETWEEN 4 AND 6");
                        procedure.Line("AND (");
                        using (procedure.Indent())
                        {
                            procedure.Line("[earlier_timer].[DueAtUtc] < [timer].[DueAtUtc]");
                            procedure.Line("OR ([earlier_timer].[DueAtUtc] = [timer].[DueAtUtc] AND [earlier_timer].[TaskId] < [timer].[TaskId])");
                        }
                        procedure.Line(")");
                    }
                    procedure.Line(")");
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
