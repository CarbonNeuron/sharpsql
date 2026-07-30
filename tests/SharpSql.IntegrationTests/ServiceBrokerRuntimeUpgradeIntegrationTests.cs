using Microsoft.Data.SqlClient;
using SharpSql.IntegrationTests.Fixtures;
using Xunit;

namespace SharpSql.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class ServiceBrokerRuntimeUpgradeIntegrationTests(SqlServerFixture sqlServer)
{
    [Fact]
    public async Task UpgradesAnUnversionedV1SnapshotAndRunsAnAsyncWorkload()
    {
        await using var connection = await OpenUpgradeDatabaseAsync();
        await ExecuteAsync(connection, ServiceBrokerRuntimeV1Snapshot.Sql);
        await ExecuteAsync(connection, SharpSqlServiceBrokerRuntime.GenerateProvisioningSql(), 120);
        await ExecuteAsync(connection, SharpSqlServiceBrokerRuntime.GenerateProvisioningSql(), 120);

        await using (var assertion = connection.CreateCommand())
        {
            assertion.CommandText = """
                EXEC [SharpSql].[AppendOutput]
                    @ExecutionId = @executionId,
                    @OutputText = N'v2-output';

                SELECT
                    (SELECT [SchemaVersion]
                     FROM [SharpSql].[RuntimeManifest]
                     WHERE [RuntimeName] = N'ServiceBroker'),
                    (SELECT MAX([SequenceNumber])
                     FROM [SharpSql].[OutputEvents]
                     WHERE [ExecutionId] = @executionId);
                """;
            assertion.Parameters.AddWithValue("@executionId", ServiceBrokerRuntimeV1Snapshot.ExecutionId);
            await using var reader = await assertion.ExecuteReaderAsync(TestContext.Current.CancellationToken);
            do
            {
                if (reader.FieldCount == 2 && await reader.ReadAsync(TestContext.Current.CancellationToken))
                {
                    Assert.Equal(ExecutionInfrastructureSqlEmitter.CurrentSchemaVersion, reader.GetInt32(0));
                    Assert.Equal(42L, reader.GetInt64(1));
                    break;
                }
            }
            while (await reader.NextResultAsync(TestContext.Current.CancellationToken));
        }

        const string source = """
            var values = new List<int> { 3, 5 };
            var tasks = values.Select(Work).ToList();
            await Task.WhenAll(tasks);
            Console.WriteLine("upgrade-complete");

            async Task<int> Work(int value)
            {
                await Task.Delay(1);
                return value * 2;
            }
            """;
        var compilation = new SharpSqlCompiler().Transpile(
            source,
            new TranspileOptions { RuntimeStorage = RuntimeStorageKind.ServiceBroker });
        Assert.True(compilation.Success, string.Join(Environment.NewLine, compilation.Diagnostics));

        var messages = new List<string>();
        void OnInfoMessage(object sender, SqlInfoMessageEventArgs args) =>
            messages.AddRange(args.Errors.Cast<SqlError>().Where(error => error.Class == 0).Select(error => error.Message));
        connection.InfoMessage += OnInfoMessage;
        try
        {
            await ExecuteAsync(connection, compilation.Sql, 120);
        }
        finally
        {
            connection.InfoMessage -= OnInfoMessage;
        }
        Assert.Contains("upgrade-complete", messages);

        await using (var workloadAssertion = connection.CreateCommand())
        {
            workloadAssertion.CommandText = "SELECT COUNT_BIG(*) FROM [SharpSql].[ServiceBrokerPrograms];";
            Assert.Equal(1L, (long)(await workloadAssertion.ExecuteScalarAsync(TestContext.Current.CancellationToken))!);
        }

        await ExecuteAsync(
            connection,
            $"UPDATE [SharpSql].[RuntimeManifest] SET [SchemaVersion] = {ExecutionInfrastructureSqlEmitter.CurrentSchemaVersion + 1} WHERE [RuntimeName] = N'ServiceBroker';");
        var error = await Assert.ThrowsAsync<SqlException>(
            () => ExecuteAsync(connection, SharpSqlServiceBrokerRuntime.GenerateProvisioningSql(), 120));
        Assert.Equal(ExecutionInfrastructureSqlEmitter.UnsupportedSchemaVersionErrorNumber, error.Number);
    }

    private async Task<SqlConnection> OpenUpgradeDatabaseAsync()
    {
        var databaseName = $"SharpSqlBrokerUpgradeTests_{Guid.NewGuid():N}";
        await using (var master = new SqlConnection(sqlServer.ConnectionString))
        {
            await master.OpenAsync(TestContext.Current.CancellationToken);
            await using var create = master.CreateCommand();
            create.CommandText = $"""
                IF DB_ID(N'{databaseName}') IS NULL CREATE DATABASE [{databaseName}];
                ALTER DATABASE [{databaseName}] SET ENABLE_BROKER;
                """;
            create.CommandTimeout = 60;
            await create.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var connection = new SqlConnection(new SqlConnectionStringBuilder(sqlServer.ConnectionString)
        {
            InitialCatalog = databaseName
        }.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        return connection;
    }

    private static async Task ExecuteAsync(SqlConnection connection, string sql, int timeoutSeconds = 60)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = timeoutSeconds;
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }
}
