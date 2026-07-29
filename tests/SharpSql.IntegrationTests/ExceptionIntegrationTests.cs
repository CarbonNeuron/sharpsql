using Microsoft.Data.SqlClient;
using Xunit;

namespace SharpSql.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class ExceptionIntegrationTests(SqlServerFixture sqlServer)
{
    [Fact]
    public async Task DatabaseExceptionCatchesAndExposesANativeSqlServerError()
    {
        const string source = """
            try
            {
                int zero = 0;
                Console.WriteLine(42 / zero);
            }
            catch (SharpSql.DatabaseException exception)
            {
                Console.WriteLine($"{exception.Number}:{exception.Message}");
            }
            """;
        var transpiled = new SharpSqlCompiler().Transpile(source);
        Assert.True(transpiled.Success, string.Join(Environment.NewLine, transpiled.Diagnostics));

        var messages = new List<string>();
        await using var connection = new SqlConnection(sqlServer.ConnectionString)
        {
            FireInfoMessageEventOnUserErrors = true
        };
        connection.InfoMessage += (_, args) =>
        {
            foreach (SqlError error in args.Errors)
                if (error.Class == 0)
                    messages.Add(error.Message);
        };
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = transpiled.Sql;

        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);

        Assert.Contains(messages, message => message.StartsWith("8134:", StringComparison.Ordinal));
    }
}
