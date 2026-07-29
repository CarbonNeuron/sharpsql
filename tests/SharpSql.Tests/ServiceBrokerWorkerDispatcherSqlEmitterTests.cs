using Xunit;

namespace SharpSql.Tests;

public sealed class ServiceBrokerWorkerDispatcherSqlEmitterTests
{
    [Fact]
    public void PublicProvisioningEnablesAValidatedActivatedWorkerPool()
    {
        var sql = SharpSqlServiceBrokerRuntime.GenerateProvisioningSql();

        Assert.StartsWith(ExecutionInfrastructureSqlEmitter.Emit(), sql, StringComparison.Ordinal);
        Assert.Contains("CREATE OR ALTER PROCEDURE [SharpSql].[DispatchWorker]", sql);
        Assert.Contains("FROM [SharpSql].[WorkerQueue]", sql);
        Assert.Contains("LEN(@RequestedProgramId) <> 32", sql);
        Assert.Contains("LIKE N''%[^0-9a-f]%''", sql);
        Assert.Contains("FROM [SharpSql].[Tasks] WITH (UPDLOCK, HOLDLOCK)", sql);
        Assert.Contains("[DispatchConversationId] = @ConversationId", sql);
        Assert.Contains("SET @TaskValidated = 1", sql);
        Assert.Contains("@RequestedProgramId <> @ProgramId OR @RequestedHandlerName <> @HandlerName", sql);
        Assert.Contains("N''[SharpSql].[Program_'' + @ProgramId + N'']''", sql);
        Assert.Contains("EXEC sys.sp_executesql", sql);
        Assert.Contains("ALTER QUEUE [SharpSql].[WorkerQueue]", sql);
        Assert.Contains("PROCEDURE_NAME = [SharpSql].[DispatchWorker]", sql);
        Assert.Contains("MAX_QUEUE_READERS = 8", sql);
        Assert.Contains("EXECUTE AS OWNER", sql);
    }

    [Fact]
    public void DispatcherConsumesBothSidesOfInternalWorkerDialogs()
    {
        var sql = ServiceBrokerWorkerDispatcherSqlEmitter.Emit();

        Assert.Contains("//sharpsql/v1/execution/request", sql);
        Assert.Contains("http://schemas.microsoft.com/SQL/ServiceBroker/EndDialog", sql);
        Assert.Contains("http://schemas.microsoft.com/SQL/ServiceBroker/Error", sql);
        Assert.Contains("END CONVERSATION @ConversationHandle;", sql);
        Assert.Contains("EXEC [SharpSql].[CompleteTask]", sql);
        Assert.Contains("EXEC [SharpSql].[CompleteExecution]", sql);
        Assert.Contains("WHERE [DispatchConversationHandle] = @ConversationHandle AND [State] = 2", sql);
        Assert.Contains("IF @TaskValidated = 1 AND @ExecutionId IS NOT NULL AND @TaskId IS NOT NULL", sql);
        Assert.Contains("SET @BrokerErrorXml = TRY_CONVERT(XML, @MessageBody)", sql);
        Assert.Contains("string((/Error/Code/text())[1])", sql);
        Assert.Contains("string((/Error/Description/text())[1])", sql);
        Assert.Contains("TRY_CONVERT(INT, NULLIF(@BrokerErrorCodeText", sql);
        Assert.Contains("SET @BrokerErrorCodeText = NULL", sql);
        Assert.Contains("SET @BrokerErrorNumber = 51928", sql);
        Assert.Contains("SET @BrokerErrorMessage = NULL", sql);
        Assert.Contains("@ErrorNumber = @BrokerErrorNumber", sql);
        Assert.Contains("@ErrorMessage = @BrokerErrorMessage", sql);
        Assert.Contains("END CONVERSATION @ConversationHandle WITH ERROR = 51925", sql);
        Assert.Contains("DESCRIPTION = @RouterErrorMessage", sql);
    }

    [Fact]
    public void DispatcherRetriesDeadlockedWorkerMessagesInsteadOfFaultingTasks()
    {
        var sql = ServiceBrokerWorkerDispatcherSqlEmitter.Emit();

        var retry = sql.IndexOf("IF @RouterErrorNumber IN (1205, 51929)", StringComparison.Ordinal);
        var fault = sql.IndexOf("EXEC [SharpSql].[CompleteTask]", retry, StringComparison.Ordinal);
        Assert.True(retry >= 0);
        Assert.Contains("WAITFOR DELAY ''00:00:00.100'';", sql[retry..]);
        Assert.Contains("CONTINUE;", sql[retry..]);
        Assert.True(fault > retry);
    }

    [Fact]
    public void DispatcherClearsPerMessageStateBeforeEveryReceive()
    {
        var sql = ServiceBrokerWorkerDispatcherSqlEmitter.Emit();

        var resetStart = sql.IndexOf("SET @ConversationHandle = NULL;", StringComparison.Ordinal);
        var receiveStart = sql.IndexOf("WAITFOR (", StringComparison.Ordinal);
        Assert.True(resetStart >= 0 && resetStart < receiveStart);
        Assert.Contains("SET @ConversationId = NULL;", sql);
        Assert.Contains("SET @MessageTypeName = NULL;", sql);
        Assert.Contains("SET @MessageBody = NULL;", sql);
        Assert.Contains("SET @MessageJson = NULL;", sql);
        Assert.Contains("SET @ExecutionId = NULL;", sql);
        Assert.Contains("SET @TaskId = NULL;", sql);
        Assert.Contains("SET @ProgramId = NULL;", sql);
        Assert.Contains("SET @HandlerName = NULL;", sql);
        Assert.Contains("SET @RequestedProgramId = NULL;", sql);
        Assert.Contains("SET @RequestedHandlerName = NULL;", sql);
        Assert.Contains("SET @TaskValidated = 0;", sql);
    }
}
