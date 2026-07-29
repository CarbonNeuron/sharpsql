namespace SharpSql;

/// <summary>
/// Emits the fixed activated router. Program-specific procedures are selected only from
/// validated compiler hashes, so message data cannot inject an arbitrary procedure name.
/// </summary>
internal static class ServiceBrokerWorkerDispatcherSqlEmitter
{
    internal const string ProcedureName = "DispatchWorker";
    internal const int InvalidMessageErrorNumber = 51925;
    internal const int ProgramNotInstalledErrorNumber = 51926;
    internal const int BrokerDeliveryErrorNumber = 51928;

    internal static string Emit()
    {
        var procedure = new SqlWriter();
        procedure.Line($"CREATE OR ALTER PROCEDURE [SharpSql].[{ProcedureName}]");
        procedure.Line("AS");
        procedure.Line("BEGIN");
        using (procedure.Indent())
        {
            procedure.Line("SET NOCOUNT ON;");
            procedure.Line("SET XACT_ABORT ON;");
            procedure.Line();
            procedure.Line("WHILE 1 = 1");
            procedure.Line("BEGIN");
            using (procedure.Indent())
            {
                procedure.Line("DECLARE @ConversationHandle UNIQUEIDENTIFIER;");
                procedure.Line("DECLARE @ConversationId UNIQUEIDENTIFIER;");
                procedure.Line("DECLARE @MessageTypeName NVARCHAR(256);");
                procedure.Line("DECLARE @MessageBody VARBINARY(MAX);");
                procedure.Line("DECLARE @MessageJson NVARCHAR(MAX);");
                procedure.Line("DECLARE @ExecutionId UNIQUEIDENTIFIER;");
                procedure.Line("DECLARE @TaskId BIGINT;");
                procedure.Line("DECLARE @ProgramId NVARCHAR(128);");
                procedure.Line("DECLARE @HandlerName NVARCHAR(450);");
                procedure.Line("DECLARE @RequestedProgramId NVARCHAR(128);");
                procedure.Line("DECLARE @RequestedHandlerName NVARCHAR(450);");
                procedure.Line("DECLARE @TaskValidated BIT;");
                procedure.Line();
                procedure.Line("-- DECLARE does not reinitialize variables on later WHILE iterations.");
                procedure.Line("SET @ConversationHandle = NULL;");
                procedure.Line("SET @ConversationId = NULL;");
                procedure.Line("SET @MessageTypeName = NULL;");
                procedure.Line("SET @MessageBody = NULL;");
                procedure.Line("SET @MessageJson = NULL;");
                procedure.Line("SET @ExecutionId = NULL;");
                procedure.Line("SET @TaskId = NULL;");
                procedure.Line("SET @ProgramId = NULL;");
                procedure.Line("SET @HandlerName = NULL;");
                procedure.Line("SET @RequestedProgramId = NULL;");
                procedure.Line("SET @RequestedHandlerName = NULL;");
                procedure.Line("SET @TaskValidated = 0;");
                procedure.Line();
                procedure.Line("BEGIN TRY");
                using (procedure.Indent())
                {
                    procedure.Line("BEGIN TRANSACTION;");
                    procedure.Line("WAITFOR (");
                    using (procedure.Indent())
                    {
                        procedure.Line("RECEIVE TOP (1)");
                        using (procedure.Indent())
                        {
                            procedure.Line("@ConversationHandle = [conversation_handle],");
                            procedure.Line("@MessageTypeName = [message_type_name],");
                            procedure.Line("@MessageBody = [message_body]");
                        }
                        procedure.Line("FROM [SharpSql].[WorkerQueue]");
                    }
                    procedure.Line("), TIMEOUT 1000;");
                    procedure.Line();
                    procedure.Line("IF @ConversationHandle IS NULL");
                    procedure.Line("BEGIN");
                    using (procedure.Indent())
                    {
                        procedure.Line("COMMIT TRANSACTION;");
                        procedure.Line("BREAK;");
                    }
                    procedure.Line("END;");
                    procedure.Line("SELECT @ConversationId = [conversation_id]");
                    procedure.Line("FROM sys.conversation_endpoints");
                    procedure.Line("WHERE [conversation_handle] = @ConversationHandle;");
                    procedure.Line();
                    procedure.Line($"IF @MessageTypeName = N'{ExecutionInfrastructureSqlEmitter.RequestMessageTypeName}'");
                    procedure.Line("BEGIN");
                    using (procedure.Indent())
                    {
                        procedure.Line("SET @MessageJson = CONVERT(NVARCHAR(MAX), @MessageBody);");
                        procedure.Line("IF ISJSON(@MessageJson) <> 1");
                        using (procedure.Indent())
                            procedure.Line($"THROW {InvalidMessageErrorNumber}, 'A SharpSql worker request must contain JSON.', 1;");
                        procedure.Line("SELECT");
                        using (procedure.Indent())
                        {
                            procedure.Line("@ExecutionId = TRY_CONVERT(UNIQUEIDENTIFIER, JSON_VALUE(@MessageJson, '$.executionId')), ");
                            procedure.Line("@TaskId = TRY_CONVERT(BIGINT, JSON_VALUE(@MessageJson, '$.taskId')), ");
                            procedure.Line("@RequestedProgramId = JSON_VALUE(@MessageJson, '$.programId'),");
                            procedure.Line("@RequestedHandlerName = JSON_VALUE(@MessageJson, '$.handlerName');");
                        }
                        procedure.Line("IF @ConversationId IS NULL OR @ExecutionId IS NULL OR @TaskId IS NULL OR @RequestedProgramId IS NULL OR @RequestedHandlerName IS NULL OR");
                        using (procedure.Indent())
                            procedure.Line("LEN(@RequestedProgramId) <> 32 OR @RequestedProgramId COLLATE Latin1_General_100_BIN2 LIKE N'%[^0-9a-f]%'");
                        using (procedure.Indent())
                            procedure.Line($"THROW {InvalidMessageErrorNumber}, 'A SharpSql worker request has an invalid route.', 1;");
                        procedure.Line();
                        procedure.Line("SELECT @ProgramId = [ProgramId], @HandlerName = [HandlerName]");
                        procedure.Line("FROM [SharpSql].[Tasks] WITH (UPDLOCK, HOLDLOCK)");
                        procedure.Line("WHERE [ExecutionId] = @ExecutionId AND [TaskId] = @TaskId");
                        using (procedure.Indent())
                            procedure.Line("AND [DispatchConversationId] = @ConversationId AND [State] = 2;");
                        procedure.Line("IF @ProgramId IS NULL OR @HandlerName IS NULL");
                        using (procedure.Indent())
                            procedure.Line($"THROW {InvalidMessageErrorNumber}, 'The SharpSql worker task is missing or is not enqueued.', 1;");
                        procedure.Line("SET @TaskValidated = 1;");
                        procedure.Line("IF @RequestedProgramId <> @ProgramId OR @RequestedHandlerName <> @HandlerName");
                        using (procedure.Indent())
                            procedure.Line($"THROW {InvalidMessageErrorNumber}, 'The SharpSql worker request does not match its durable task route.', 1;");
                        procedure.Line();
                        procedure.Line("DECLARE @ProcedureName NVARCHAR(776) = N'[SharpSql].[Program_' + @ProgramId + N']';");
                        procedure.Line("IF OBJECT_ID(@ProcedureName, N'P') IS NULL");
                        using (procedure.Indent())
                            procedure.Line($"THROW {ProgramNotInstalledErrorNumber}, 'The compiled SharpSql worker procedure is not installed.', 1;");
                        procedure.Line("DECLARE @Statement NVARCHAR(MAX) = N'EXEC ' + @ProcedureName + N' @__sharpsql_execution_id = @ExecutionId, @__sharpsql_task_id = @TaskId;';");
                        procedure.Line("EXEC sys.sp_executesql");
                        using (procedure.Indent())
                        {
                            procedure.Line("@Statement,");
                            procedure.Line("N'@ExecutionId UNIQUEIDENTIFIER, @TaskId BIGINT',");
                            procedure.Line("@ExecutionId = @ExecutionId,");
                            procedure.Line("@TaskId = @TaskId;");
                        }
                        procedure.Line("END CONVERSATION @ConversationHandle;");
                    }
                    procedure.Line("END");
                    procedure.Line("ELSE IF @MessageTypeName = N'http://schemas.microsoft.com/SQL/ServiceBroker/Error'");
                    procedure.Line("BEGIN");
                    using (procedure.Indent())
                    {
                        procedure.Line("DECLARE @BrokerErrorXml XML;");
                        procedure.Line("DECLARE @BrokerErrorCodeText NVARCHAR(32);");
                        procedure.Line("DECLARE @BrokerErrorNumber INT;");
                        procedure.Line("DECLARE @BrokerErrorMessage NVARCHAR(3000);");
                        procedure.Line("SET @BrokerErrorXml = TRY_CONVERT(XML, @MessageBody);");
                        procedure.Line("SET @BrokerErrorCodeText = NULL;");
                        procedure.Line($"SET @BrokerErrorNumber = {BrokerDeliveryErrorNumber};");
                        procedure.Line("SET @BrokerErrorMessage = NULL;");
                        procedure.Line("IF @BrokerErrorXml IS NOT NULL");
                        procedure.Line("BEGIN");
                        using (procedure.Indent())
                        {
                            procedure.Line("SELECT");
                            using (procedure.Indent())
                            {
                                procedure.Line("@BrokerErrorCodeText = @BrokerErrorXml.value('declare default element namespace \"http://schemas.microsoft.com/SQL/ServiceBroker/Error\"; string((/Error/Code/text())[1])', 'NVARCHAR(32)'),");
                                procedure.Line("@BrokerErrorMessage = NULLIF(@BrokerErrorXml.value('declare default element namespace \"http://schemas.microsoft.com/SQL/ServiceBroker/Error\"; string((/Error/Description/text())[1])', 'NVARCHAR(3000)'), N'');");
                            }
                            procedure.Line($"SET @BrokerErrorNumber = COALESCE(TRY_CONVERT(INT, NULLIF(@BrokerErrorCodeText, N'')), {BrokerDeliveryErrorNumber});");
                        }
                        procedure.Line("END;");
                        procedure.Line("SET @BrokerErrorMessage = COALESCE(@BrokerErrorMessage, N'Service Broker could not deliver a SharpSql worker request.');");
                        procedure.Line();
                        procedure.Line("SELECT @ExecutionId = [ExecutionId], @TaskId = [TaskId], @HandlerName = [HandlerName]");
                        procedure.Line("FROM [SharpSql].[Tasks] WITH (UPDLOCK, HOLDLOCK)");
                        procedure.Line("WHERE [DispatchConversationHandle] = @ConversationHandle AND [State] = 2;");
                        procedure.Line("IF @ExecutionId IS NOT NULL AND @TaskId IS NOT NULL");
                        procedure.Line("BEGIN");
                        using (procedure.Indent())
                        {
                            procedure.Line("SET @TaskValidated = 1;");
                            procedure.Line("EXEC [SharpSql].[CompleteTask]");
                            using (procedure.Indent())
                            {
                                procedure.Line("@ExecutionId = @ExecutionId,");
                                procedure.Line("@TaskId = @TaskId,");
                                procedure.Line("@State = 5,");
                                procedure.Line("@ErrorNumber = @BrokerErrorNumber,");
                                procedure.Line("@ErrorMessage = @BrokerErrorMessage;");
                            }
                            procedure.Line("IF @HandlerName = N'__entry'");
                            using (procedure.Indent())
                            {
                                procedure.Line("EXEC [SharpSql].[CompleteExecution]");
                                using (procedure.Indent())
                                {
                                    procedure.Line("@ExecutionId = @ExecutionId,");
                                    procedure.Line("@State = 3,");
                                    procedure.Line("@ErrorNumber = @BrokerErrorNumber,");
                                    procedure.Line("@ErrorMessage = @BrokerErrorMessage;");
                                }
                            }
                        }
                        procedure.Line("END;");
                        procedure.Line("END CONVERSATION @ConversationHandle;");
                    }
                    procedure.Line("END");
                    procedure.Line("ELSE IF @MessageTypeName = N'http://schemas.microsoft.com/SQL/ServiceBroker/EndDialog'");
                    using (procedure.Indent())
                        procedure.Line("END CONVERSATION @ConversationHandle;");
                    procedure.Line("ELSE IF @MessageTypeName = N'http://schemas.microsoft.com/SQL/ServiceBroker/DialogTimer'");
                    procedure.Line("BEGIN");
                    using (procedure.Indent())
                    {
                        procedure.Line("EXEC [SharpSql].[ClaimDueContinuations] @BatchSize = 100;");
                        procedure.Line("END CONVERSATION @ConversationHandle;");
                    }
                    procedure.Line("END");
                    procedure.Line("ELSE");
                    using (procedure.Indent())
                    {
                        procedure.Line($"END CONVERSATION @ConversationHandle WITH ERROR = {InvalidMessageErrorNumber} DESCRIPTION = N'Unsupported SharpSql worker message.';");
                    }
                    procedure.Line();
                    procedure.Line("COMMIT TRANSACTION;");
                }
                procedure.Line("END TRY");
                procedure.Line("BEGIN CATCH");
                using (procedure.Indent())
                {
                    procedure.Line("IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;");
                    procedure.Line("DECLARE @RouterErrorNumber INT = ERROR_NUMBER();");
                    procedure.Line("DECLARE @RouterErrorMessage NVARCHAR(2048) = LEFT(ERROR_MESSAGE(), 2048);");
                    procedure.Line();
                    procedure.Line("BEGIN TRY");
                    using (procedure.Indent())
                    {
                        procedure.Line("IF @TaskValidated = 1 AND @ExecutionId IS NOT NULL AND @TaskId IS NOT NULL");
                        procedure.Line("BEGIN");
                        using (procedure.Indent())
                        {
                            procedure.Line("EXEC [SharpSql].[CompleteTask]");
                            using (procedure.Indent())
                            {
                                procedure.Line("@ExecutionId = @ExecutionId,");
                                procedure.Line("@TaskId = @TaskId,");
                                procedure.Line("@State = 5,");
                                procedure.Line("@ErrorNumber = @RouterErrorNumber,");
                                procedure.Line("@ErrorMessage = @RouterErrorMessage;");
                            }
                            procedure.Line("IF @HandlerName = N'__entry'");
                            using (procedure.Indent())
                            {
                                procedure.Line("EXEC [SharpSql].[CompleteExecution]");
                                using (procedure.Indent())
                                {
                                    procedure.Line("@ExecutionId = @ExecutionId,");
                                    procedure.Line("@State = 3,");
                                    procedure.Line("@ErrorNumber = @RouterErrorNumber,");
                                    procedure.Line("@ErrorMessage = @RouterErrorMessage;");
                                }
                            }
                        }
                        procedure.Line("END;");
                    }
                    procedure.Line("END TRY");
                    procedure.Line("BEGIN CATCH");
                    using (procedure.Indent())
                        procedure.Line("IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;");
                    procedure.Line("END CATCH;");
                    procedure.Line();
                    procedure.Line("BEGIN TRY");
                    using (procedure.Indent())
                    {
                        procedure.Line("IF @ConversationHandle IS NOT NULL");
                        using (procedure.Indent())
                            procedure.Line($"END CONVERSATION @ConversationHandle WITH ERROR = {InvalidMessageErrorNumber} DESCRIPTION = @RouterErrorMessage;");
                    }
                    procedure.Line("END TRY");
                    procedure.Line("BEGIN CATCH");
                    using (procedure.Indent())
                        procedure.Line("IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;");
                    procedure.Line("END CATCH;");
                }
                procedure.Line("END CATCH;");
            }
            procedure.Line("END;");
        }
        procedure.Line("END;");

        var sql = new SqlWriter();
        var escaped = procedure.ToString().TrimEnd().Replace("'", "''", StringComparison.Ordinal);
        sql.Line($"EXEC(N'{escaped}');");
        sql.Line($"ALTER QUEUE [SharpSql].[{ExecutionInfrastructureSqlEmitter.WorkerQueueName}]");
        sql.Line("WITH ACTIVATION (");
        using (sql.Indent())
        {
            sql.Line("STATUS = ON,");
            sql.Line($"PROCEDURE_NAME = [SharpSql].[{ProcedureName}],");
            sql.Line("MAX_QUEUE_READERS = 8,");
            sql.Line("EXECUTE AS OWNER");
        }
        sql.Line(");");
        return sql.ToString();
    }
}
