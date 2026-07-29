using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Xunit;

namespace SharpSql.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class ServiceBrokerInfrastructureIntegrationTests(SqlServerFixture sqlServer)
{
    [Fact]
    public async Task ProvisioningIsRepeatableAndOutputAppendIsOrdered()
    {
        await using var connection = await OpenBrokerDatabaseAsync();

        var provisioning = ExecutionInfrastructureSqlEmitter.Emit();
        await ExecuteAsync(connection, provisioning);
        await ExecuteAsync(connection, provisioning);

        var executionId = Guid.NewGuid();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO [SharpSql].[Executions] ([ExecutionId]) VALUES (@executionId);
                EXEC [SharpSql].[AppendOutput] @ExecutionId = @executionId, @OutputText = N'first';
                EXEC [SharpSql].[AppendOutput] @ExecutionId = @executionId, @OutputText = N'second';
                """;
            command.Parameters.AddWithValue("@executionId", executionId);
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT [SequenceNumber], [OutputText]
                FROM [SharpSql].[OutputEvents]
                WHERE [ExecutionId] = @executionId
                ORDER BY [SequenceNumber];
                """;
            command.Parameters.AddWithValue("@executionId", executionId);
            await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
            Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
            Assert.Equal(1L, reader.GetInt64(0));
            Assert.Equal("first", reader.GetString(1));
            Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
            Assert.Equal(2L, reader.GetInt64(0));
            Assert.Equal("second", reader.GetString(1));
            Assert.False(await reader.ReadAsync(TestContext.Current.CancellationToken));
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "DELETE FROM [SharpSql].[Executions] WHERE [ExecutionId] = @executionId;";
            command.Parameters.AddWithValue("@executionId", executionId);
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task CaughtDatabaseErrorRollsBackToProcedureSavepointAndLeavesAmbientTransactionCommittable()
    {
        await using var connection = await OpenBrokerDatabaseAsync();
        await ExecuteAsync(connection, ExecutionInfrastructureSqlEmitter.Emit());

        var executionId = Guid.NewGuid();
        var persistedExecutionId = Guid.NewGuid();
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO [SharpSql].[Executions] ([ExecutionId], [NextOutputSequence])
                VALUES (@executionId, 1);
                INSERT INTO [SharpSql].[OutputEvents] ([ExecutionId], [SequenceNumber], [OutputText])
                VALUES (@executionId, 1, N'existing');

                DECLARE @caughtError INT;
                DECLARE @caughtXactState INT;

                SET XACT_ABORT ON;
                BEGIN TRANSACTION;
                BEGIN TRY
                    EXEC [SharpSql].[AppendOutput]
                        @ExecutionId = @executionId,
                        @OutputText = N'duplicate sequence';
                END TRY
                BEGIN CATCH
                    SELECT @caughtError = ERROR_NUMBER(), @caughtXactState = XACT_STATE();
                END CATCH;

                IF @caughtError IS NULL
                BEGIN
                    ROLLBACK TRANSACTION;
                    SET XACT_ABORT OFF;
                    THROW 51997, 'AppendOutput unexpectedly succeeded.', 1;
                END;
                IF XACT_STATE() <> 1
                BEGIN
                    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
                    SET XACT_ABORT OFF;
                    THROW 51996, 'The ambient transaction was not committable after the caught error.', 1;
                END;

                INSERT INTO [SharpSql].[Executions] ([ExecutionId]) VALUES (@persistedExecutionId);
                COMMIT TRANSACTION;
                SET XACT_ABORT OFF;

                SELECT
                    @caughtError AS [CaughtError],
                    @caughtXactState AS [CaughtXactState],
                    (SELECT [NextOutputSequence] FROM [SharpSql].[Executions] WHERE [ExecutionId] = @executionId) AS [NextOutputSequence],
                    (SELECT COUNT(*) FROM [SharpSql].[OutputEvents] WHERE [ExecutionId] = @executionId) AS [OutputCount],
                    (SELECT COUNT(*) FROM [SharpSql].[Executions] WHERE [ExecutionId] = @persistedExecutionId) AS [SubsequentWorkCount];
                """;
            command.Parameters.AddWithValue("@executionId", executionId);
            command.Parameters.AddWithValue("@persistedExecutionId", persistedExecutionId);
            await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
            Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
            Assert.Contains(reader.GetInt32(0), new[] { 2601, 2627 });
            Assert.Equal(1, reader.GetInt32(1));
            Assert.Equal(1L, reader.GetInt64(2));
            Assert.Equal(1, reader.GetInt32(3));
            Assert.Equal(1, reader.GetInt32(4));
        }
        finally
        {
            await using var cleanup = connection.CreateCommand();
            cleanup.CommandText = """
                IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
                SET XACT_ABORT OFF;
                DELETE FROM [SharpSql].[Executions] WHERE [ExecutionId] IN (@executionId, @persistedExecutionId);
                """;
            cleanup.Parameters.AddWithValue("@executionId", executionId);
            cleanup.Parameters.AddWithValue("@persistedExecutionId", persistedExecutionId);
            await cleanup.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task CompletionNotificationsCarrySuccessAndFaultPayloadsExactlyOnce()
    {
        await using var connection = await OpenBrokerDatabaseAsync();
        await ExecuteAsync(connection, ExecutionInfrastructureSqlEmitter.Emit());

        await AssertCompletionAsync(
            connection,
            state: 2,
            errorNumber: null,
            errorMessage: null);
        await AssertCompletionAsync(
            connection,
            state: 3,
            errorNumber: 8134,
            errorMessage: "Divide by zero.");
    }

    [Fact]
    public async Task ConcurrentDependencyCompletionEnqueuesTheWhenAllContinuationExactlyOnce()
    {
        await using var connection = await OpenBrokerDatabaseAsync();
        await ExecuteAsync(connection, ExecutionInfrastructureSqlEmitter.Emit());

        var executionId = Guid.NewGuid();
        await InsertExecutionAsync(connection, executionId);
        try
        {
            var firstDependency = await ScheduleTaskAsync(connection, executionId, "join-program", "FirstDependency");
            var secondDependency = await ScheduleTaskAsync(connection, executionId, "join-program", "SecondDependency");
            var continuation = await ScheduleTaskAsync(
                connection,
                executionId,
                "join-program",
                "WhenAllContinuation",
                startSuspended: true);

            Assert.NotNull(await ReceiveTaskRequestAsync(connection, executionId, 10_000));
            Assert.NotNull(await ReceiveTaskRequestAsync(connection, executionId, 10_000));

            await using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    EXEC [SharpSql].[SuspendTaskForDependencies]
                        @ExecutionId = @executionId,
                        @TaskId = @continuation,
                        @ContinuationState = 17,
                        @PayloadJson = N'{"locals":{"answer":42}}',
                        @ExpectedDependencyCount = 2;
                    EXEC [SharpSql].[RegisterTaskDependency]
                        @ExecutionId = @executionId,
                        @ContinuationTaskId = @continuation,
                        @DependencyTaskId = @firstDependency;
                    EXEC [SharpSql].[RegisterTaskDependency]
                        @ExecutionId = @executionId,
                        @ContinuationTaskId = @continuation,
                        @DependencyTaskId = @secondDependency;
                    """;
                command.Parameters.AddWithValue("@executionId", executionId);
                command.Parameters.AddWithValue("@continuation", continuation);
                command.Parameters.AddWithValue("@firstDependency", firstDependency);
                command.Parameters.AddWithValue("@secondDependency", secondDependency);
                await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }

            var brokerConnectionString = BrokerConnectionString();
            await using var firstConnection = new SqlConnection(brokerConnectionString);
            await using var secondConnection = new SqlConnection(brokerConnectionString);
            await Task.WhenAll(
                firstConnection.OpenAsync(TestContext.Current.CancellationToken),
                secondConnection.OpenAsync(TestContext.Current.CancellationToken));

            var firstCompletion = CompleteTaskAsync(firstConnection, executionId, firstDependency);
            var secondCompletion = CompleteTaskAsync(secondConnection, executionId, secondDependency);
            await Task.WhenAll(firstCompletion, secondCompletion);

            var request = await ReceiveTaskRequestAsync(connection, executionId, 10_000);
            Assert.NotNull(request);
            Assert.Equal(continuation, request.Value.TaskId);
            Assert.Equal("join-program", request.Value.ProgramId);
            Assert.Equal("WhenAllContinuation", request.Value.HandlerName);
            Assert.Equal(17, request.Value.ContinuationState);
            Assert.Null(await ReceiveTaskRequestAsync(connection, executionId, 500));

            await using var assertion = connection.CreateCommand();
            assertion.CommandText = """
                SELECT
                    [join].[RegisteredDependencyCount],
                    [join].[CompletedDependencyCount],
                    CASE WHEN [join].[ReadyAtUtc] IS NULL THEN 0 ELSE 1 END,
                    CASE WHEN [join].[EnqueuedAtUtc] IS NULL THEN 0 ELSE 1 END,
                    [task].[State]
                FROM [SharpSql].[TaskJoins] AS [join]
                INNER JOIN [SharpSql].[Tasks] AS [task]
                    ON [task].[ExecutionId] = [join].[ExecutionId]
                    AND [task].[TaskId] = [join].[ContinuationTaskId]
                WHERE [join].[ExecutionId] = @executionId
                    AND [join].[ContinuationTaskId] = @continuation;
                """;
            assertion.Parameters.AddWithValue("@executionId", executionId);
            assertion.Parameters.AddWithValue("@continuation", continuation);
            await using var reader = await assertion.ExecuteReaderAsync(TestContext.Current.CancellationToken);
            Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
            Assert.Equal(2, reader.GetInt32(0));
            Assert.Equal(2, reader.GetInt32(1));
            Assert.Equal(1, reader.GetInt32(2));
            Assert.Equal(1, reader.GetInt32(3));
            Assert.Equal((byte)2, reader.GetByte(4));
        }
        finally
        {
            await DeleteExecutionAsync(connection, executionId);
        }
    }

    [Fact]
    public async Task MillisecondDelayClaimsAndEnqueuesTheSameTaskOnceWhenDue()
    {
        await using var connection = await OpenBrokerDatabaseAsync();
        await ExecuteAsync(connection, ExecutionInfrastructureSqlEmitter.Emit());

        var executionId = Guid.NewGuid();
        await InsertExecutionAsync(connection, executionId);
        try
        {
            var taskId = await ScheduleTaskAsync(connection, executionId, "timer-program", "ResumeAfterDelay");
            Assert.NotNull(await ReceiveTaskRequestAsync(connection, executionId, 10_000));

            await using (var suspend = connection.CreateCommand())
            {
                suspend.CommandText = """
                    EXEC [SharpSql].[SuspendTaskForDelay]
                        @ExecutionId = @executionId,
                        @TaskId = @taskId,
                        @ContinuationState = 29,
                        @PayloadJson = N'{"local":"preserved"}',
                        @DelayMilliseconds = 150;
                    """;
                suspend.Parameters.AddWithValue("@executionId", executionId);
                suspend.Parameters.AddWithValue("@taskId", taskId);
                await suspend.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }

            Assert.Equal(0, await ClaimDueContinuationsAsync(connection));
            Assert.Null(await ReceiveTaskRequestAsync(connection, executionId, 50));

            await Task.Delay(250, TestContext.Current.CancellationToken);

            var brokerConnectionString = BrokerConnectionString();
            await using var firstClaimConnection = new SqlConnection(brokerConnectionString);
            await using var secondClaimConnection = new SqlConnection(brokerConnectionString);
            await Task.WhenAll(
                firstClaimConnection.OpenAsync(TestContext.Current.CancellationToken),
                secondClaimConnection.OpenAsync(TestContext.Current.CancellationToken));
            var claims = await Task.WhenAll(
                ClaimDueContinuationsAsync(firstClaimConnection),
                ClaimDueContinuationsAsync(secondClaimConnection));
            Assert.Equal(1, claims.Sum());

            var request = await ReceiveTaskRequestAsync(connection, executionId, 10_000);
            Assert.NotNull(request);
            Assert.Equal(taskId, request.Value.TaskId);
            Assert.Equal("timer-program", request.Value.ProgramId);
            Assert.Equal("ResumeAfterDelay", request.Value.HandlerName);
            Assert.Equal(29, request.Value.ContinuationState);
            Assert.Null(await ReceiveTaskRequestAsync(connection, executionId, 500));

            await using var assertion = connection.CreateCommand();
            assertion.CommandText = """
                SELECT
                    [timer].[State],
                    DATEDIFF(MILLISECOND, [timer].[CreatedAtUtc], [timer].[DueAtUtc]),
                    [task].[State],
                    [task].[TaskId],
                    CASE WHEN EXISTS (
                        SELECT 1
                        FROM sys.conversation_endpoints AS [endpoint]
                        WHERE [endpoint].[conversation_handle] = [task].[DispatchConversationHandle]
                            AND [endpoint].[conversation_id] = [task].[DispatchConversationId]
                    ) THEN 1 ELSE 0 END
                FROM [SharpSql].[TaskTimers] AS [timer]
                INNER JOIN [SharpSql].[Tasks] AS [task]
                    ON [task].[ExecutionId] = [timer].[ExecutionId]
                    AND [task].[TaskId] = [timer].[TaskId]
                WHERE [timer].[ExecutionId] = @executionId AND [timer].[TaskId] = @taskId;
                """;
            assertion.Parameters.AddWithValue("@executionId", executionId);
            assertion.Parameters.AddWithValue("@taskId", taskId);
            await using var reader = await assertion.ExecuteReaderAsync(TestContext.Current.CancellationToken);
            Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
            Assert.Equal((byte)2, reader.GetByte(0));
            Assert.InRange(reader.GetInt32(1), 100, 200);
            Assert.Equal((byte)2, reader.GetByte(2));
            Assert.Equal(taskId, reader.GetInt64(3));
            Assert.Equal(1, reader.GetInt32(4));
        }
        finally
        {
            await DeleteExecutionAsync(connection, executionId);
        }
    }

    [Fact]
    public async Task DispatcherReturnsAfterConsumingTheLastEndDialog()
    {
        await using var connection = await OpenBrokerDatabaseAsync("SharpSqlBrokerDispatcherLoopTests");
        await ExecuteAsync(connection, SharpSqlServiceBrokerRuntime.GenerateProvisioningSql());

        await using (var setup = connection.CreateCommand())
        {
            setup.CommandText = """
                ALTER QUEUE [SharpSql].[WorkerQueue] WITH ACTIVATION (STATUS = OFF);

                DECLARE @initiatorHandle UNIQUEIDENTIFIER;
                BEGIN DIALOG CONVERSATION @initiatorHandle
                    FROM SERVICE [//sharpsql/v1/worker]
                    TO SERVICE N'//sharpsql/v1/worker'
                    ON CONTRACT [//sharpsql/v1/execution/contract]
                    WITH ENCRYPTION = OFF;

                DECLARE @requestBody VARBINARY(MAX) = 0x;
                SEND ON CONVERSATION @initiatorHandle
                    MESSAGE TYPE [//sharpsql/v1/execution/request] (@requestBody);

                DECLARE @targetHandle UNIQUEIDENTIFIER;
                WAITFOR (
                    RECEIVE TOP (1) @targetHandle = [conversation_handle]
                    FROM [SharpSql].[WorkerQueue]
                ), TIMEOUT 10000;
                IF @targetHandle IS NULL THROW 51998, 'The worker queue did not receive the probe request.', 1;
                END CONVERSATION @targetHandle;
                """;
            await setup.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        await using (var dispatch = connection.CreateCommand())
        {
            dispatch.CommandText = "EXEC [SharpSql].[DispatchWorker];";
            dispatch.CommandTimeout = 5;
            await dispatch.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        await using var assertion = connection.CreateCommand();
        assertion.CommandText = "SELECT COUNT(*) FROM [SharpSql].[WorkerQueue];";
        Assert.Equal(0, Convert.ToInt32(
            await assertion.ExecuteScalarAsync(TestContext.Current.CancellationToken),
            System.Globalization.CultureInfo.InvariantCulture));
    }

    private async Task<SqlConnection> OpenBrokerDatabaseAsync(string databaseName = "SharpSqlBrokerTests")
    {
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

    private string BrokerConnectionString() => new SqlConnectionStringBuilder(sqlServer.ConnectionString)
    {
        InitialCatalog = "SharpSqlBrokerTests"
    }.ConnectionString;

    private static async Task InsertExecutionAsync(SqlConnection connection, Guid executionId)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO [SharpSql].[Executions] ([ExecutionId]) VALUES (@executionId);";
        command.Parameters.AddWithValue("@executionId", executionId);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<long> ScheduleTaskAsync(
        SqlConnection connection,
        Guid executionId,
        string programId,
        string handlerName,
        bool startSuspended = false)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DECLARE @taskId BIGINT;
            EXEC [SharpSql].[ScheduleTask]
                @ExecutionId = @executionId,
                @ProgramId = @programId,
                @HandlerName = @handlerName,
                @PayloadJson = N'{}',
                @StartSuspended = @startSuspended,
                @TaskId = @taskId OUTPUT;
            """;
        command.Parameters.AddWithValue("@executionId", executionId);
        command.Parameters.AddWithValue("@programId", programId);
        command.Parameters.AddWithValue("@handlerName", handlerName);
        command.Parameters.AddWithValue("@startSuspended", startSuspended);
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(TestContext.Current.CancellationToken),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task CompleteTaskAsync(SqlConnection connection, Guid executionId, long taskId)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            EXEC [SharpSql].[CompleteTask]
                @ExecutionId = @executionId,
                @TaskId = @taskId,
                @State = 4;
            """;
        command.Parameters.AddWithValue("@executionId", executionId);
        command.Parameters.AddWithValue("@taskId", taskId);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<int> ClaimDueContinuationsAsync(SqlConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "EXEC [SharpSql].[ClaimDueContinuations] @BatchSize = 100;";
        var count = 0;
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
            if (reader.GetBoolean(3))
                count++;
        return count;
    }

    private static async Task<TaskRequest?> ReceiveTaskRequestAsync(
        SqlConnection connection,
        Guid executionId,
        int timeoutMilliseconds)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);
        do
        {
            var remaining = Math.Max(1, (int)(deadline - DateTime.UtcNow).TotalMilliseconds);
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                DECLARE @messageType NVARCHAR(256);
                DECLARE @messageBody VARBINARY(MAX);
                WAITFOR (
                    RECEIVE TOP (1)
                        @messageType = [message_type_name],
                        @messageBody = [message_body]
                    FROM [SharpSql].[WorkerQueue]
                ), TIMEOUT {remaining};
                IF @messageType = N'//sharpsql/v1/execution/request'
                    SELECT CONVERT(NVARCHAR(MAX), @messageBody);
                """;
            var value = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
            if (value is not string json)
                return null;

            using var payload = JsonDocument.Parse(json);
            var root = payload.RootElement;
            if (Guid.Parse(root.GetProperty("executionId").GetString()!) == executionId)
            {
                return new TaskRequest(
                    root.GetProperty("taskId").GetInt64(),
                    root.GetProperty("programId").GetString()!,
                    root.GetProperty("handlerName").GetString()!,
                    root.GetProperty("continuationState").GetInt32());
            }
        }
        while (DateTime.UtcNow < deadline);

        return null;
    }

    private static async Task DeleteExecutionAsync(SqlConnection connection, Guid executionId)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM [SharpSql].[Executions] WHERE [ExecutionId] = @executionId;";
        command.Parameters.AddWithValue("@executionId", executionId);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private readonly record struct TaskRequest(
        long TaskId,
        string ProgramId,
        string HandlerName,
        int ContinuationState);

    private static async Task AssertCompletionAsync(
        SqlConnection connection,
        byte state,
        int? errorNumber,
        string? errorMessage)
    {
        var executionId = Guid.NewGuid();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO [SharpSql].[Executions] ([ExecutionId]) VALUES (@executionId);

            DECLARE @initiatorHandle UNIQUEIDENTIFIER;
            BEGIN DIALOG CONVERSATION @initiatorHandle
                FROM SERVICE [//sharpsql/v1/launcher]
                TO SERVICE N'//sharpsql/v1/worker'
                ON CONTRACT [//sharpsql/v1/execution/contract]
                WITH ENCRYPTION = OFF;

            DECLARE @requestBody VARBINARY(MAX) = 0x;
            SEND ON CONVERSATION @initiatorHandle
                MESSAGE TYPE [//sharpsql/v1/execution/request] (@requestBody);

            DECLARE @targetHandle UNIQUEIDENTIFIER;
            WAITFOR (
                RECEIVE TOP (1) @targetHandle = [conversation_handle]
                FROM [SharpSql].[WorkerQueue]
            ), TIMEOUT 10000;
            IF @targetHandle IS NULL THROW 51998, 'The worker queue did not receive the request.', 1;

            UPDATE [SharpSql].[Executions]
            SET [ConversationHandle] = @targetHandle
            WHERE [ExecutionId] = @executionId;

            DECLARE @firstCompletion TABLE ([Completed] BIT NOT NULL);
            INSERT INTO @firstCompletion ([Completed])
                EXEC [SharpSql].[CompleteExecution]
                    @ExecutionId = @executionId,
                    @State = @state,
                    @ErrorNumber = @errorNumber,
                    @ErrorMessage = @errorMessage;

            DECLARE @secondCompletion TABLE ([Completed] BIT NOT NULL);
            INSERT INTO @secondCompletion ([Completed])
                EXEC [SharpSql].[CompleteExecution]
                    @ExecutionId = @executionId,
                    @State = @state,
                    @ErrorNumber = @errorNumber,
                    @ErrorMessage = @errorMessage;

            DECLARE @completionType NVARCHAR(256);
            DECLARE @completionBody VARBINARY(MAX);
            WAITFOR (
                RECEIVE TOP (1)
                    @completionType = [message_type_name],
                    @completionBody = [message_body]
                FROM [SharpSql].[LauncherQueue]
            ), TIMEOUT 10000;

            DECLARE @endType NVARCHAR(256);
            WAITFOR (
                RECEIVE TOP (1) @endType = [message_type_name]
                FROM [SharpSql].[LauncherQueue]
            ), TIMEOUT 10000;
            END CONVERSATION @initiatorHandle;

            SELECT
                CONVERT(NVARCHAR(MAX), @completionBody) AS [CompletionBody],
                @completionType AS [CompletionType],
                @endType AS [EndType],
                (SELECT [Completed] FROM @firstCompletion) AS [FirstCompleted],
                (SELECT [Completed] FROM @secondCompletion) AS [SecondCompleted],
                [State],
                [ErrorNumber],
                [ErrorMessage]
            FROM [SharpSql].[Executions]
            WHERE [ExecutionId] = @executionId;
            """;
        command.Parameters.Add("@executionId", SqlDbType.UniqueIdentifier).Value = executionId;
        command.Parameters.Add("@state", SqlDbType.TinyInt).Value = state;
        command.Parameters.Add("@errorNumber", SqlDbType.Int).Value = errorNumber ?? (object)DBNull.Value;
        command.Parameters.Add("@errorMessage", SqlDbType.NVarChar, -1).Value =
            errorMessage ?? (object)DBNull.Value;

        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
        using var payload = JsonDocument.Parse(reader.GetString(0));
        var root = payload.RootElement;
        Assert.Equal(executionId, Guid.Parse(root.GetProperty("executionId").GetString()!));
        Assert.Equal(state, root.GetProperty("state").GetByte());
        Assert.Equal("//sharpsql/v1/execution/completed", reader.GetString(1));
        Assert.Equal("http://schemas.microsoft.com/SQL/ServiceBroker/EndDialog", reader.GetString(2));
        Assert.True(reader.GetBoolean(3));
        Assert.False(reader.GetBoolean(4));
        Assert.Equal(state, reader.GetByte(5));
        if (errorNumber is null)
        {
            Assert.Equal(JsonValueKind.Null, root.GetProperty("errorNumber").ValueKind);
            Assert.Equal(JsonValueKind.Null, root.GetProperty("errorMessage").ValueKind);
            Assert.True(reader.IsDBNull(6));
            Assert.True(reader.IsDBNull(7));
        }
        else
        {
            Assert.Equal(errorNumber, root.GetProperty("errorNumber").GetInt32());
            Assert.Equal(errorMessage, root.GetProperty("errorMessage").GetString());
            Assert.Equal(errorNumber, reader.GetInt32(6));
            Assert.Equal(errorMessage, reader.GetString(7));
        }
    }

    private static async Task ExecuteAsync(SqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 60;
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }
}
