using Microsoft.Data.SqlClient;
using Xunit;

namespace SharpSql.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class DurableRuntimeIntegrationTests(SqlServerFixture sqlServer)
{
    [Fact]
    public async Task ConcurrentExecutionsShareTablesButNotState()
    {
        const string source = """
            var people = new List<Person>
            {
                new("Bob", 12),
                new("Jane", 30)
            };

            int SumTo(int value) => value <= 0 ? 0 : value + SumTo(value - 1);

            var random = new Random(4);
            Console.WriteLine(people[0].Age + SumTo(4) + random.Next(0, 1));

            record Person(string Name, int Age);
            """;
        var transpiled = new SharpSqlCompiler().Transpile(
            source,
            new TranspileOptions { RuntimeStorage = RuntimeStorageKind.Durable });

        Assert.True(transpiled.Success, string.Join(Environment.NewLine, transpiled.Diagnostics));

        await using var first = await OpenConnectionAsync();
        await using var second = await OpenConnectionAsync();
        var outputs = await Task.WhenAll(
            ExecuteAsync(first, transpiled.Sql),
            ExecuteAsync(second, transpiled.Sql));

        Assert.Equal(["22", "22"], outputs);
        Assert.Equal(0L, await ScalarAsync(first, "SELECT COUNT_BIG(*) FROM [SharpSql].[__sharpsql_objects];"));
        Assert.Equal(0L, await ScalarAsync(first, "SELECT COUNT_BIG(*) FROM [SharpSql].[__sharpsql_stack];"));
        Assert.Equal(0L, await ScalarAsync(first, "SELECT COUNT_BIG(*) FROM [SharpSql].[__sharpsql_slots];"));
    }

    [Fact]
    public async Task TopLevelReturnRunsDurableCleanup()
    {
        const string source = """
            var values = new List<int> { 1, 2, 3 };
            return;
            """;
        var transpiled = CompileDurable(source);

        await using var connection = await OpenConnectionAsync();
        await ExecuteAsync(connection, transpiled.Sql);

        Assert.Equal(0L, await ScalarAsync(connection, "SELECT COUNT_BIG(*) FROM [SharpSql].[__sharpsql_objects];"));
        Assert.Equal(0L, await ScalarAsync(connection, "SELECT COUNT_BIG(*) FROM [SharpSql].[__sharpsql_indexed_items];"));
    }

    [Fact]
    public async Task RuntimeFailureRunsDurableCleanupBeforeRethrowing()
    {
        const string source = """
            var values = new List<int> { 1, 2, 3 };
            Console.WriteLine(values[10]);
            """;
        var transpiled = CompileDurable(source);

        await using var connection = await OpenConnectionAsync();
        connection.FireInfoMessageEventOnUserErrors = false;
        var exception = await Assert.ThrowsAsync<SqlException>(async () =>
            await ExecuteAsync(connection, transpiled.Sql));

        Assert.Equal(51002, exception.Number);
        Assert.Equal(0L, await ScalarAsync(connection, "SELECT COUNT_BIG(*) FROM [SharpSql].[__sharpsql_objects];"));
        Assert.Equal(0L, await ScalarAsync(connection, "SELECT COUNT_BIG(*) FROM [SharpSql].[__sharpsql_indexed_items];"));
    }

    private static TranspileResult CompileDurable(string source)
    {
        var transpiled = new SharpSqlCompiler().Transpile(
            source,
            new TranspileOptions { RuntimeStorage = RuntimeStorageKind.Durable });
        Assert.True(transpiled.Success, string.Join(Environment.NewLine, transpiled.Diagnostics));
        return transpiled;
    }

    private async Task<SqlConnection> OpenConnectionAsync()
    {
        var connection = new SqlConnection(sqlServer.ConnectionString)
        {
            FireInfoMessageEventOnUserErrors = true
        };
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        return connection;
    }

    private static async Task<string> ExecuteAsync(SqlConnection connection, string sql)
    {
        var messages = new List<string>();
        connection.InfoMessage += HandleMessage;
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.CommandTimeout = 60;
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            return string.Join(Environment.NewLine, messages);
        }
        finally
        {
            connection.InfoMessage -= HandleMessage;
        }

        void HandleMessage(object sender, SqlInfoMessageEventArgs args)
        {
            foreach (SqlError error in args.Errors)
                if (error.Class == 0)
                    messages.Add(error.Message);
        }
    }

    private static async Task<long> ScalarAsync(SqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(TestContext.Current.CancellationToken),
            System.Globalization.CultureInfo.InvariantCulture);
    }
}
