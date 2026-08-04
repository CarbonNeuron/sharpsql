using Microsoft.Data.SqlClient;
using Xunit;

namespace SharpSql.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class ServiceBrokerAsyncExecutionIntegrationTests(SqlServerFixture sqlServer)
{
    private readonly string _databaseName = $"SharpSqlBrokerAsyncTests_{Guid.NewGuid():N}";

    [Fact]
    public async Task PersistsTheBrokerErrorCodeAndDescriptionForAnUndeliverableRootTask()
    {
        await using var connection = await OpenBrokerDatabaseAsync();
        await ExecuteAsync(connection, SharpSqlServiceBrokerRuntime.GenerateProvisioningSql(), 120);

        var executionId = Guid.NewGuid();
        await ExecuteAsync(
            connection,
            "ALTER QUEUE [SharpSql].[WorkerQueue] WITH ACTIVATION (STATUS = OFF);",
            120);
        try
        {
            long taskId;
            await using (var arrange = connection.CreateCommand())
            {
                arrange.CommandText = """
                    INSERT INTO [SharpSql].[Executions] ([ExecutionId], [State], [StartedAtUtc])
                    VALUES (@executionId, 1, SYSUTCDATETIME());

                    DECLARE @taskId BIGINT;
                    DECLARE @scheduled TABLE (
                        [TaskId] BIGINT NOT NULL,
                        [InitialState] TINYINT NOT NULL,
                        [Enqueued] BIT NOT NULL,
                        [DueAtUtc] DATETIME2(3) NULL
                    );
                    INSERT INTO @scheduled ([TaskId], [InitialState], [Enqueued], [DueAtUtc])
                    EXEC [SharpSql].[ScheduleTask]
                        @ExecutionId = @executionId,
                        @ProgramId = N'00000000000000000000000000000000',
                        @HandlerName = N'__entry',
                        @PayloadJson = N'{}',
                        @TaskId = @taskId OUTPUT;

                    DECLARE @targetHandle UNIQUEIDENTIFIER;
                    WAITFOR (
                        RECEIVE TOP (1) @targetHandle = [conversation_handle]
                        FROM [SharpSql].[WorkerQueue]
                    ), TIMEOUT 10000;
                    IF @targetHandle IS NULL
                        THROW 51998, 'The worker queue did not receive the test request.', 1;

                    END CONVERSATION @targetHandle
                        WITH ERROR = 56789 DESCRIPTION = N'Native broker delivery description';

                    SELECT @taskId;
                    """;
                arrange.Parameters.AddWithValue("@executionId", executionId);
                arrange.CommandTimeout = 30;
                taskId = (long)(await arrange.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
            }

            await ExecuteAsync(
                connection,
                "ALTER QUEUE [SharpSql].[WorkerQueue] WITH ACTIVATION (STATUS = ON);",
                120);

            var completed = false;
            for (var attempt = 0; attempt < 200 && !completed; attempt++)
            {
                await using var poll = connection.CreateCommand();
                poll.CommandText = "SELECT [State] FROM [SharpSql].[Executions] WHERE [ExecutionId] = @executionId;";
                poll.Parameters.AddWithValue("@executionId", executionId);
                completed = Equals(
                    await poll.ExecuteScalarAsync(TestContext.Current.CancellationToken),
                    (byte)3);
                if (completed)
                    break;
                await Task.Delay(25, TestContext.Current.CancellationToken);
            }

            Assert.True(completed, "The dispatcher did not persist the broker error before the timeout.");
            await using var assertion = connection.CreateCommand();
            assertion.CommandText = """
                SELECT
                    [task].[State],
                    [task].[ErrorNumber],
                    [task].[ErrorMessage],
                    [execution].[State],
                    [execution].[ErrorNumber],
                    [execution].[ErrorMessage]
                FROM [SharpSql].[Tasks] AS [task]
                INNER JOIN [SharpSql].[Executions] AS [execution]
                    ON [execution].[ExecutionId] = [task].[ExecutionId]
                WHERE [task].[ExecutionId] = @executionId AND [task].[TaskId] = @taskId;
                """;
            assertion.Parameters.AddWithValue("@executionId", executionId);
            assertion.Parameters.AddWithValue("@taskId", taskId);
            await using var reader = await assertion.ExecuteReaderAsync(TestContext.Current.CancellationToken);
            Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
            Assert.Equal((byte)5, reader.GetByte(0));
            Assert.Equal(56789, reader.GetInt32(1));
            Assert.Equal("Native broker delivery description", reader.GetString(2));
            Assert.Equal((byte)3, reader.GetByte(3));
            Assert.Equal(56789, reader.GetInt32(4));
            Assert.Equal("Native broker delivery description", reader.GetString(5));
        }
        finally
        {
            try
            {
                await using var cleanup = connection.CreateCommand();
                cleanup.CommandText = "DELETE FROM [SharpSql].[Executions] WHERE [ExecutionId] = @executionId;";
                cleanup.Parameters.AddWithValue("@executionId", executionId);
                await cleanup.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }
            finally
            {
                await ExecuteAsync(
                    connection,
                    "ALTER QUEUE [SharpSql].[WorkerQueue] WITH ACTIVATION (STATUS = ON);",
                    120);
            }
        }
    }

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
        SqlError exception;
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
    public async Task PreAwaitFailureFaultsOnlyItsChildTask()
    {
        await using var connection = await OpenBrokerDatabaseAsync();
        await ExecuteAsync(connection, SharpSqlServiceBrokerRuntime.GenerateProvisioningSql(), 120);

        const string source = """
            var values = new List<int> { 1, 2 };
            var tasks = values.Select(Work).ToList();
            await Task.WhenAll(tasks);
            Console.WriteLine("after");

            async Task<int> Work(int value)
            {
                if (value == 1)
                    throw new ApplicationException("pre-await");
                await Task.Delay(10);
                Console.WriteLine($"completed:{value}");
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
        SqlError exception;
        try
        {
            exception = await ExecuteForSqlErrorAsync(connection, compilation.Sql);
        }
        finally
        {
            connection.InfoMessage -= OnInfoMessage;
        }

        Assert.Equal(51012, exception.Number);
        Assert.Contains("completed:2", messages);
        Assert.DoesNotContain("after", messages);
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
    public async Task ConcurrentRandomRecordExamplePreservesSchedulingIndependentInvariants()
    {
        await using var connection = await OpenBrokerDatabaseAsync();
        await ExecuteAsync(connection, SharpSqlServiceBrokerRuntime.GenerateProvisioningSql(), 120);

        const string source = """
            using System.Linq;
            using System.Threading.Tasks;

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
        var expectedInitialOutput = """
            Person { Name = Epstein, Age = 50 }
            Person { Name = Jeffery, Age = 40 }
            Person { Name = Jane, Age = 30 }
            Person { Name = John, Age = 20 }
            Person { Name = Bob, Age = 12 }
            """.Split('\n');

        var compilation = new SharpSqlCompiler().Transpile(
            source,
            new TranspileOptions { RuntimeStorage = RuntimeStorageKind.ServiceBroker });
        Assert.True(compilation.Success, string.Join(Environment.NewLine, compilation.Diagnostics));

        var messages = await ExecuteCapturingMessagesAsync(connection, compilation.Sql);

        Assert.Equal(expectedInitialOutput, messages.Take(5));
        var catches = messages
            .Skip(5)
            .Where(message => message.StartsWith("Don't worry, ", StringComparison.Ordinal))
            .ToArray();
        var finalPeople = messages
            .Skip(5)
            .Where(message => message.StartsWith("Person { ", StringComparison.Ordinal))
            .Select(ParsePerson)
            .ToArray();
        Assert.Equal(5, finalPeople.Length);
        Assert.Equal(5 + catches.Length + finalPeople.Length, messages.Count);

        var initialAges = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["Bob"] = 12,
            ["John"] = 20,
            ["Jane"] = 30,
            ["Jeffery"] = 40,
            ["Epstein"] = 50
        };
        Assert.Equal(initialAges.Keys.Order(), finalPeople.Select(person => person.Name).Order());
        Assert.All(finalPeople, person =>
            Assert.InRange(person.Age, initialAges[person.Name], initialAges[person.Name] + 49));
        Assert.Equal(
            finalPeople.Select(person => person.Age).OrderDescending(),
            finalPeople.Select(person => person.Age));
        Assert.All(catches, message =>
            Assert.Contains(message["Don't worry, ".Length..], initialAges.Keys));
        Assert.Equal(catches.Length, catches.Distinct(StringComparer.Ordinal).Count());
        await AssertExecutionsCleanedUpAsync(connection);

        static (string Name, int Age) ParsePerson(string message)
        {
            const string prefix = "Person { Name = ";
            const string separator = ", Age = ";
            const string suffix = " }";
            Assert.StartsWith(prefix, message, StringComparison.Ordinal);
            Assert.EndsWith(suffix, message, StringComparison.Ordinal);
            var separatorIndex = message.IndexOf(separator, prefix.Length, StringComparison.Ordinal);
            Assert.True(separatorIndex > prefix.Length);
            var name = message[prefix.Length..separatorIndex];
            var age = int.Parse(
                message[(separatorIndex + separator.Length)..^suffix.Length],
                System.Globalization.CultureInfo.InvariantCulture);
            return (name, age);
        }
    }

    [Fact]
    public async Task StreamsOutputBeforeTheAsyncExecutionCompletes()
    {
        await using var connection = await OpenBrokerDatabaseAsync();
        await ExecuteAsync(connection, SharpSqlServiceBrokerRuntime.GenerateProvisioningSql(), 120);

        const string source = """
            var values = new List<int> { 1 };
            Console.WriteLine("started 100%");
            var tasks = values.Select(Work).ToList();
            await Task.WhenAll(tasks);
            Console.WriteLine("finished");

            async Task<int> Work(int value)
            {
                await Task.Delay(2000);
                return value;
            }
            """;
        var compilation = new SharpSqlCompiler().Transpile(
            source,
            new TranspileOptions { RuntimeStorage = RuntimeStorageKind.ServiceBroker });
        Assert.True(compilation.Success, string.Join(Environment.NewLine, compilation.Diagnostics));

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnInfoMessage(object sender, SqlInfoMessageEventArgs args)
        {
            if (args.Errors.Cast<SqlError>().Any(error => error.Class == 0 && error.Message == "started 100%"))
                started.TrySetResult();
        }

        connection.InfoMessage += OnInfoMessage;
        Task execution;
        try
        {
            execution = ExecuteAsync(connection, compilation.Sql, 120);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.False(execution.IsCompleted, "The async execution completed before its first output was streamed.");
            await execution;
        }
        finally
        {
            connection.InfoMessage -= OnInfoMessage;
        }
        await AssertExecutionsCleanedUpAsync(connection);
    }

    [Fact]
    public async Task EqualDelayContinuationsUseMultipleWorkersAndShareRandomSafely()
    {
        await using var connection = await OpenBrokerDatabaseAsync();
        await ExecuteAsync(connection, SharpSqlServiceBrokerRuntime.GenerateProvisioningSql(), 120);

        const string source = """
            using System.Threading;

            var values = new List<int> { 1, 2, 3, 4, 5, 6, 7, 8 };
            var random = new Random(4);
            var tasks = values.Select(Work).ToList();
            await Task.WhenAll(tasks);
            var results = tasks.Select(task => task.Result);
            foreach (int result in results.OrderBy(value => value))
                Console.WriteLine($"result:{result}");

            async Task<int> Work(int value)
            {
                await Task.Delay(100);
                int sample;
                if (value % 2 == 0)
                {
                    Console.WriteLine($"worker:{Thread.GetCurrentProcessorId()}:{value}");
                    sample = random.Next(0, 1000000);
                }
                else
                {
                    sample = random.Next(0, 1000000);
                    Console.WriteLine($"worker:{Thread.GetCurrentProcessorId()}:{value}");
                }
                int checksum = 0;
                for (int iteration = 0; iteration < 250000; iteration++)
                    checksum ^= iteration + value;
                return sample;
            }
            """;
        var compilation = new SharpSqlCompiler().Transpile(
            source,
            new TranspileOptions { RuntimeStorage = RuntimeStorageKind.ServiceBroker });
        Assert.True(compilation.Success, string.Join(Environment.NewLine, compilation.Diagnostics));

        var messages = await ExecuteCapturingMessagesAsync(connection, compilation.Sql);

        var workerMessages = messages
            .Where(message => message.StartsWith("worker:", StringComparison.Ordinal))
            .ToArray();
        Assert.True(
            workerMessages.Length == 8,
            $"Expected eight worker messages, but received: {string.Join(" | ", messages)}");
        Assert.Equal(
            Enumerable.Range(1, 8),
            workerMessages
                .Select(message => int.Parse(message.Split(':')[2], System.Globalization.CultureInfo.InvariantCulture))
                .Order());
        var workerIds = workerMessages
            .Select(message => int.Parse(message.Split(':')[1], System.Globalization.CultureInfo.InvariantCulture))
            .ToArray();
        Assert.All(workerIds, workerId =>
        {
            Assert.True(workerId > 0);
            Assert.NotEqual(connection.ServerProcessId, workerId);
        });
        Assert.True(
            workerIds.Distinct().Count() > 1,
            $"Expected multiple activated workers, but all continuations used SPID {workerIds[0]}.");
        var expectedRandom = new Random(4);
        var expectedSamples = Enumerable.Range(0, 8)
            .Select(_ => expectedRandom.Next(0, 1000000))
            .Order();
        Assert.Equal(
            expectedSamples,
            messages
                .Where(message => message.StartsWith("result:", StringComparison.Ordinal))
                .Select(message => int.Parse(message.Split(':')[1], System.Globalization.CultureInfo.InvariantCulture)));
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

    [Fact]
    public async Task ExecutesRegisterBytecodeHelpersToCompletionInsideBrokerWorkers()
    {
        await using var connection = await OpenBrokerDatabaseAsync();
        await ExecuteAsync(connection, SharpSqlServiceBrokerRuntime.GenerateProvisioningSql(), 120);

        const string source = """
            var values = new List<int> { 6 };
            var tasks = values.Select(Work).ToList();
            await Task.WhenAll(tasks);
            var results = tasks.Select(task => task.Result);
            foreach (int result in results)
                Console.WriteLine("result:" + result);

            async Task<int> Work(int value)
            {
                await Task.Delay(25);
                Emit(Decorate("worker"));
                return Fib(value);
            }

            string Echo(string value)
            {
                string copy = value;
                return copy;
            }

            string Decorate(string value)
            {
                string decorated = "[" + Echo(value);
                return decorated + "]";
            }

            void Emit(string value)
            {
                string output = value;
                Console.WriteLine(output);
            }

            int Fib(int value) => value <= 1 ? value : Fib(value - 1) + Fib(value - 2);
            """;
        var compilation = new SharpSqlCompiler().Transpile(
            source,
            new TranspileOptions
            {
                RuntimeStorage = RuntimeStorageKind.ServiceBroker,
                ManagedFallback = ManagedFallbackKind.Bytecode,
                MaxInlineStatements = 1
            });
        Assert.True(compilation.Success, string.Join(Environment.NewLine, compilation.Diagnostics));
        Assert.True(compilation.UsesRegisterBytecode);

        var messages = await ExecuteCapturingMessagesAsync(connection, compilation.Sql);

        Assert.Contains("[worker]", messages);
        Assert.Contains("result:8", messages);
        await AssertExecutionsCleanedUpAsync(connection);
    }

    private async Task<SqlConnection> OpenBrokerDatabaseAsync()
    {
        await using (var master = new SqlConnection(sqlServer.ConnectionString))
        {
            await master.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = master.CreateCommand();
            command.CommandText = $"""
                IF DB_ID(N'{_databaseName}') IS NULL CREATE DATABASE [{_databaseName}];
                IF EXISTS (
                    SELECT 1
                    FROM sys.databases
                    WHERE [name] = N'{_databaseName}' AND [is_broker_enabled] = 0
                )
                    ALTER DATABASE [{_databaseName}] SET ENABLE_BROKER;
                """;
            command.CommandTimeout = 60;
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var connectionString = new SqlConnectionStringBuilder(sqlServer.ConnectionString)
        {
            InitialCatalog = _databaseName
        }.ConnectionString;
        var connection = new SqlConnection(connectionString)
        {
            FireInfoMessageEventOnUserErrors = true
        };
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

    private static async Task<SqlError> ExecuteForSqlErrorAsync(SqlConnection connection, string sql)
    {
        SqlError? reportedError = null;
        void OnInfoMessage(object sender, SqlInfoMessageEventArgs args) =>
            reportedError ??= args.Errors.Cast<SqlError>().FirstOrDefault(error => error.Class > 0);

        connection.InfoMessage += OnInfoMessage;
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 30;
        try
        {
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
        catch (SqlException exception)
        {
            return exception.Errors.Cast<SqlError>().FirstOrDefault(error => error.Class > 0)
                ?? exception.Errors.Cast<SqlError>().First();
        }
        finally
        {
            connection.InfoMessage -= OnInfoMessage;
        }
        return reportedError ?? throw new InvalidOperationException("Expected the SQL batch to fail.");
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
