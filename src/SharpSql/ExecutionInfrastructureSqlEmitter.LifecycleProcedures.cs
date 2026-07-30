namespace SharpSql;

internal static partial class ExecutionInfrastructureSqlEmitter
{
    internal const string ProgramCatalogTableName = "ServiceBrokerPrograms";
    internal const string RegisterProgramProcedureName = "RegisterServiceBrokerProgram";
    internal const string StartExecutionProcedureName = "StartServiceBrokerExecution";
    internal const string HeartbeatExecutionProcedureName = "HeartbeatExecution";
    internal const string CancelExecutionProcedureName = "CancelExecution";
    internal const string ReapAbandonedExecutionsProcedureName = "ReapAbandonedExecutions";
    internal const string CleanupProgramsProcedureName = "CleanupServiceBrokerPrograms";
    internal const string ServiceBrokerStatusProcedureName = "GetServiceBrokerStatus";

    internal const int InvalidLeaseErrorNumber = 51912;
    internal const int ExecutionCanceledErrorNumber = 51913;
    internal const int ExecutionAbandonedErrorNumber = 51914;
    internal const int InvalidRetentionErrorNumber = 51915;

    /// <summary>
    /// Emits additive lifecycle objects separately from the base infrastructure so older
    /// databases can be upgraded without making the base table emitter own migrations.
    /// </summary>
    internal static string EmitLifecycle()
    {
        var sql = new SqlWriter();

        sql.Line("DECLARE @__sharpsql_lifecycle_lock_result INT;");
        sql.Line("BEGIN TRY");
        using (sql.Indent())
        {
            sql.Line("BEGIN TRANSACTION;");
            sql.Line("EXEC @__sharpsql_lifecycle_lock_result = sys.sp_getapplock");
            using (sql.Indent())
            {
                sql.Line("@Resource = N'SharpSql.Runtime.Provisioning',");
                sql.Line("@LockMode = N'Exclusive',");
                sql.Line("@LockOwner = N'Transaction',");
                sql.Line("@LockTimeout = 60000,");
                sql.Line("@DbPrincipal = N'public';");
            }
            sql.Line("IF @__sharpsql_lifecycle_lock_result < 0");
            using (sql.Indent())
                sql.Line($"THROW {ProvisioningLockErrorNumber}, 'Could not acquire the SharpSql lifecycle provisioning lock.', 1;");
            sql.Line();
            EmitLifecycleColumns(sql);
            sql.Line();
            EmitProgramCatalog(sql);
            sql.Line();
            EmitRegisterProgramProcedure(sql);
            sql.Line();
            EmitStartExecutionProcedure(sql);
            sql.Line();
            EmitHeartbeatExecutionProcedure(sql);
            sql.Line();
            EmitCancelExecutionProcedure(sql);
            sql.Line();
            EmitReapAbandonedExecutionsProcedure(sql);
            sql.Line();
            EmitCleanupProgramsProcedure(sql);
            sql.Line();
            EmitServiceBrokerStatusProcedure(sql);
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

    private static void EmitLifecycleColumns(SqlWriter sql)
    {
        EmitNullableExecutionColumn(sql, "ProgramId", "NVARCHAR(32)");
        EmitNullableExecutionColumn(sql, "LeaseId", "UNIQUEIDENTIFIER");
        EmitNullableExecutionColumn(sql, "LastHeartbeatAtUtc", "DATETIME2(7)");
        EmitNullableExecutionColumn(sql, "LeaseExpiresAtUtc", "DATETIME2(7)");
        EmitNullableExecutionColumn(sql, "CancelRequestedAtUtc", "DATETIME2(7)");
        sql.Line();
        sql.Line($"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'[{SchemaName}].[{ExecutionsTableName}]') AND [name] = N'IX_sharpsql_Executions_LeaseExpiry')");
        using (sql.Indent())
        {
            sql.Line("CREATE INDEX [IX_sharpsql_Executions_LeaseExpiry]");
            sql.Line($"ON [{SchemaName}].[{ExecutionsTableName}] ([State], [LeaseExpiresAtUtc], [ExecutionId])");
            sql.Line("INCLUDE ([LeaseId], [ProgramId]);");
        }
    }

    private static void EmitNullableExecutionColumn(SqlWriter sql, string name, string type)
    {
        sql.Line($"IF COL_LENGTH(N'[{SchemaName}].[{ExecutionsTableName}]', N'{name}') IS NULL");
        using (sql.Indent())
            sql.Line($"ALTER TABLE [{SchemaName}].[{ExecutionsTableName}] ADD [{name}] {type} NULL;");
    }

    private static void EmitProgramCatalog(SqlWriter sql)
    {
        sql.Line($"IF OBJECT_ID(N'[{SchemaName}].[{ProgramCatalogTableName}]', N'U') IS NULL");
        sql.Line("BEGIN");
        using (sql.Indent())
        {
            sql.Line($"CREATE TABLE [{SchemaName}].[{ProgramCatalogTableName}] (");
            using (sql.Indent())
            {
                sql.Line("[ProgramId] NVARCHAR(32) NOT NULL,");
                sql.Line("[ProcedureName] SYSNAME NOT NULL,");
                sql.Line("[InstalledAtUtc] DATETIME2(7) NOT NULL,");
                sql.Line("[LastUsedAtUtc] DATETIME2(7) NOT NULL,");
                sql.Line("CONSTRAINT [PK_sharpsql_ServiceBrokerPrograms] PRIMARY KEY ([ProgramId]),");
                sql.Line("CONSTRAINT [CK_sharpsql_ServiceBrokerPrograms_Id] CHECK (LEN([ProgramId]) = 32 AND [ProgramId] COLLATE Latin1_General_100_BIN2 NOT LIKE N'%[^0-9a-f]%'),");
                sql.Line("CONSTRAINT [CK_sharpsql_ServiceBrokerPrograms_Name] CHECK ([ProcedureName] = N'Program_' + [ProgramId])");
            }
            sql.Line(");");
        }
        sql.Line("END;");
        sql.Line();
        sql.Line($"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'[{SchemaName}].[{ProgramCatalogTableName}]') AND [name] = N'IX_sharpsql_ServiceBrokerPrograms_LastUsed')");
        using (sql.Indent())
            sql.Line($"CREATE INDEX [IX_sharpsql_ServiceBrokerPrograms_LastUsed] ON [{SchemaName}].[{ProgramCatalogTableName}] ([LastUsedAtUtc], [ProgramId]);");
    }

    private static void EmitRegisterProgramProcedure(SqlWriter sql)
    {
        var procedure = BeginProcedure(RegisterProgramProcedureName, "@ProgramId NVARCHAR(32)");
        using (procedure.Indent())
        {
            procedure.Line("SET NOCOUNT ON;");
            EmitProcedureTransactionDeclaration(procedure);
            procedure.Line("IF @ProgramId IS NULL OR LEN(@ProgramId) <> 32 OR @ProgramId COLLATE Latin1_General_100_BIN2 LIKE N'%[^0-9a-f]%'");
            using (procedure.Indent())
                procedure.Line($"THROW {InvalidTaskRouteErrorNumber}, 'A SharpSql program ID must be a lowercase 32-character hexadecimal hash.', 1;");
            procedure.Line("DECLARE @ProcedureName SYSNAME = N'Program_' + @ProgramId;");
            procedure.Line("DECLARE @QualifiedName NVARCHAR(776) = N'[SharpSql].' + QUOTENAME(@ProcedureName);");
            procedure.Line("DECLARE @LockResult INT;");
            procedure.Line("DECLARE @LockResource NVARCHAR(255);");
            procedure.Line("BEGIN TRY");
            using (procedure.Indent())
            {
                EmitProcedureTransactionStart(procedure, "SharpSqlRegisterProgram");
                EmitProgramLock(procedure);
                procedure.Line("IF OBJECT_ID(@QualifiedName, N'P') IS NULL");
                using (procedure.Indent())
                    procedure.Line($"THROW {ServiceBrokerWorkerDispatcherSqlEmitter.ProgramNotInstalledErrorNumber}, 'The compiled SharpSql worker procedure is not installed.', 1;");
                procedure.Line($"UPDATE [{SchemaName}].[{ProgramCatalogTableName}] WITH (UPDLOCK, HOLDLOCK)");
                procedure.Line("SET [ProcedureName] = @ProcedureName, [LastUsedAtUtc] = SYSUTCDATETIME()");
                procedure.Line("WHERE [ProgramId] = @ProgramId;");
                procedure.Line("IF @@ROWCOUNT = 0");
                using (procedure.Indent())
                {
                    procedure.Line($"INSERT INTO [{SchemaName}].[{ProgramCatalogTableName}] ([ProgramId], [ProcedureName], [InstalledAtUtc], [LastUsedAtUtc])");
                    procedure.Line("VALUES (@ProgramId, @ProcedureName, SYSUTCDATETIME(), SYSUTCDATETIME());");
                }
                EmitProcedureTransactionCommit(procedure);
            }
            procedure.Line("END TRY");
            procedure.Line("BEGIN CATCH");
            using (procedure.Indent())
            {
                EmitProcedureTransactionRollback(procedure, "SharpSqlRegisterProgram");
                procedure.Line("THROW;");
            }
            procedure.Line("END CATCH;");
        }
        EndProcedure(sql, procedure);
    }

    private static void EmitStartExecutionProcedure(SqlWriter sql)
    {
        var procedure = BeginProcedure(
            StartExecutionProcedureName,
            "@ExecutionId UNIQUEIDENTIFIER,",
            "@ProgramId NVARCHAR(32),",
            "@LeaseDurationSeconds INT = 30,",
            "@LeaseId UNIQUEIDENTIFIER = NULL OUTPUT");
        using (procedure.Indent())
        {
            procedure.Line("SET NOCOUNT ON;");
            EmitProcedureTransactionDeclaration(procedure);
            procedure.Line("IF @ExecutionId IS NULL OR @ProgramId IS NULL");
            using (procedure.Indent())
                procedure.Line($"THROW {InvalidLeaseErrorNumber}, 'An execution ID and program ID are required.', 1;");
            procedure.Line("IF @LeaseDurationSeconds NOT BETWEEN 5 AND 3600");
            using (procedure.Indent())
                procedure.Line($"THROW {InvalidLeaseErrorNumber}, 'A SharpSql execution lease must be between 5 and 3600 seconds.', 1;");
            procedure.Line("SET @LeaseId = NEWID();");
            procedure.Line("DECLARE @NowUtc DATETIME2(7) = SYSUTCDATETIME();");
            procedure.Line("DECLARE @QualifiedName NVARCHAR(776) = N'[SharpSql].[Program_' + @ProgramId + N']';");
            procedure.Line("DECLARE @LockResult INT;");
            procedure.Line("DECLARE @LockResource NVARCHAR(255);");
            procedure.Line("BEGIN TRY");
            using (procedure.Indent())
            {
                EmitProcedureTransactionStart(procedure, "SharpSqlStartExecution");
                EmitProgramLock(procedure);
                procedure.Line($"IF NOT EXISTS (SELECT 1 FROM [{SchemaName}].[{ProgramCatalogTableName}] WITH (UPDLOCK, HOLDLOCK) WHERE [ProgramId] = @ProgramId)");
                using (procedure.Indent())
                    procedure.Line($"THROW {ServiceBrokerWorkerDispatcherSqlEmitter.ProgramNotInstalledErrorNumber}, 'The compiled SharpSql worker program is not registered.', 1;");
                procedure.Line("IF OBJECT_ID(@QualifiedName, N'P') IS NULL");
                using (procedure.Indent())
                    procedure.Line($"THROW {ServiceBrokerWorkerDispatcherSqlEmitter.ProgramNotInstalledErrorNumber}, 'The compiled SharpSql worker procedure is not installed.', 1;");
                procedure.Line($"UPDATE [{SchemaName}].[{ProgramCatalogTableName}] SET [LastUsedAtUtc] = @NowUtc WHERE [ProgramId] = @ProgramId;");
                procedure.Line($"INSERT INTO [{SchemaName}].[{ExecutionsTableName}] (");
                using (procedure.Indent())
                    procedure.Line("[ExecutionId], [State], [StartedAtUtc], [ProgramId], [LeaseId], [LastHeartbeatAtUtc], [LeaseExpiresAtUtc]");
                procedure.Line(") VALUES (");
                using (procedure.Indent())
                    procedure.Line("@ExecutionId, 1, @NowUtc, @ProgramId, @LeaseId, @NowUtc, DATEADD(SECOND, @LeaseDurationSeconds, @NowUtc)");
                procedure.Line(");");
                EmitProcedureTransactionCommit(procedure);
            }
            procedure.Line("END TRY");
            procedure.Line("BEGIN CATCH");
            using (procedure.Indent())
            {
                EmitProcedureTransactionRollback(procedure, "SharpSqlStartExecution");
                procedure.Line("THROW;");
            }
            procedure.Line("END CATCH;");
        }
        EndProcedure(sql, procedure);
    }

    private static void EmitHeartbeatExecutionProcedure(SqlWriter sql)
    {
        var procedure = BeginProcedure(
            HeartbeatExecutionProcedureName,
            "@ExecutionId UNIQUEIDENTIFIER,",
            "@LeaseId UNIQUEIDENTIFIER,",
            "@LeaseDurationSeconds INT = 30,",
            "@Renewed BIT = NULL OUTPUT");
        using (procedure.Indent())
        {
            procedure.Line("SET NOCOUNT ON;");
            procedure.Line("IF @LeaseDurationSeconds NOT BETWEEN 5 AND 3600");
            using (procedure.Indent())
                procedure.Line($"THROW {InvalidLeaseErrorNumber}, 'A SharpSql execution lease must be between 5 and 3600 seconds.', 1;");
            procedure.Line("DECLARE @NowUtc DATETIME2(7) = SYSUTCDATETIME();");
            procedure.Line($"UPDATE [{SchemaName}].[{ExecutionsTableName}] WITH (UPDLOCK, ROWLOCK)");
            procedure.Line("SET [LastHeartbeatAtUtc] = @NowUtc, [LeaseExpiresAtUtc] = DATEADD(SECOND, @LeaseDurationSeconds, @NowUtc)");
            procedure.Line("WHERE [ExecutionId] = @ExecutionId AND [LeaseId] = @LeaseId AND [State] = 1;");
            procedure.Line("SET @Renewed = CONVERT(BIT, CASE WHEN @@ROWCOUNT = 1 THEN 1 ELSE 0 END);");
        }
        EndProcedure(sql, procedure);
    }

    private static void EmitCancelExecutionProcedure(SqlWriter sql)
    {
        var procedure = BeginProcedure(
            CancelExecutionProcedureName,
            "@ExecutionId UNIQUEIDENTIFIER,",
            "@Reason NVARCHAR(2048) = NULL");
        using (procedure.Indent())
        {
            procedure.Line("SET NOCOUNT ON;");
            EmitProcedureTransactionDeclaration(procedure);
            procedure.Line("DECLARE @NowUtc DATETIME2(7) = SYSUTCDATETIME();");
            procedure.Line("DECLARE @StoredReason NVARCHAR(2048) = COALESCE(@Reason, N'The SharpSql execution was canceled.');");
            procedure.Line("BEGIN TRY");
            using (procedure.Indent())
            {
                EmitProcedureTransactionStart(procedure, "SharpSqlCancelExecution");
                procedure.Line($"IF NOT EXISTS (SELECT 1 FROM [{SchemaName}].[{ExecutionsTableName}] WITH (UPDLOCK, HOLDLOCK) WHERE [ExecutionId] = @ExecutionId)");
                using (procedure.Indent())
                    procedure.Line($"THROW {ExecutionNotFoundErrorNumber}, 'The SharpSql execution does not exist.', 1;");
                procedure.Line($"EXEC [{SchemaName}].[{CompleteExecutionProcedureName}]");
                using (procedure.Indent())
                {
                    procedure.Line("@ExecutionId = @ExecutionId,");
                    procedure.Line("@State = 4,");
                    procedure.Line($"@ErrorNumber = {ExecutionCanceledErrorNumber},");
                    procedure.Line("@ErrorMessage = @StoredReason;");
                }
                procedure.Line($"IF EXISTS (SELECT 1 FROM [{SchemaName}].[{ExecutionsTableName}] WHERE [ExecutionId] = @ExecutionId AND [State] = 4)");
                procedure.Line("BEGIN");
                using (procedure.Indent())
                {
                    procedure.Line($"UPDATE [{SchemaName}].[{ExecutionsTableName}] SET [CancelRequestedAtUtc] = COALESCE([CancelRequestedAtUtc], @NowUtc), [LeaseExpiresAtUtc] = NULL WHERE [ExecutionId] = @ExecutionId;");
                    EmitCancelOutstandingWork(procedure, ExecutionCanceledErrorNumber, "@StoredReason");
                }
                procedure.Line("END;");
                EmitProcedureTransactionCommit(procedure);
            }
            procedure.Line("END TRY");
            procedure.Line("BEGIN CATCH");
            using (procedure.Indent())
            {
                EmitProcedureTransactionRollback(procedure, "SharpSqlCancelExecution");
                procedure.Line("THROW;");
            }
            procedure.Line("END CATCH;");
        }
        EndProcedure(sql, procedure);
    }

    private static void EmitReapAbandonedExecutionsProcedure(SqlWriter sql)
    {
        var procedure = BeginProcedure(
            ReapAbandonedExecutionsProcedureName,
            "@BatchSize INT = 100,",
            "@AsOfUtc DATETIME2(7) = NULL");
        using (procedure.Indent())
        {
            procedure.Line("SET NOCOUNT ON;");
            procedure.Line($"IF @@TRANCOUNT > 0 THROW {AmbientTransactionErrorNumber}, 'Abandoned-execution reaping must run outside an existing transaction.', 1;");
            procedure.Line("IF @BatchSize NOT BETWEEN 1 AND 1000");
            using (procedure.Indent())
                procedure.Line($"THROW {InvalidClaimBatchSizeErrorNumber}, 'The abandoned-execution batch size must be between 1 and 1000.', 1;");
            procedure.Line("SET @AsOfUtc = COALESCE(@AsOfUtc, SYSUTCDATETIME());");
            procedure.Line("DECLARE @Candidates TABLE ([ExecutionId] UNIQUEIDENTIFIER NOT NULL, [LeaseId] UNIQUEIDENTIFIER NOT NULL);");
            procedure.Line($"INSERT INTO @Candidates SELECT TOP (@BatchSize) [ExecutionId], [LeaseId] FROM [{SchemaName}].[{ExecutionsTableName}] WITH (READPAST)");
            procedure.Line("WHERE [State] = 1 AND [LeaseId] IS NOT NULL AND [LeaseExpiresAtUtc] < @AsOfUtc ORDER BY [LeaseExpiresAtUtc], [ExecutionId];");
            procedure.Line("DECLARE @ExecutionId UNIQUEIDENTIFIER;");
            procedure.Line("DECLARE @LeaseId UNIQUEIDENTIFIER;");
            procedure.Line("DECLARE @NowUtc DATETIME2(7);");
            procedure.Line("DECLARE @Reaped TABLE ([ExecutionId] UNIQUEIDENTIFIER NOT NULL);");
            procedure.Line("DECLARE [candidate] CURSOR LOCAL FAST_FORWARD FOR SELECT [ExecutionId], [LeaseId] FROM @Candidates;");
            procedure.Line("OPEN [candidate];");
            procedure.Line("FETCH NEXT FROM [candidate] INTO @ExecutionId, @LeaseId;");
            procedure.Line("WHILE @@FETCH_STATUS = 0");
            procedure.Line("BEGIN");
            using (procedure.Indent())
            {
                procedure.Line("BEGIN TRY");
                using (procedure.Indent())
                {
                    procedure.Line("SET @NowUtc = SYSUTCDATETIME();");
                    procedure.Line("BEGIN TRANSACTION;");
                    procedure.Line($"IF EXISTS (SELECT 1 FROM [{SchemaName}].[{ExecutionsTableName}] WITH (UPDLOCK, HOLDLOCK) WHERE [ExecutionId] = @ExecutionId AND [LeaseId] = @LeaseId AND [State] = 1 AND [LeaseExpiresAtUtc] < @AsOfUtc)");
                    procedure.Line("BEGIN");
                    using (procedure.Indent())
                    {
                        procedure.Line($"EXEC [{SchemaName}].[{CompleteExecutionProcedureName}]");
                        using (procedure.Indent())
                        {
                            procedure.Line("@ExecutionId = @ExecutionId,");
                            procedure.Line("@State = 3,");
                            procedure.Line($"@ErrorNumber = {ExecutionAbandonedErrorNumber},");
                            procedure.Line("@ErrorMessage = N'The SharpSql execution lease expired.';");
                        }
                        procedure.Line($"UPDATE [{SchemaName}].[{ExecutionsTableName}] SET [LeaseExpiresAtUtc] = NULL WHERE [ExecutionId] = @ExecutionId;");
                        EmitCancelOutstandingWork(procedure, ExecutionAbandonedErrorNumber, "N'The SharpSql execution lease expired.'");
                        procedure.Line("INSERT INTO @Reaped ([ExecutionId]) VALUES (@ExecutionId);");
                    }
                    procedure.Line("END;");
                    procedure.Line("COMMIT TRANSACTION;");
                }
                procedure.Line("END TRY");
                procedure.Line("BEGIN CATCH");
                using (procedure.Indent())
                {
                    procedure.Line("IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;");
                    procedure.Line("CLOSE [candidate];");
                    procedure.Line("DEALLOCATE [candidate];");
                    procedure.Line("THROW;");
                }
                procedure.Line("END CATCH;");
                procedure.Line("FETCH NEXT FROM [candidate] INTO @ExecutionId, @LeaseId;");
            }
            procedure.Line("END;");
            procedure.Line("CLOSE [candidate];");
            procedure.Line("DEALLOCATE [candidate];");
            procedure.Line("SELECT [ExecutionId] FROM @Reaped ORDER BY [ExecutionId];");
        }
        EndProcedure(sql, procedure);
    }

    private static void EmitCleanupProgramsProcedure(SqlWriter sql)
    {
        var procedure = BeginProcedure(
            CleanupProgramsProcedureName,
            "@UnusedForMinutes INT = 1440,",
            "@BatchSize INT = 20,",
            "@DryRun BIT = 0");
        using (procedure.Indent())
        {
            procedure.Line("SET NOCOUNT ON;");
            procedure.Line($"IF @@TRANCOUNT > 0 THROW {AmbientTransactionErrorNumber}, 'Program cleanup must run outside an existing transaction.', 1;");
            procedure.Line("IF @UnusedForMinutes < 1 OR @BatchSize NOT BETWEEN 1 AND 100");
            using (procedure.Indent())
                procedure.Line($"THROW {InvalidRetentionErrorNumber}, 'Program retention must be at least one minute and the batch size between 1 and 100.', 1;");
            procedure.Line("DECLARE @CutoffUtc DATETIME2(7) = DATEADD(MINUTE, -@UnusedForMinutes, SYSUTCDATETIME());");
            procedure.Line("DECLARE @Removed TABLE ([ProgramId] NVARCHAR(32) NOT NULL, [ProcedureName] SYSNAME NOT NULL, [Removed] BIT NOT NULL);");
            procedure.Line("IF @DryRun = 1");
            procedure.Line("BEGIN");
            using (procedure.Indent())
            {
                EmitEligibleProgramsQuery(procedure, "SELECT TOP (@BatchSize) [program].[ProgramId], [program].[ProcedureName], CONVERT(BIT, 0) AS [Removed]", false);
                procedure.Line("RETURN;");
            }
            procedure.Line("END;");
            procedure.Line("DECLARE @ProgramId NVARCHAR(32);");
            procedure.Line("DECLARE @ProcedureName SYSNAME;");
            procedure.Line("DECLARE @LockResult INT;");
            procedure.Line("DECLARE @LockResource NVARCHAR(255);");
            procedure.Line("DECLARE @Count INT = 0;");
            procedure.Line("WHILE @Count < @BatchSize");
            procedure.Line("BEGIN");
            using (procedure.Indent())
            {
                procedure.Line("SET @ProgramId = NULL;");
                procedure.Line("BEGIN TRY");
                using (procedure.Indent())
                {
                    procedure.Line("BEGIN TRANSACTION;");
                    EmitEligibleProgramsQuery(procedure, "SELECT TOP (1) @ProgramId = [program].[ProgramId], @ProcedureName = [program].[ProcedureName]", false);
                    procedure.Line("IF @ProgramId IS NULL");
                    procedure.Line("BEGIN");
                    using (procedure.Indent())
                    {
                        procedure.Line("COMMIT TRANSACTION;");
                        procedure.Line("BREAK;");
                    }
                    procedure.Line("END;");
                    EmitProgramLock(procedure);
                    procedure.Line("IF EXISTS (");
                    using (procedure.Indent())
                    {
                        procedure.Line($"SELECT 1 FROM [{SchemaName}].[{ProgramCatalogTableName}] AS [program] WITH (UPDLOCK, HOLDLOCK)");
                        procedure.Line("WHERE [program].[ProgramId] = @ProgramId AND [program].[LastUsedAtUtc] < @CutoffUtc");
                        procedure.Line($"AND NOT EXISTS (SELECT 1 FROM [{SchemaName}].[{ExecutionsTableName}] AS [execution] WHERE [execution].[ProgramId] = @ProgramId AND [execution].[State] NOT IN (2, 3, 4))");
                        procedure.Line($"AND NOT EXISTS (SELECT 1 FROM [{SchemaName}].[{TasksTableName}] AS [task] WHERE [task].[ProgramId] = @ProgramId AND [task].[State] NOT BETWEEN 4 AND 6)");
                    }
                    procedure.Line(")");
                    procedure.Line("BEGIN");
                    using (procedure.Indent())
                    {
                        procedure.Line("DECLARE @QualifiedName NVARCHAR(776) = N'[SharpSql].' + QUOTENAME(@ProcedureName);");
                        procedure.Line("IF OBJECT_ID(@QualifiedName, N'P') IS NOT NULL");
                        procedure.Line("BEGIN");
                        using (procedure.Indent())
                        {
                            procedure.Line("DECLARE @DropStatement NVARCHAR(776) = N'DROP PROCEDURE [SharpSql].' + QUOTENAME(@ProcedureName) + N';';");
                            procedure.Line("EXEC sys.sp_executesql @DropStatement;");
                        }
                        procedure.Line("END;");
                        procedure.Line($"DELETE FROM [{SchemaName}].[{ProgramCatalogTableName}] WHERE [ProgramId] = @ProgramId;");
                        procedure.Line("INSERT INTO @Removed VALUES (@ProgramId, @ProcedureName, CONVERT(BIT, 1));");
                        procedure.Line("SET @Count += 1;");
                    }
                    procedure.Line("END;");
                    procedure.Line("COMMIT TRANSACTION;");
                }
                procedure.Line("END TRY");
                procedure.Line("BEGIN CATCH");
                using (procedure.Indent())
                {
                    procedure.Line("IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;");
                    procedure.Line("THROW;");
                }
                procedure.Line("END CATCH;");
            }
            procedure.Line("END;");
            procedure.Line("SELECT [ProgramId], [ProcedureName], [Removed] FROM @Removed ORDER BY [ProgramId];");
        }
        EndProcedure(sql, procedure);
    }

    private static void EmitServiceBrokerStatusProcedure(SqlWriter sql)
    {
        var procedure = BeginProcedure(ServiceBrokerStatusProcedureName, "@ExecutionId UNIQUEIDENTIFIER = NULL");
        using (procedure.Indent())
        {
            procedure.Line("SET NOCOUNT ON;");
            procedure.Line("SELECT [ExecutionId], [ProgramId], [State], [CreatedAtUtc], [StartedAtUtc], [CompletedAtUtc], [LastHeartbeatAtUtc], [LeaseExpiresAtUtc], [CancelRequestedAtUtc], [ErrorNumber], [ErrorMessage]");
            procedure.Line($"FROM [{SchemaName}].[{ExecutionsTableName}] WHERE @ExecutionId IS NULL OR [ExecutionId] = @ExecutionId ORDER BY [CreatedAtUtc] DESC;");
            procedure.Line("SELECT [program].[ProgramId], [program].[ProcedureName], [program].[InstalledAtUtc], [program].[LastUsedAtUtc],");
            using (procedure.Indent())
            {
                procedure.Line("CONVERT(BIT, CASE WHEN OBJECT_ID(N'[SharpSql].' + QUOTENAME([program].[ProcedureName]), N'P') IS NULL THEN 0 ELSE 1 END) AS [IsInstalled],");
                procedure.Line("(SELECT COUNT_BIG(*) FROM [SharpSql].[Executions] AS [execution] WHERE [execution].[ProgramId] = [program].[ProgramId] AND [execution].[State] NOT IN (2, 3, 4)) AS [ActiveExecutionCount]");
            }
            procedure.Line($"FROM [{SchemaName}].[{ProgramCatalogTableName}] AS [program] ORDER BY [program].[LastUsedAtUtc] DESC;");
        }
        EndProcedure(sql, procedure);
    }

    private static void EmitCancelOutstandingWork(SqlWriter procedure, int errorNumber, string messageExpression)
    {
        procedure.Line($"UPDATE [{SchemaName}].[{TasksTableName}]");
        procedure.Line($"SET [State] = 6, [CompletedAtUtc] = @NowUtc, [ErrorNumber] = {errorNumber}, [ErrorMessage] = {messageExpression},");
        using (procedure.Indent())
            procedure.Line("[DispatchConversationHandle] = NULL, [DispatchConversationId] = NULL");
        procedure.Line("WHERE [ExecutionId] = @ExecutionId AND [State] NOT BETWEEN 4 AND 6;");
        procedure.Line($"UPDATE [{SchemaName}].[{TaskTimersTableName}] SET [State] = 3 WHERE [ExecutionId] = @ExecutionId AND [State] NOT IN (2, 3);");
    }

    private static void EmitEligibleProgramsQuery(SqlWriter procedure, string select, bool lockRows)
    {
        procedure.Line(select);
        procedure.Line($"FROM [{SchemaName}].[{ProgramCatalogTableName}] AS [program]" + (lockRows ? " WITH (UPDLOCK, READPAST, ROWLOCK)" : string.Empty));
        procedure.Line("WHERE [program].[LastUsedAtUtc] < @CutoffUtc");
        procedure.Line($"AND NOT EXISTS (SELECT 1 FROM [{SchemaName}].[{ExecutionsTableName}] AS [execution] WHERE [execution].[ProgramId] = [program].[ProgramId] AND [execution].[State] NOT IN (2, 3, 4))");
        procedure.Line($"AND NOT EXISTS (SELECT 1 FROM [{SchemaName}].[{TasksTableName}] AS [task] WHERE [task].[ProgramId] = [program].[ProgramId] AND [task].[State] NOT BETWEEN 4 AND 6)");
        procedure.Line("ORDER BY [program].[LastUsedAtUtc], [program].[ProgramId];");
    }

    private static void EmitProgramLock(SqlWriter procedure)
    {
        procedure.Line("SET @LockResource = CONCAT(N'SharpSql.ServiceBroker.Program.', @ProgramId);");
        procedure.Line("EXEC @LockResult = sys.sp_getapplock");
        using (procedure.Indent())
        {
            procedure.Line("@Resource = @LockResource,");
            procedure.Line("@LockMode = N'Exclusive',");
            procedure.Line("@LockOwner = N'Transaction',");
            procedure.Line("@LockTimeout = 60000,");
            procedure.Line("@DbPrincipal = N'public';");
        }
        procedure.Line("IF @LockResult < 0");
        using (procedure.Indent())
            procedure.Line($"THROW {ProvisioningLockErrorNumber}, 'Could not acquire the SharpSql program lifecycle lock.', 1;");
    }

    private static SqlWriter BeginProcedure(string name, params string[] parameters)
    {
        var procedure = new SqlWriter();
        procedure.Line($"CREATE OR ALTER PROCEDURE [{SchemaName}].[{name}]");
        using (procedure.Indent())
        {
            for (var index = 0; index < parameters.Length; index++)
                procedure.Line(parameters[index]);
        }
        procedure.Line("AS");
        procedure.Line("BEGIN");
        return procedure;
    }

    private static void EndProcedure(SqlWriter sql, SqlWriter procedure)
    {
        procedure.Line("END;");
        EmitProcedureBatch(sql, procedure);
    }
}
