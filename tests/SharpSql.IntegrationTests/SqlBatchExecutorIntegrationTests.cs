using Microsoft.Data.SqlClient;
using SharpSql.SqlServer;
using Xunit;

namespace SharpSql.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class SqlBatchExecutorIntegrationTests(SqlServerFixture sqlServer)
{
    [Fact]
    public async Task StreamsMessagesBeforeCompletionAndRetainsThemOnce()
    {
        await using var connection = new SqlConnection(sqlServer.ConnectionString)
        {
            FireInfoMessageEventOnUserErrors = true
        };
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        var firstMessage = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var streamed = new List<string>();
        var execution = SqlBatchExecutor.ExecuteAsync(
            connection,
            """
            RAISERROR(N'first', 0, 1) WITH NOWAIT;
            WAITFOR DELAY '00:00:02';
            RAISERROR(N'second', 0, 1) WITH NOWAIT;
            """,
            30,
            new SqlBatchExecutionOptions(
                MessageReceived: message =>
                {
                    lock (streamed)
                        streamed.Add(message);
                    if (message == "first")
                        firstMessage.TrySetResult();
                }),
            TestContext.Current.CancellationToken);

        await firstMessage.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.False(execution.IsCompleted, "The SQL batch completed before its first message was streamed.");
        var result = await execution;

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(new[] { "first", "second" }, result.Messages);
        Assert.Equal("first\nsecond", result.StandardOutput);
        lock (streamed)
            Assert.Equal(new[] { "first", "second" }, streamed);
    }

    [Fact]
    public async Task CanConsumeHeapCountersWithoutCollectingPlans()
    {
        await using var connection = new SqlConnection(sqlServer.ConnectionString)
        {
            FireInfoMessageEventOnUserErrors = true
        };
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        var result = await SqlBatchExecutor.ExecuteAsync(
            connection,
            """
            PRINT N'__SHARPSQL_DEBUG_HEAP__|objects=7|indexed_items=8|dictionary_entries=9';
            PRINT N'visible';
            """,
            30,
            new SqlBatchExecutionOptions(ConsumeHeapDiagnostics: true),
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(new[] { "visible" }, result.Messages);
        Assert.Equal("visible", result.StandardOutput);
        Assert.Null(result.DebugInfo);
    }

    [Fact]
    public async Task CollectsPlansAndHeapCountersWithoutStreamingTheInternalMarker()
    {
        await using var connection = new SqlConnection(sqlServer.ConnectionString)
        {
            FireInfoMessageEventOnUserErrors = true
        };
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        var streamed = new List<string>();
        var result = await SqlBatchExecutor.ExecuteAsync(
            connection,
            """
            PRINT N'__SHARPSQL_DEBUG_HEAP__|objects=7|indexed_items=8|dictionary_entries=9';
            SELECT [value] FROM (VALUES (1), (2)) AS [items] ([value]) WHERE [value] > 0;
            SELECT [value] FROM (VALUES (1), (2)) AS [items] ([value]) WHERE [value] > 0;
            RAISERROR(N'visible', 0, 1) WITH NOWAIT;
            """,
            30,
            new SqlBatchExecutionOptions(CollectDebugInfo: true, MessageReceived: streamed.Add),
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(new[] { "visible" }, streamed);
        Assert.Equal(new[] { "visible" }, result.Messages);
        var debug = Assert.IsType<SqlBatchDebugInfo>(result.DebugInfo);
        Assert.Equal(1, debug.PlanStatementCount);
        Assert.Equal(7, debug.HeapObjectsAllocated);
        Assert.Equal(8, debug.IndexedItemsAllocated);
        Assert.Equal(9, debug.DictionaryEntriesAllocated);
        Assert.True(debug.HeapDiagnosticsObserved);
    }

    [Fact]
    public async Task StreamsPriorOutputButKeepsSqlErrorsOnTheFailurePath()
    {
        await using var connection = new SqlConnection(sqlServer.ConnectionString)
        {
            FireInfoMessageEventOnUserErrors = true
        };
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        var streamed = new List<string>();
        var result = await SqlBatchExecutor.ExecuteAsync(
            connection,
            """
            RAISERROR(N'before failure', 0, 1) WITH NOWAIT;
            THROW 51012, 'expected failure', 1;
            """,
            30,
            new SqlBatchExecutionOptions(MessageReceived: streamed.Add),
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(51012, result.ErrorNumber);
        Assert.Equal("expected failure", result.ErrorMessage);
        Assert.Equal(new[] { "before failure" }, streamed);
        Assert.Equal(new[] { "before failure" }, result.Messages);
    }
}
