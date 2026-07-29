using Microsoft.Data.SqlClient;
using Xunit;

namespace SharpSql.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class ServiceBrokerAsyncExecutionIntegrationTests(SqlServerFixture sqlServer)
{
    [Fact]
    public async Task RollsBackPartiallyScheduledChildrenWhenTheRootFailsBeforeAwait()
    {
        await using var connection = await OpenBrokerDatabaseAsync();
        await ExecuteAsync(connection, SharpSqlServiceBrokerRuntime.GenerateProvisioningSql(), 120);

        const string source = """
            var values = new List<int> { 1, 2 };
            var tasks = values.Select(Work).ToList();
            Console.WriteLine("root-before-failure");
            int zero = values.Count - values.Count;
            int failure = 1 / zero;
            Console.WriteLine(failure);
            await Task.WhenAll(tasks);

            async Task<int> Work(int value)
            {
                await Task.Delay(0);
                Console.WriteLine("child-ran");
                return value;
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
        SqlException exception;
        try
        {
            exception = await ExecuteForSqlErrorAsync(connection, compilation.Sql);
        }
        finally
        {
            connection.InfoMessage -= OnInfoMessage;
        }

        Assert.Equal(51923, exception.Number);
        Assert.DoesNotContain("root-before-failure", messages);
        Assert.DoesNotContain("child-ran", messages);
        await AssertExecutionsCleanedUpAsync(connection);
    }

    [Fact]
    public async Task FailsInsteadOfPollingForeverWhenTheExecutionRowDisappears()
    {
        await using var launcherConnection = await OpenBrokerDatabaseAsync();
        await using var controlConnection = await OpenBrokerDatabaseAsync();
        await ExecuteAsync(launcherConnection, SharpSqlServiceBrokerRuntime.GenerateProvisioningSql(), 120);

        const string source = """
            var values = new List<int> { 1 };
            var tasks = values.Select(Work).ToList();
            await Task.WhenAll(tasks);
            Console.WriteLine("done");

            async Task<int> Work(int value)
            {
                await Task.Delay(5000);
                return value;
            }
            """;
        var compilation = new SharpSqlCompiler().Transpile(
            source,
            new TranspileOptions { RuntimeStorage = RuntimeStorageKind.ServiceBroker });
        Assert.True(compilation.Success, string.Join(Environment.NewLine, compilation.Diagnostics));

        var execution = ExecuteForSqlErrorAsync(launcherConnection, compilation.Sql);
        await WaitForSuspendedExecutionAsync(controlConnection);
        await using (var delete = controlConnection.CreateCommand())
        {
            delete.CommandText = "DELETE FROM [SharpSql].[Executions];";
            await delete.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var exception = await execution;
        Assert.Equal(51922, exception.Number);
        await AssertExecutionsCleanedUpAsync(controlConnection);
    }

    [Fact]
    public async Task IsolatesConcurrentExecutionsAndTheirOutput()
    {
        await using var firstConnection = await OpenBrokerDatabaseAsync();
        await using var secondConnection = await OpenBrokerDatabaseAsync();
        await ExecuteAsync(firstConnection, SharpSqlServiceBrokerRuntime.GenerateProvisioningSql(), 120);

        const string source = """
            var people = new List<Person>
            {
                new("Bob", 40),
                new("Jane", 80)
            };
            Console.WriteLine("before");
            var tasks = people.Select(Work).ToList();
            await Task.WhenAll(tasks);
            var results = tasks.Select(task => task.Result);
            foreach (Person person in results.OrderBy(person => person.Name))
                Console.WriteLine($"after:{person.Name}:{person.Age}");

            async Task<Person> Work(Person person)
            {
                await Task.Delay(person.Age);
                Console.WriteLine($"worker:{person.Name}");
                return person with { Age = person.Age + 1 };
            }

            record Person(string Name, int Age);
            """;
        var compilation = new SharpSqlCompiler().Transpile(
            source,
            new TranspileOptions { RuntimeStorage = RuntimeStorageKind.ServiceBroker });
        Assert.True(compilation.Success, string.Join(Environment.NewLine, compilation.Diagnostics));

        var runs = await Task.WhenAll(
            ExecuteCapturingMessagesAsync(firstConnection, compilation.Sql),
            ExecuteCapturingMessagesAsync(secondConnection, compilation.Sql));

        foreach (var messages in runs)
        {
            Assert.Equal(5, messages.Count);
            Assert.Equal("before", messages[0]);
            Assert.Contains("worker:Bob", messages);
            Assert.Contains("worker:Jane", messages);
            Assert.Equal(
                new[] { "after:Bob:41", "after:Jane:81" },
                messages.Where(message => message.StartsWith("after:", StringComparison.Ordinal)).ToArray());
        }
        await AssertExecutionsCleanedUpAsync(firstConnection);
    }

    [Fact]
    public async Task CatchesDatabaseErrorsAsSdkExceptionsAfterAnAwait()
    {
        await using var connection = await OpenBrokerDatabaseAsync();
        await ExecuteAsync(connection, SharpSqlServiceBrokerRuntime.GenerateProvisioningSql(), 120);

        const string source = """
            var people = new List<Person> { new("Bob", 12) };
            var tasks = people.Select(Work).ToList();
            await Task.WhenAll(tasks);
            var results = tasks.Select(task => task.Result);
            foreach (Person person in results)
                Console.WriteLine($"done:{person.Name}");

            async Task<Person> Work(Person person)
            {
                await Task.Delay(0);
                try
                {
                    int zero = person.Age - person.Age;
                    int value = 1 / zero;
                    Console.WriteLine(value);
                }
                catch (SharpSql.DatabaseException exception)
                {
                    Console.WriteLine($"database:{exception.Number}");
                }
                return person;
            }

            record Person(string Name, int Age);
            """;
        var compilation = new SharpSqlCompiler().Transpile(
            source,
            new TranspileOptions { RuntimeStorage = RuntimeStorageKind.ServiceBroker });
        Assert.True(compilation.Success, string.Join(Environment.NewLine, compilation.Diagnostics));

        var messages = await ExecuteCapturingMessagesAsync(connection, compilation.Sql);

        Assert.Contains("database:8134", messages);
        Assert.Contains("done:Bob", messages);
        await AssertExecutionsCleanedUpAsync(connection);
    }

    [Fact]
    public async Task ExecutesTheCapturedRandomRecordExampleAcrossWorkers()
    {
        await using var connection = await OpenBrokerDatabaseAsync();
        await ExecuteAsync(connection, SharpSqlServiceBrokerRuntime.GenerateProvisioningSql(), 120);

        const string source = """
            var people = new List<Person>
            {
                new("Bob", 12),
                new("John", 20),
                new("Jane", 30),
                new("Jeffery", 40),
                new("Epstein", 50)
            };

            var random = new Random(4);
            PrintPeople(people);

            var tasks = people.Select(AgeUp).ToList();
            await Task.WhenAll(tasks);
            var results = tasks.Select(task => task.Result);
            PrintPeople(results);
            return;

            async Task<Person> AgeUp(Person person)
            {
                try
                {
                    await Task.Delay(random.Next(0, 1000));
                    if (random.Next(0, 5) > 2)
                        throw new ApplicationException();
                }
                catch (ApplicationException)
                {
                    Console.WriteLine($"Don't worry, {person.Name}");
                }
                return person with { Age = person.Age + random.Next(0, 50) };
            }

            void PrintPeople(IEnumerable<Person> values)
            {
                foreach (var person in values.OrderByDescending(value => value.Age))
                    Console.WriteLine(person);
            }

            record Person(string Name, int Age);
            """;
        var compilation = new SharpSqlCompiler().Transpile(
            source,
            new TranspileOptions { RuntimeStorage = RuntimeStorageKind.ServiceBroker });
        Assert.True(compilation.Success, string.Join(Environment.NewLine, compilation.Diagnostics));

        var messages = await ExecuteCapturingMessagesAsync(connection, compilation.Sql);

        foreach (var name in new[] { "Bob", "John", "Jane", "Jeffery", "Epstein" })
        {
            Assert.True(
                messages.Count(message => message.Contains($"Name = {name}", StringComparison.Ordinal)) >= 2,
                $"Expected before/after output for {name}:{Environment.NewLine}{string.Join(Environment.NewLine, messages)}");
        }
        Assert.InRange(messages.Count, 10, 15);
        await AssertExecutionsCleanedUpAsync(connection);
    }

    [Fact]
    public async Task ExecutesForkJoinDelaysCatchAndWorkerOutputEndToEnd()
    {
        await using var connection = await OpenBrokerDatabaseAsync();
        await ExecuteAsync(connection, SharpSqlServiceBrokerRuntime.GenerateProvisioningSql(), 120);

        const string source = """
            var people = new List<Person>
            {
                new("Bob", 12),
                new("John", 20),
                new("Jane", 30)
            };

            Console.WriteLine("before");
            var tasks = people.Select(AgeUp).ToList();
            await Task.WhenAll(tasks);
            var results = tasks.Select(task => task.Result);
            foreach (Person person in results.OrderBy(person => person.Age))
                Console.WriteLine($"after:{person.Name}:{person.Age}");

            async Task<Person> AgeUp(Person person)
            {
                await Task.Delay(person.Age);
                try
                {
                    throw new ApplicationException();
                }
                catch (ApplicationException)
                {
                    Console.WriteLine($"caught:{person.Name}");
                }
                return person with { Age = person.Age + 1 };
            }

            record Person(string Name, int Age);
            """;
        var compilation = new SharpSqlCompiler().Transpile(
            source,
            new TranspileOptions { RuntimeStorage = RuntimeStorageKind.ServiceBroker });
        Assert.True(compilation.Success, string.Join(Environment.NewLine, compilation.Diagnostics));

        var messages = await ExecuteCapturingMessagesAsync(connection, compilation.Sql);

        Assert.Equal("before", messages[0]);
        Assert.Contains("caught:Bob", messages);
        Assert.Contains("caught:John", messages);
        Assert.Contains("caught:Jane", messages);
        Assert.Equal(
            new[] { "after:Bob:13", "after:John:21", "after:Jane:31" },
            messages.Where(message => message.StartsWith("after:", StringComparison.Ordinal)).ToArray());

        await AssertExecutionsCleanedUpAsync(connection);
    }

    private async Task<SqlConnection> OpenBrokerDatabaseAsync()
    {
        const string databaseName = "SharpSqlBrokerAsyncTests";
        await using (var master = new SqlConnection(sqlServer.ConnectionString))
        {
            await master.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = master.CreateCommand();
            command.CommandText = $"""
                IF DB_ID(N'{databaseName}') IS NULL CREATE DATABASE [{databaseName}];
                IF EXISTS (
                    SELECT 1
                    FROM sys.databases
                    WHERE [name] = N'{databaseName}' AND [is_broker_enabled] = 0
                )
                    ALTER DATABASE [{databaseName}] SET ENABLE_BROKER;
                """;
            command.CommandTimeout = 60;
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var connectionString = new SqlConnectionStringBuilder(sqlServer.ConnectionString)
        {
            InitialCatalog = databaseName
        }.ConnectionString;
        var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        return connection;
    }

    private static async Task ExecuteAsync(SqlConnection connection, string sql, int timeoutSeconds)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = timeoutSeconds;
        try
        {
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
        catch (SqlException exception)
        {
            var diagnosticSql = sql;
            var procedureMarker = "EXEC(N'CREATE OR ALTER PROCEDURE [SharpSql].[Program_";
            var procedureStart = sql.IndexOf(procedureMarker, StringComparison.Ordinal);
            if (procedureStart >= 0 && exception.Procedure.StartsWith("Program_", StringComparison.Ordinal))
            {
                procedureStart += "EXEC(N'".Length;
                var procedureEnd = sql.IndexOf("');" + Environment.NewLine + Environment.NewLine + "-- The entry connection", procedureStart, StringComparison.Ordinal);
                if (procedureEnd > procedureStart)
                    diagnosticSql = sql[procedureStart..procedureEnd].Replace("''", "'", StringComparison.Ordinal);
            }
            var lines = diagnosticSql.Split('\n');
            var first = Math.Max(0, exception.LineNumber - 4);
            var context = string.Join(Environment.NewLine, lines.Skip(first).Take(7)
                .Select((line, index) => $"{first + index + 1}: {line}"));
            throw new InvalidOperationException(
                $"SQL failed at line {exception.LineNumber} in '{exception.Procedure}': {exception.Message}{Environment.NewLine}{context}",
                exception);
        }
    }

    private static async Task<SqlException> ExecuteForSqlErrorAsync(SqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 30;
        try
        {
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
        catch (SqlException exception)
        {
            return exception;
        }
        throw new InvalidOperationException("Expected the SQL batch to fail.");
    }

    private static async Task WaitForSuspendedExecutionAsync(SqlConnection connection)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT COUNT_BIG(*)
                FROM [SharpSql].[Executions] AS [execution]
                WHERE EXISTS (
                    SELECT 1
                    FROM [SharpSql].[TaskTimers] AS [timer]
                    WHERE [timer].[ExecutionId] = [execution].[ExecutionId]
                );
                """;
            if ((long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))! > 0)
                return;
            await Task.Delay(25, TestContext.Current.CancellationToken);
        }
        throw new TimeoutException("The Service Broker execution did not reach a suspended timer.");
    }

    private static async Task<IReadOnlyList<string>> ExecuteCapturingMessagesAsync(
        SqlConnection connection,
        string sql)
    {
        var messages = new List<string>();
        void OnInfoMessage(object sender, SqlInfoMessageEventArgs args)
        {
            lock (messages)
                messages.AddRange(args.Errors.Cast<SqlError>().Where(error => error.Class == 0).Select(error => error.Message));
        }

        connection.InfoMessage += OnInfoMessage;
        try
        {
            await ExecuteAsync(connection, sql, 120);
        }
        finally
        {
            connection.InfoMessage -= OnInfoMessage;
        }
        return messages;
    }

    private static async Task AssertExecutionsCleanedUpAsync(SqlConnection connection)
    {
        await using var cleanupCheck = connection.CreateCommand();
        cleanupCheck.CommandText = "SELECT COUNT_BIG(*) FROM [SharpSql].[Executions];";
        Assert.Equal(0L, (long)(await cleanupCheck.ExecuteScalarAsync(TestContext.Current.CancellationToken))!);
    }
}
