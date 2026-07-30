using Xunit;

namespace SharpSql.Tests;

public sealed class ExecutionInfrastructureVersioningTests
{
    [Fact]
    public void ProvisionsAndLocksADurableServiceBrokerSchemaManifest()
    {
        var sql = ExecutionInfrastructureSqlEmitter.Emit();

        Assert.Equal(2, ExecutionInfrastructureSqlEmitter.CurrentSchemaVersion);
        Assert.Equal(1, ExecutionInfrastructureSqlEmitter.MinimumSupportedSchemaVersion);
        Assert.Contains("CREATE TABLE [SharpSql].[RuntimeManifest]", sql);
        Assert.Contains("[RuntimeName] NVARCHAR(32) NOT NULL", sql);
        Assert.Contains("[SchemaVersion] INT NOT NULL", sql);
        Assert.Contains("WHERE [RuntimeName] = N'ServiceBroker'", sql);
        Assert.Contains("WITH (UPDLOCK, HOLDLOCK)", sql);
        Assert.True(
            sql.IndexOf("sys.sp_getapplock", StringComparison.Ordinal) <
            sql.IndexOf("CREATE TABLE [SharpSql].[RuntimeManifest]", StringComparison.Ordinal));
        Assert.True(
            sql.IndexOf("CREATE TABLE [SharpSql].[RuntimeManifest]", StringComparison.Ordinal) <
            sql.IndexOf("COMMIT TRANSACTION;", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidatesVersionsAndMigratesLegacyOutputWithoutReusingASequenceNumber()
    {
        var sql = ExecutionInfrastructureSqlEmitter.Emit();

        Assert.Equal(51916, ExecutionInfrastructureSqlEmitter.UnsupportedSchemaVersionErrorNumber);
        Assert.Contains("IF @__sharpsql_schema_version < 1", sql);
        Assert.Contains("IF @__sharpsql_schema_version > 2", sql);
        Assert.Contains("IF @__sharpsql_schema_version = 1", sql);
        Assert.Contains("ISNULL(MAX([SequenceNumber]), 0) + 1", sql);
        Assert.Contains("CREATE SEQUENCE [SharpSql].[OutputSequence] AS BIGINT START WITH ' +", sql);
        Assert.Contains("EXEC(@__sharpsql_create_output_sequence_sql);", sql);
        Assert.Contains("SET [SchemaVersion] = 2", sql);
        Assert.Contains("IF @__sharpsql_schema_version <> 2", sql);
        Assert.True(
            sql.IndexOf("ISNULL(MAX([SequenceNumber]), 0) + 1", StringComparison.Ordinal) <
            sql.IndexOf("CREATE OR ALTER PROCEDURE [SharpSql].[AppendOutput]", StringComparison.Ordinal));
    }
}
