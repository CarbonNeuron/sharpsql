using Microsoft.Data.SqlClient;
using SharpSql.SqlServer;
using Xunit;

namespace SharpSql.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class MemoryOptimizedIndexedItemsIntegrationTests(SqlServerFixture sqlServer)
{
    private const string IndexedItemsTable =
        "[SharpSql].[__sharpsql_memory_heap_indexed_items_ephemeral_v1]";

    private static readonly TranspileOptions MemoryOptimizedOptions = new()
    {
        Execution = RuntimeExecutionKind.Inline,
        Durability = RuntimeDurabilityKind.Ephemeral,
        UseMemoryOptimizedTables = true
    };

    [Fact]
    public async Task ListsAndMaterializedLinqRetainExpectedParityAndCleanUp()
    {
        const string source = """
            var values = new List<int> { 4, 1, 3, 1, 2, 4 };
            values.RemoveAt(1);
            var page = values
                .Where(value => value > 1)
                .OrderByDescending(value => value)
                .Take(3)
                .ToList();

            foreach (var value in page)
                Console.WriteLine($"page={value}");
            Console.WriteLine($"sum={page.Sum()}");
            """;
        var sql = Compile(source);
        await using var connection = await OpenConnectionAsync();

        var execution = await ExecuteAsync(connection, sql);

        Assert.True(execution.Success, execution.ErrorMessage);
        Assert.Equal(["page=4", "page=4", "page=3", "sum=11"], execution.Messages);
        await AssertIndexedItemsEmptyAsync(connection);
    }

    [Fact]
    public async Task ConcurrentListAndLinqExecutionsRemainIsolatedAndCleanUp()
    {
        const string source = """
            var values = Enumerable.Range(1, 512).ToList();
            var evens = values.Where(value => value % 2 == 0).ToList();
            Console.WriteLine($"count={evens.Count}");
            Console.WriteLine($"sum={evens.Sum()}");
            """;
        var sql = Compile(source);
        var connectionString = await sqlServer.GetMemoryOptimizedConnectionStringAsync(
            TestContext.Current.CancellationToken);

        var executions = Enumerable.Range(0, 8).Select(async _ =>
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            return await ExecuteAsync(connection, sql);
        });
        var results = await Task.WhenAll(executions);

        Assert.All(results, result =>
        {
            Assert.True(result.Success, result.ErrorMessage);
            Assert.Equal(["count=256", "sum=65792"], result.Messages);
        });
        await using var verification = new SqlConnection(connectionString);
        await verification.OpenAsync(TestContext.Current.CancellationToken);
        await AssertIndexedItemsEmptyAsync(verification);
    }

    [Fact]
    public async Task RuntimeFailureCleansUpIndexedItemsBeforeRethrowing()
    {
        const string source = """
            var values = Enumerable.Range(1, 32).ToList();
            var evens = values.Where(value => value % 2 == 0).ToList();
            Console.WriteLine(evens[100]);
            """;
        var sql = Compile(source);
        await using var connection = await OpenConnectionAsync();

        var execution = await ExecuteAsync(connection, sql);

        Assert.False(execution.Success);
        Assert.Equal(51002, execution.ErrorNumber);
        await AssertIndexedItemsEmptyAsync(connection);
    }

    private static string Compile(string source)
    {
        var result = new SharpSqlCompiler().Transpile(source, MemoryOptimizedOptions);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains(IndexedItemsTable, result.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("#__sharpsql_indexed_items", result.Sql, StringComparison.Ordinal);
        return result.Sql;
    }

    private async Task<SqlConnection> OpenConnectionAsync()
    {
        var connectionString = await sqlServer.GetMemoryOptimizedConnectionStringAsync(
            TestContext.Current.CancellationToken);
        var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        return connection;
    }

    private static Task<SqlBatchExecutionResult> ExecuteAsync(SqlConnection connection, string sql) =>
        SqlBatchExecutor.ExecuteAsync(
            connection,
            sql,
            120,
            TestContext.Current.CancellationToken);

    private static async Task AssertIndexedItemsEmptyAsync(SqlConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT_BIG(*) FROM {IndexedItemsTable};";
        var remaining = Convert.ToInt64(
            await command.ExecuteScalarAsync(TestContext.Current.CancellationToken),
            System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(0L, remaining);
    }
}
