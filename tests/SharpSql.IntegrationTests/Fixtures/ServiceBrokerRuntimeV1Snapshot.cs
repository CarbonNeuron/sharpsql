namespace SharpSql.IntegrationTests.Fixtures;

internal static class ServiceBrokerRuntimeV1Snapshot
{
    internal static readonly Guid ExecutionId = Guid.Parse("10000000-0000-0000-0000-000000000001");

    // This is the durable table shape immediately before RuntimeManifest and the
    // global OutputSequence were introduced. The retained row makes the upgrade
    // prove that sequence allocation advances beyond persisted v1 output.
    internal const string Sql = """
        EXEC(N'CREATE SCHEMA [SharpSql] AUTHORIZATION [dbo];');

        CREATE TABLE [SharpSql].[Executions] (
            [ExecutionId] UNIQUEIDENTIFIER NOT NULL,
            [ConversationHandle] UNIQUEIDENTIFIER NULL,
            [State] TINYINT NOT NULL CONSTRAINT [DF_sharpsql_Executions_State] DEFAULT (0),
            [NextOutputSequence] BIGINT NOT NULL CONSTRAINT [DF_sharpsql_Executions_NextOutputSequence] DEFAULT (1),
            [NextTaskId] BIGINT NOT NULL CONSTRAINT [DF_sharpsql_Executions_NextTaskId] DEFAULT (1),
            [CreatedAtUtc] DATETIME2(7) NOT NULL CONSTRAINT [DF_sharpsql_Executions_CreatedAtUtc] DEFAULT (SYSUTCDATETIME()),
            [StartedAtUtc] DATETIME2(7) NULL,
            [CompletedAtUtc] DATETIME2(7) NULL,
            [ErrorNumber] INT NULL,
            [ErrorMessage] NVARCHAR(MAX) NULL,
            CONSTRAINT [PK_sharpsql_Executions] PRIMARY KEY ([ExecutionId]),
            CONSTRAINT [CK_sharpsql_Executions_State] CHECK ([State] BETWEEN 0 AND 4),
            CONSTRAINT [CK_sharpsql_Executions_NextOutputSequence] CHECK ([NextOutputSequence] > 0),
            CONSTRAINT [CK_sharpsql_Executions_NextTaskId] CHECK ([NextTaskId] > 0)
        );

        CREATE INDEX [IX_sharpsql_Executions_ConversationHandle]
            ON [SharpSql].[Executions] ([ConversationHandle]);

        CREATE TABLE [SharpSql].[OutputEvents] (
            [ExecutionId] UNIQUEIDENTIFIER NOT NULL,
            [SequenceNumber] BIGINT NOT NULL,
            [OutputText] NVARCHAR(MAX) NOT NULL,
            [CreatedAtUtc] DATETIME2(7) NOT NULL CONSTRAINT [DF_sharpsql_OutputEvents_CreatedAtUtc] DEFAULT (SYSUTCDATETIME()),
            CONSTRAINT [PK_sharpsql_OutputEvents] PRIMARY KEY CLUSTERED ([ExecutionId], [SequenceNumber]),
            CONSTRAINT [FK_sharpsql_OutputEvents_Executions] FOREIGN KEY ([ExecutionId])
                REFERENCES [SharpSql].[Executions] ([ExecutionId]) ON DELETE CASCADE,
            CONSTRAINT [CK_sharpsql_OutputEvents_SequenceNumber] CHECK ([SequenceNumber] > 0)
        );

        INSERT INTO [SharpSql].[Executions] ([ExecutionId], [NextOutputSequence])
        VALUES ('10000000-0000-0000-0000-000000000001', 42);

        INSERT INTO [SharpSql].[OutputEvents] ([ExecutionId], [SequenceNumber], [OutputText])
        VALUES ('10000000-0000-0000-0000-000000000001', 41, N'v1-output');
        """;
}
