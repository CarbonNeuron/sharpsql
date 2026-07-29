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
        Assert.Contains("@ErrorNumber = 51928", sql);
        Assert.Contains("END CONVERSATION @ConversationHandle WITH ERROR = 51925", sql);
    }
}
