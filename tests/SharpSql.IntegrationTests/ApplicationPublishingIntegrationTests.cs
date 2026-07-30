using Microsoft.Data.SqlClient;
using SharpSql.SqlServer;
using Xunit;

namespace SharpSql.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class ApplicationPublishingIntegrationTests(SqlServerFixture sqlServer)
{
    [Fact]
    public async Task RepeatedPublishUpdatesOneManifestRowAndEntryProcedure()
    {
        const string schema = "PublishingTests";
        const string application = "IdempotentJob";
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new SqlConnection(sqlServer.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        var initial = Package("1.0.0", "PRINT N'first version';").GenerateInstallSql();
        var upgraded = Package("2.0.0", "PRINT N'second version';").GenerateInstallSql();

        await AssertSucceedsAsync(connection, initial);
        await AssertSucceedsAsync(connection, initial);
        await AssertSucceedsAsync(connection, upgraded);
        await AssertSucceedsAsync(connection, upgraded);

        await using (var manifest = connection.CreateCommand())
        {
            manifest.CommandText = $"""
                SELECT COUNT_BIG(*), MAX([PackageVersion])
                FROM [{schema}].[PackageManifest]
                WHERE [ApplicationName] = N'{application}';
                """;
            await using var reader = await manifest.ExecuteReaderAsync(cancellationToken);
            Assert.True(await reader.ReadAsync(cancellationToken));
            Assert.Equal(1L, reader.GetInt64(0));
            Assert.Equal("2.0.0", reader.GetString(1));
        }

        var execution = await SqlBatchExecutor.ExecuteAsync(
            connection,
            $"EXEC [{schema}].[Run];",
            60,
            cancellationToken);

        Assert.True(execution.Success, execution.ErrorMessage);
        Assert.Equal(["second version"], execution.Messages);

        static SharpSqlApplicationPackage Package(string version, string compiledSql) => new(
            schema,
            application,
            version,
            compiledSql);
    }

    [Fact]
    public async Task MemoryOptimizedPackageInstallsSchemaTypesAndRunsCompiledProgram()
    {
        const string schema = "PublishingMemoryTests";
        const string source = """
            int Sum(int value)
            {
                if (value == 0)
                    return 0;
                return value + Sum(value - 1);
            }

            int result = Sum(10);
            Console.WriteLine(result);
            """;
        var compilation = new SharpSqlCompiler().Transpile(
            source,
            new TranspileOptions
            {
                RuntimeStorage = RuntimeStorageKind.MemoryOptimized,
                ApplicationSchema = schema
            });
        Assert.True(compilation.Success, string.Join(Environment.NewLine, compilation.Diagnostics));
        var package = new SharpSqlApplicationPackage(
            schema,
            "MemoryJob",
            "3.1.4",
            compilation.Sql)
        {
            RuntimeStorage = RuntimeStorageKind.MemoryOptimized
        };
        var cancellationToken = TestContext.Current.CancellationToken;
        var connectionString = await sqlServer.GetMemoryOptimizedConnectionStringAsync(cancellationToken);
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await AssertSucceedsAsync(connection, package.GenerateInstallSql());
        await AssertSucceedsAsync(connection, package.GenerateInstallSql());

        var execution = await SqlBatchExecutor.ExecuteAsync(
            connection,
            $"EXEC [{schema}].[Run];",
            60,
            cancellationToken);
        Assert.True(execution.Success, execution.ErrorMessage);
        Assert.Equal(["55"], execution.Messages);

        await using var verification = connection.CreateCommand();
        verification.CommandText = $"""
            SELECT
                TYPE_ID(N'[{schema}].[MemoryVmStackV1]'),
                TYPE_ID(N'[{schema}].[MemoryVmSlotsV1]'),
                [PackageVersion],
                [RuntimeStorage]
            FROM [{schema}].[PackageManifest]
            WHERE [ApplicationName] = N'MemoryJob';
            """;
        await using var reader = await verification.ExecuteReaderAsync(cancellationToken);
        Assert.True(await reader.ReadAsync(cancellationToken));
        Assert.False(reader.IsDBNull(0));
        Assert.False(reader.IsDBNull(1));
        Assert.Equal("3.1.4", reader.GetString(2));
        Assert.Equal(nameof(RuntimeStorageKind.MemoryOptimized), reader.GetString(3));
    }

    private static async Task AssertSucceedsAsync(SqlConnection connection, string sql)
    {
        var result = await SqlBatchExecutor.ExecuteAsync(
            connection,
            sql,
            60,
            TestContext.Current.CancellationToken);
        Assert.True(result.Success, result.ErrorMessage);
    }
}
