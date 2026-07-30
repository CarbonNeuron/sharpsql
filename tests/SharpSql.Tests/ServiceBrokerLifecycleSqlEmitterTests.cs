using Xunit;

namespace SharpSql.Tests;

public sealed class ServiceBrokerLifecycleSqlEmitterTests
{
    [Fact]
    public void ProvisioningAddsLeaseCancellationReapingAndProgramRetentionObjects()
    {
        var sql = SharpSqlServiceBrokerRuntime.GenerateProvisioningSql();

        Assert.Contains("ADD [LeaseId] UNIQUEIDENTIFIER NULL", sql);
        Assert.Contains("ADD [LastHeartbeatAtUtc] DATETIME2(7) NULL", sql);
        Assert.Contains("ADD [LeaseExpiresAtUtc] DATETIME2(7) NULL", sql);
        Assert.Contains("CREATE TABLE [SharpSql].[ServiceBrokerPrograms]", sql);
        Assert.Contains("CREATE OR ALTER PROCEDURE [SharpSql].[StartServiceBrokerExecution]", sql);
        Assert.Contains("CREATE OR ALTER PROCEDURE [SharpSql].[HeartbeatExecution]", sql);
        Assert.Contains("CREATE OR ALTER PROCEDURE [SharpSql].[CancelExecution]", sql);
        Assert.Contains("CREATE OR ALTER PROCEDURE [SharpSql].[ReapAbandonedExecutions]", sql);
        Assert.Contains("CREATE OR ALTER PROCEDURE [SharpSql].[CleanupServiceBrokerPrograms]", sql);
        Assert.Contains("CREATE OR ALTER PROCEDURE [SharpSql].[GetServiceBrokerStatus]", sql);
    }

    [Fact]
    public void ReapingAndCancellationAreExecutionScopedAndRecheckTheLease()
    {
        var sql = ExecutionInfrastructureSqlEmitter.EmitLifecycle();

        Assert.Contains("[ExecutionId] = @ExecutionId AND [LeaseId] = @LeaseId AND [State] = 1", sql);
        Assert.Contains("[LeaseExpiresAtUtc] < @AsOfUtc", sql);
        Assert.Contains("WHERE [ExecutionId] = @ExecutionId AND [State] NOT BETWEEN 4 AND 6", sql);
        Assert.Contains("WHERE [ExecutionId] = @ExecutionId AND [State] NOT IN (2, 3)", sql);
        Assert.Contains($"@ErrorNumber = {ExecutionInfrastructureSqlEmitter.ExecutionAbandonedErrorNumber}", sql);
        Assert.Contains("WITH (UPDLOCK, HOLDLOCK)", sql);
    }

    [Fact]
    public void ProgramCleanupUsesCatalogAndProgramLocksAndExcludesLiveReferences()
    {
        var sql = ExecutionInfrastructureSqlEmitter.EmitLifecycle();

        Assert.Contains("SET @LockResource = CONCAT(N''SharpSql.ServiceBroker.Program.'', @ProgramId)", sql);
        Assert.Contains("@Resource = @LockResource", sql);
        Assert.Contains("[execution].[State] NOT IN (2, 3, 4)", sql);
        Assert.Contains("[task].[State] NOT BETWEEN 4 AND 6", sql);
        Assert.Contains("EXEC sys.sp_executesql @DropStatement", sql);
        Assert.Contains("@UnusedForMinutes < 1", sql);
        Assert.Contains("@DryRun = 1", sql);
    }

    [Fact]
    public void GeneratedProgramsRegisterStartAndHeartbeatTheirLease()
    {
        const string source = """
            var values = new List<int> { 1 };
            var tasks = values.Select(Work).ToList();
            await Task.WhenAll(tasks);

            async Task<int> Work(int value)
            {
                await Task.Delay(10);
                return value;
            }
            """;

        var result = new SharpSqlCompiler().Transpile(
            source,
            new TranspileOptions { RuntimeStorage = RuntimeStorageKind.ServiceBroker });

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Contains("EXEC [SharpSql].[RegisterServiceBrokerProgram]", result.Sql);
        Assert.Contains("EXEC [SharpSql].[StartServiceBrokerExecution]", result.Sql);
        Assert.Contains("EXEC [SharpSql].[HeartbeatExecution]", result.Sql);
        Assert.Contains("@LeaseId = @__sharpsql_lease_id OUTPUT", result.Sql);
        Assert.Contains("SharpSql.ServiceBroker.Program.", result.Sql);
        Assert.DoesNotContain("[SequenceNumber] > @__sharpsql_output_sequence", result.Sql);
        Assert.Contains("DECLARE @__sharpsql_drained_output TABLE", result.Sql);
        Assert.Contains("NOT EXISTS (SELECT 1 FROM @__sharpsql_drained_output", result.Sql);
        Assert.Contains("WHILE @__sharpsql_terminal_drain_pass < 3", result.Sql);
    }
}
