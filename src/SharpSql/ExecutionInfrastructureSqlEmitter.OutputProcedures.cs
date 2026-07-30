namespace SharpSql;

internal static partial class ExecutionInfrastructureSqlEmitter
{
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

}

