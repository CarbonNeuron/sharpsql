namespace SharpSql;

/// <summary>
/// Emits the permanent, database-scoped objects used to coordinate SharpSql executions.
/// This is intentionally separate from program lowering so provisioning can happen once
/// without making an individual transpiled batch own shared infrastructure.
/// </summary>
internal static partial class ExecutionInfrastructureSqlEmitter
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

}
