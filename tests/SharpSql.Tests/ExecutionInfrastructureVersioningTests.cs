using Xunit;

namespace SharpSql.Tests;

public sealed class ExecutionInfrastructureVersioningTests
{
    [Fact]
    public void ProvisionsAndLocksADurableServiceBrokerSchemaManifest()
    {
        var sql = ExecutionInfrastructureSqlEmitter.Emit();

        Assert.Equal(3, ExecutionInfrastructureSqlEmitter.CurrentSchemaVersion);
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
        Assert.Contains("IF @__sharpsql_schema_version > 3", sql);
        Assert.Contains("IF @__sharpsql_schema_version = 1", sql);
        Assert.Contains("ISNULL(MAX([SequenceNumber]), 0) + 1", sql);
        Assert.Contains("CREATE SEQUENCE [SharpSql].[OutputSequence] AS BIGINT START WITH ' +", sql);
        Assert.Contains("EXEC(@__sharpsql_create_output_sequence_sql);", sql);
        Assert.Contains("SET [SchemaVersion] = 2", sql);
        Assert.Contains("IF @__sharpsql_schema_version = 2", sql);
        Assert.Contains("SET [SchemaVersion] = 3", sql);
        Assert.Contains("IF @__sharpsql_schema_version <> 3", sql);
        Assert.True(
            sql.IndexOf("ISNULL(MAX([SequenceNumber]), 0) + 1", StringComparison.Ordinal) <
            sql.IndexOf("CREATE OR ALTER PROCEDURE [SharpSql].[AppendOutput]", StringComparison.Ordinal));
    }

    [Fact]
    public void MigratesDurableBytecodeActivationAndProgramImageInfrastructureAtomically()
    {
        var sql = ExecutionInfrastructureSqlEmitter.Emit();

        Assert.Contains("CREATE TABLE [SharpSql].[ServiceBrokerProgramBytecodeImages]", sql);
        Assert.Contains("PRIMARY KEY ([ProgramId], [BytecodeImageId])", sql);
        Assert.Contains("CREATE TABLE [SharpSql].[BytecodeActivations]", sql);
        Assert.Contains("PRIMARY KEY ([ExecutionId], [TaskId])", sql);
        Assert.Contains("FOREIGN KEY ([ProgramId], [BytecodeImageId])", sql);
        Assert.Contains("FOREIGN KEY ([ExecutionId], [CurrentFrameId], [BytecodeImageId])", sql);
        Assert.Contains("REFERENCES [SharpSql].[Tasks] ([ExecutionId], [TaskId]) ON DELETE CASCADE", sql);
        Assert.Contains("IX_sharpsql_ServiceBrokerProgramBytecodeImages_Image", sql);
        Assert.Contains("IX_sharpsql_BytecodeActivations_Image", sql);
        Assert.True(
            sql.IndexOf("CREATE TABLE [SharpSql].[BytecodeImages]", StringComparison.Ordinal) <
            sql.IndexOf("IF @__sharpsql_schema_version = 2", StringComparison.Ordinal));
        Assert.True(
            sql.IndexOf("CREATE TABLE [SharpSql].[ServiceBrokerPrograms]", StringComparison.Ordinal) <
            sql.IndexOf("CREATE TABLE [SharpSql].[ServiceBrokerProgramBytecodeImages]", StringComparison.Ordinal));
        Assert.True(
            sql.IndexOf("CREATE TABLE [SharpSql].[BytecodeActivations]", StringComparison.Ordinal) <
            sql.IndexOf("SET [SchemaVersion] = 3", StringComparison.Ordinal));
    }
}
