using Microsoft.Data.SqlClient;
using Xunit;

namespace SharpSql.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class ServiceBrokerLifecycleIntegrationTests(SqlServerFixture sqlServer)
{
    private const string BytecodeImageId = "0x1111111111111111111111111111111111111111111111111111111111111111";

    [Fact]
    public async Task LeasesCancellationReapingAndProgramCleanupAreCoordinated()
    {
        await using var connection = await OpenBrokerDatabaseAsync();
        await ExecuteAsync(connection, SharpSqlServiceBrokerRuntime.GenerateProvisioningSql());

        const string programId = "0123456789abcdef0123456789abcdef";
        var canceledExecutionId = Guid.NewGuid();
        var abandonedExecutionId = Guid.NewGuid();
        try
        {
            await ExecuteAsync(connection, ProgramProcedureSql(programId));
            await ExecuteLifecycleAssertionsAsync(
                connection,
                programId,
                canceledExecutionId,
                abandonedExecutionId);
        }
        finally
        {
            await using var cleanup = connection.CreateCommand();
            cleanup.CommandText = $"""
                DELETE FROM [SharpSql].[Executions]
                WHERE [ExecutionId] IN (@canceledExecutionId, @abandonedExecutionId);
                DELETE FROM [SharpSql].[ServiceBrokerProgramBytecodeImages] WHERE [ProgramId] = N'{programId}';
                DELETE FROM [SharpSql].[ServiceBrokerPrograms] WHERE [ProgramId] = N'{programId}';
                DELETE FROM [SharpSql].[BytecodeImages] WHERE [__image_id] = {BytecodeImageId};
                DROP PROCEDURE IF EXISTS [SharpSql].[Program_{programId}];
                """;
            cleanup.Parameters.AddWithValue("@canceledExecutionId", canceledExecutionId);
            cleanup.Parameters.AddWithValue("@abandonedExecutionId", abandonedExecutionId);
            await cleanup.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
    }

    private static async Task ExecuteLifecycleAssertionsAsync(
        SqlConnection connection,
        string programId,
        Guid canceledExecutionId,
        Guid abandonedExecutionId)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            DECLARE @leaseId UNIQUEIDENTIFIER;
            DECLARE @renewed BIT;
            DECLARE @taskId BIGINT;
            DECLARE @frameId BIGINT;

            INSERT INTO [SharpSql].[BytecodeImages] (
                [__image_id], [__abi_major], [__abi_minor], [__instruction_count],
                [__argument_count], [__parameter_count], [__installed_at_utc], [__last_used_at_utc])
            VALUES ({BytecodeImageId}, 1, 2, 0, 0, 0, SYSUTCDATETIME(), SYSUTCDATETIME());
            EXEC [SharpSql].[RegisterServiceBrokerProgram]
                @ProgramId = N'{programId}', @BytecodeImageId = {BytecodeImageId};
            EXEC [SharpSql].[StartServiceBrokerExecution]
                @ExecutionId = @canceledExecutionId,
                @ProgramId = N'{programId}',
                @LeaseDurationSeconds = 30,
                @LeaseId = @leaseId OUTPUT;

            EXEC [SharpSql].[HeartbeatExecution]
                @ExecutionId = @canceledExecutionId,
                @LeaseId = '00000000-0000-0000-0000-000000000001',
                @Renewed = @renewed OUTPUT;
            IF @renewed <> 0 THROW 51990, 'A stale lease token renewed an execution.', 1;

            EXEC [SharpSql].[HeartbeatExecution]
                @ExecutionId = @canceledExecutionId,
                @LeaseId = @leaseId,
                @Renewed = @renewed OUTPUT;
            IF @renewed <> 1 THROW 51991, 'The current lease token did not renew its execution.', 1;

            EXEC [SharpSql].[ScheduleTask]
                @ExecutionId = @canceledExecutionId,
                @ProgramId = N'{programId}',
                @HandlerName = N'test',
                @StartSuspended = 1,
                @TaskId = @taskId OUTPUT;
            INSERT INTO [SharpSql].[BytecodeFramesV1] (
                [__execution_id], [__image_id], [__method_id], [__pc], [__return_id], [__caller_id], [__result_destination])
            VALUES (@canceledExecutionId, {BytecodeImageId}, 1, 0, 0, NULL, NULL);
            SET @frameId = SCOPE_IDENTITY();
            INSERT INTO [SharpSql].[BytecodeRegistersV1] ([__execution_id], [__frame_id], [__register_id], [__type], [__value])
            VALUES (@canceledExecutionId, @frameId, 1, 2, 42);
            INSERT INTO [SharpSql].[BytecodeActivations] (
                [ExecutionId], [TaskId], [ProgramId], [BytecodeImageId], [CurrentFrameId], [SuspensionGeneration])
            VALUES (@canceledExecutionId, @taskId, N'{programId}', {BytecodeImageId}, @frameId, 0);
            UPDATE [SharpSql].[ServiceBrokerPrograms]
            SET [LastUsedAtUtc] = DATEADD(DAY, -2, SYSUTCDATETIME())
            WHERE [ProgramId] = N'{programId}';
            EXEC [SharpSql].[CleanupServiceBrokerPrograms] @UnusedForMinutes = 1;
            IF OBJECT_ID(N'[SharpSql].[Program_{programId}]', N'P') IS NULL
                THROW 51992, 'Cleanup dropped a program used by an active execution.', 1;

            EXEC [SharpSql].[CancelExecution]
                @ExecutionId = @canceledExecutionId,
                @Reason = N'integration cancellation';
            IF NOT EXISTS (
                SELECT 1 FROM [SharpSql].[Executions]
                WHERE [ExecutionId] = @canceledExecutionId AND [State] = 4
                    AND [ErrorNumber] = {ExecutionInfrastructureSqlEmitter.ExecutionCanceledErrorNumber}
            ) THROW 51993, 'Cancellation did not transition the execution.', 1;
            IF NOT EXISTS (
                SELECT 1 FROM [SharpSql].[Tasks]
                WHERE [ExecutionId] = @canceledExecutionId AND [TaskId] = @taskId AND [State] = 6
            ) THROW 51994, 'Cancellation did not remain execution-scoped through its task.', 1;
            IF EXISTS (SELECT 1 FROM [SharpSql].[BytecodeActivations] WHERE [ExecutionId] = @canceledExecutionId)
                OR EXISTS (SELECT 1 FROM [SharpSql].[BytecodeRegistersV1] WHERE [__execution_id] = @canceledExecutionId)
                OR EXISTS (SELECT 1 FROM [SharpSql].[BytecodeFramesV1] WHERE [__execution_id] = @canceledExecutionId)
                THROW 51989, 'Cancellation retained mutable bytecode state.', 1;
            IF NOT EXISTS (SELECT 1 FROM [SharpSql].[ServiceBrokerProgramBytecodeImages] WHERE [ProgramId] = N'{programId}' AND [BytecodeImageId] = {BytecodeImageId})
                THROW 51988, 'Cancellation removed the retained program image link.', 1;

            UPDATE [SharpSql].[ServiceBrokerPrograms]
            SET [LastUsedAtUtc] = DATEADD(DAY, -2, SYSUTCDATETIME())
            WHERE [ProgramId] = N'{programId}';
            EXEC [SharpSql].[CleanupServiceBrokerPrograms] @UnusedForMinutes = 1;
            IF OBJECT_ID(N'[SharpSql].[Program_{programId}]', N'P') IS NOT NULL
                THROW 51995, 'Cleanup retained an unused terminal program.', 1;
            IF EXISTS (SELECT 1 FROM [SharpSql].[ServiceBrokerProgramBytecodeImages] WHERE [ProgramId] = N'{programId}')
                THROW 51987, 'Program cleanup retained its bytecode image link.', 1;
            IF NOT EXISTS (SELECT 1 FROM [SharpSql].[BytecodeImages] WHERE [__image_id] = {BytecodeImageId})
                THROW 51986, 'Program cleanup removed a shared immutable bytecode image.', 1;

            EXEC(N'{ProgramProcedureSql(programId).Replace("'", "''", StringComparison.Ordinal)}');
            EXEC [SharpSql].[RegisterServiceBrokerProgram]
                @ProgramId = N'{programId}', @BytecodeImageId = {BytecodeImageId};
            EXEC [SharpSql].[StartServiceBrokerExecution]
                @ExecutionId = @abandonedExecutionId,
                @ProgramId = N'{programId}',
                @LeaseDurationSeconds = 30,
                @LeaseId = @leaseId OUTPUT;
            EXEC [SharpSql].[ScheduleTask]
                @ExecutionId = @abandonedExecutionId,
                @ProgramId = N'{programId}',
                @HandlerName = N'test',
                @StartSuspended = 1,
                @TaskId = @taskId OUTPUT;
            INSERT INTO [SharpSql].[BytecodeFramesV1] (
                [__execution_id], [__image_id], [__method_id], [__pc], [__return_id], [__caller_id], [__result_destination])
            VALUES (@abandonedExecutionId, {BytecodeImageId}, 1, 0, 0, NULL, NULL);
            SET @frameId = SCOPE_IDENTITY();
            INSERT INTO [SharpSql].[BytecodeRegistersV1] ([__execution_id], [__frame_id], [__register_id], [__type], [__value])
            VALUES (@abandonedExecutionId, @frameId, 1, 2, 84);
            INSERT INTO [SharpSql].[BytecodeActivations] (
                [ExecutionId], [TaskId], [ProgramId], [BytecodeImageId], [CurrentFrameId], [SuspensionGeneration])
            VALUES (@abandonedExecutionId, @taskId, N'{programId}', {BytecodeImageId}, @frameId, 0);
            UPDATE [SharpSql].[Executions]
            SET [LeaseExpiresAtUtc] = DATEADD(MINUTE, -1, SYSUTCDATETIME())
            WHERE [ExecutionId] = @abandonedExecutionId;
            EXEC [SharpSql].[ReapAbandonedExecutions] @BatchSize = 10;
            IF NOT EXISTS (
                SELECT 1 FROM [SharpSql].[Executions]
                WHERE [ExecutionId] = @abandonedExecutionId AND [State] = 3
                    AND [ErrorNumber] = {ExecutionInfrastructureSqlEmitter.ExecutionAbandonedErrorNumber}
            ) THROW 51996, 'The expired execution was not reaped.', 1;
            IF NOT EXISTS (
                SELECT 1 FROM [SharpSql].[Tasks]
                WHERE [ExecutionId] = @abandonedExecutionId AND [TaskId] = @taskId AND [State] = 6
            ) THROW 51997, 'Reaping did not cancel the abandoned execution task.', 1;
            IF EXISTS (SELECT 1 FROM [SharpSql].[BytecodeActivations] WHERE [ExecutionId] = @abandonedExecutionId)
                OR EXISTS (SELECT 1 FROM [SharpSql].[BytecodeRegistersV1] WHERE [__execution_id] = @abandonedExecutionId)
                OR EXISTS (SELECT 1 FROM [SharpSql].[BytecodeFramesV1] WHERE [__execution_id] = @abandonedExecutionId)
                THROW 51985, 'Reaping retained mutable bytecode state.', 1;
            IF NOT EXISTS (SELECT 1 FROM [SharpSql].[ServiceBrokerProgramBytecodeImages] WHERE [ProgramId] = N'{programId}' AND [BytecodeImageId] = {BytecodeImageId})
                THROW 51984, 'Reaping removed the retained program image link.', 1;
            """;
        command.Parameters.AddWithValue("@canceledExecutionId", canceledExecutionId);
        command.Parameters.AddWithValue("@abandonedExecutionId", abandonedExecutionId);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static string ProgramProcedureSql(string programId) => $"""
        CREATE OR ALTER PROCEDURE [SharpSql].[Program_{programId}]
            @__sharpsql_execution_id UNIQUEIDENTIFIER,
            @__sharpsql_task_id BIGINT
        AS
        BEGIN
            SET NOCOUNT ON;
        END;
        """;

    private async Task<SqlConnection> OpenBrokerDatabaseAsync()
    {
        const string databaseName = "SharpSqlBrokerLifecycleTests";
        await using (var master = new SqlConnection(sqlServer.ConnectionString))
        {
            await master.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = master.CreateCommand();
            command.CommandText = $"""
                IF DB_ID(N'{databaseName}') IS NULL CREATE DATABASE [{databaseName}];
                IF EXISTS (SELECT 1 FROM sys.databases WHERE [name] = N'{databaseName}' AND [is_broker_enabled] = 0)
                    ALTER DATABASE [{databaseName}] SET ENABLE_BROKER;
                """;
            command.CommandTimeout = 60;
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var connection = new SqlConnection(new SqlConnectionStringBuilder(sqlServer.ConnectionString)
        {
            InitialCatalog = databaseName
        }.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        return connection;
    }

    private static async Task ExecuteAsync(SqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 120;
        try
        {
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
        catch (SqlException exception)
        {
            var lines = sql.Split('\n');
            var firstLine = Math.Max(0, exception.LineNumber - 4);
            var context = string.Join("\n", lines.Skip(firstLine).Take(7));
            throw new InvalidOperationException(
                $"SQL failed at line {exception.LineNumber} in '{exception.Procedure}': {exception.Message}\n{context}",
                exception);
        }
    }
}
