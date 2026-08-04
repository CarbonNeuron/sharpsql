namespace SharpSql;

internal static partial class ExecutionInfrastructureSqlEmitter
{
    internal const string RuntimeManifestTableName = "RuntimeManifest";
    internal const string ServiceBrokerRuntimeName = "ServiceBroker";
    internal const int MinimumSupportedSchemaVersion = 1;
    internal const int CurrentSchemaVersion = 3;
    internal const string ProgramBytecodeImagesTableName = "ServiceBrokerProgramBytecodeImages";
    internal const string BytecodeActivationsTableName = "BytecodeActivations";
    internal const int UnsupportedSchemaVersionErrorNumber = 51916;

    private static void EmitRuntimeManifest(SqlWriter sql)
    {
        sql.Line($"IF OBJECT_ID(N'[{SchemaName}].[{RuntimeManifestTableName}]', N'U') IS NULL");
        sql.Line("BEGIN");
        using (sql.Indent())
        {
            sql.Line($"CREATE TABLE [{SchemaName}].[{RuntimeManifestTableName}] (");
            using (sql.Indent())
            {
                sql.Line("[RuntimeName] NVARCHAR(32) NOT NULL,");
                sql.Line("[SchemaVersion] INT NOT NULL,");
                sql.Line("[InstalledAtUtc] DATETIME2(7) NOT NULL CONSTRAINT [DF_sharpsql_RuntimeManifest_InstalledAtUtc] DEFAULT (SYSUTCDATETIME()),");
                sql.Line("[UpdatedAtUtc] DATETIME2(7) NOT NULL CONSTRAINT [DF_sharpsql_RuntimeManifest_UpdatedAtUtc] DEFAULT (SYSUTCDATETIME()),");
                sql.Line("CONSTRAINT [PK_sharpsql_RuntimeManifest] PRIMARY KEY ([RuntimeName]),");
                sql.Line("CONSTRAINT [CK_sharpsql_RuntimeManifest_SchemaVersion] CHECK ([SchemaVersion] > 0)");
            }
            sql.Line(");");
        }
        sql.Line("END;");
        sql.Line();
        sql.Line($"IF NOT EXISTS (SELECT 1 FROM [{SchemaName}].[{RuntimeManifestTableName}] WITH (UPDLOCK, HOLDLOCK) WHERE [RuntimeName] = N'{ServiceBrokerRuntimeName}')");
        using (sql.Indent())
        {
            // Version 1 is also the bootstrap version for installations created before
            // the manifest existed. Its migration is deliberately safe for an empty schema.
            sql.Line($"INSERT INTO [{SchemaName}].[{RuntimeManifestTableName}] ([RuntimeName], [SchemaVersion]) VALUES (N'{ServiceBrokerRuntimeName}', {MinimumSupportedSchemaVersion});");
        }
        sql.Line();
        sql.Line("DECLARE @__sharpsql_schema_version INT;");
        sql.Line($"SELECT @__sharpsql_schema_version = [SchemaVersion] FROM [{SchemaName}].[{RuntimeManifestTableName}] WITH (UPDLOCK, HOLDLOCK) WHERE [RuntimeName] = N'{ServiceBrokerRuntimeName}';");
        sql.Line();
        sql.Line($"IF @__sharpsql_schema_version < {MinimumSupportedSchemaVersion}");
        using (sql.Indent())
            sql.Line($"THROW {UnsupportedSchemaVersionErrorNumber}, 'The installed SharpSql Service Broker schema is too old to upgrade.', 1;");
        sql.Line($"IF @__sharpsql_schema_version > {CurrentSchemaVersion}");
        using (sql.Indent())
            sql.Line($"THROW {UnsupportedSchemaVersionErrorNumber}, 'The installed SharpSql Service Broker schema is newer than this SharpSql runtime.', 1;");
        sql.Line();
    }

    private static void EmitRuntimeMigrations(SqlWriter sql)
    {
        EmitVersionOneToTwoMigration(sql);
        sql.Line();
        EmitVersionTwoToThreeMigration(sql);
        sql.Line();
        sql.Line($"IF @__sharpsql_schema_version <> {CurrentSchemaVersion}");
        using (sql.Indent())
            sql.Line($"THROW {UnsupportedSchemaVersionErrorNumber}, 'No supported SharpSql Service Broker schema upgrade path was found.', 1;");
    }

    private static void EmitVersionOneToTwoMigration(SqlWriter sql)
    {
        sql.Line("IF @__sharpsql_schema_version = 1");
        sql.Line("BEGIN");
        using (sql.Indent())
        {
            sql.Line($"IF OBJECT_ID(N'[{SchemaName}].[{OutputSequenceName}]', N'SO') IS NULL");
            sql.Line("BEGIN");
            using (sql.Indent())
            {
                sql.Line("DECLARE @__sharpsql_next_output_sequence BIGINT = 1;");
                sql.Line($"SELECT @__sharpsql_next_output_sequence = ISNULL(MAX([SequenceNumber]), 0) + 1 FROM [{SchemaName}].[{OutputEventsTableName}];");
                sql.Line($"DECLARE @__sharpsql_create_output_sequence_sql NVARCHAR(MAX) = N'CREATE SEQUENCE [{SchemaName}].[{OutputSequenceName}] AS BIGINT START WITH ' + CONVERT(NVARCHAR(20), @__sharpsql_next_output_sequence) + N' INCREMENT BY 1 CACHE 1000;';");
                sql.Line("EXEC(@__sharpsql_create_output_sequence_sql);");
            }
            sql.Line("END;");
            sql.Line();
            sql.Line($"UPDATE [{SchemaName}].[{RuntimeManifestTableName}]");
            sql.Line($"SET [SchemaVersion] = 2, [UpdatedAtUtc] = SYSUTCDATETIME() WHERE [RuntimeName] = N'{ServiceBrokerRuntimeName}';");
            sql.Line("SET @__sharpsql_schema_version = 2;");
        }
        sql.Line("END;");
    }

    private static void EmitVersionTwoToThreeMigration(SqlWriter sql)
    {
        sql.Line("IF @__sharpsql_schema_version = 2");
        sql.Line("BEGIN");
        using (sql.Indent())
        {
            EmitBytecodeResumptionInfrastructure(sql);
            sql.Line();
            sql.Line($"UPDATE [{SchemaName}].[{RuntimeManifestTableName}]");
            sql.Line($"SET [SchemaVersion] = 3, [UpdatedAtUtc] = SYSUTCDATETIME() WHERE [RuntimeName] = N'{ServiceBrokerRuntimeName}';");
            sql.Line("SET @__sharpsql_schema_version = 3;");
        }
        sql.Line("END;");
    }

    private static void EmitBytecodeResumptionInfrastructure(SqlWriter sql)
    {
        sql.Line($"IF OBJECT_ID(N'[{SchemaName}].[{ProgramBytecodeImagesTableName}]', N'U') IS NULL");
        sql.Line("BEGIN");
        using (sql.Indent())
        {
            sql.Line($"CREATE TABLE [{SchemaName}].[{ProgramBytecodeImagesTableName}] (");
            using (sql.Indent())
            {
                sql.Line("[ProgramId] NVARCHAR(32) NOT NULL,");
                sql.Line("[BytecodeImageId] BINARY(32) NOT NULL,");
                sql.Line("[LinkedAtUtc] DATETIME2(7) NOT NULL CONSTRAINT [DF_sharpsql_ServiceBrokerProgramBytecodeImages_LinkedAtUtc] DEFAULT (SYSUTCDATETIME()),");
                sql.Line("CONSTRAINT [PK_sharpsql_ServiceBrokerProgramBytecodeImages] PRIMARY KEY ([ProgramId], [BytecodeImageId]),");
                sql.Line("CONSTRAINT [FK_sharpsql_ServiceBrokerProgramBytecodeImages_Programs] FOREIGN KEY ([ProgramId])");
                using (sql.Indent())
                    sql.Line($"REFERENCES [{SchemaName}].[{ProgramCatalogTableName}] ([ProgramId]),");
                sql.Line("CONSTRAINT [FK_sharpsql_ServiceBrokerProgramBytecodeImages_Images] FOREIGN KEY ([BytecodeImageId])");
                using (sql.Indent())
                    sql.Line($"REFERENCES {RegisterBytecodeRuntimeSqlEmitter.ImagesTable} ([__image_id])");
            }
            sql.Line(");");
        }
        sql.Line("END;");
        sql.Line();
        sql.Line($"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'[{SchemaName}].[{ProgramBytecodeImagesTableName}]') AND [name] = N'IX_sharpsql_ServiceBrokerProgramBytecodeImages_Image')");
        using (sql.Indent())
            sql.Line($"CREATE INDEX [IX_sharpsql_ServiceBrokerProgramBytecodeImages_Image] ON [{SchemaName}].[{ProgramBytecodeImagesTableName}] ([BytecodeImageId], [ProgramId]);");
        sql.Line();
        sql.Line($"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'{RegisterBytecodeRuntimeSqlEmitter.FramesTable}') AND [name] = N'UX_sharpsql_BytecodeFramesV1_ExecutionFrameImage')");
        using (sql.Indent())
            sql.Line($"CREATE UNIQUE INDEX [UX_sharpsql_BytecodeFramesV1_ExecutionFrameImage] ON {RegisterBytecodeRuntimeSqlEmitter.FramesTable} ([__execution_id], [__id], [__image_id]);");
        sql.Line();
        sql.Line($"IF OBJECT_ID(N'[{SchemaName}].[{BytecodeActivationsTableName}]', N'U') IS NULL");
        sql.Line("BEGIN");
        using (sql.Indent())
        {
            sql.Line($"CREATE TABLE [{SchemaName}].[{BytecodeActivationsTableName}] (");
            using (sql.Indent())
            {
                sql.Line("[ExecutionId] UNIQUEIDENTIFIER NOT NULL,");
                sql.Line("[TaskId] BIGINT NOT NULL,");
                sql.Line("[ProgramId] NVARCHAR(32) NOT NULL,");
                sql.Line("[BytecodeImageId] BINARY(32) NOT NULL,");
                sql.Line("[CurrentFrameId] BIGINT NOT NULL,");
                sql.Line("[SuspensionGeneration] INT NOT NULL,");
                sql.Line("[CreatedAtUtc] DATETIME2(7) NOT NULL CONSTRAINT [DF_sharpsql_BytecodeActivations_CreatedAtUtc] DEFAULT (SYSUTCDATETIME()),");
                sql.Line("[UpdatedAtUtc] DATETIME2(7) NOT NULL CONSTRAINT [DF_sharpsql_BytecodeActivations_UpdatedAtUtc] DEFAULT (SYSUTCDATETIME()),");
                sql.Line("CONSTRAINT [PK_sharpsql_BytecodeActivations] PRIMARY KEY ([ExecutionId], [TaskId]),");
                sql.Line("CONSTRAINT [FK_sharpsql_BytecodeActivations_Tasks] FOREIGN KEY ([ExecutionId], [TaskId])");
                using (sql.Indent())
                    sql.Line($"REFERENCES [{SchemaName}].[{TasksTableName}] ([ExecutionId], [TaskId]) ON DELETE CASCADE,");
                sql.Line("CONSTRAINT [FK_sharpsql_BytecodeActivations_ProgramImages] FOREIGN KEY ([ProgramId], [BytecodeImageId])");
                using (sql.Indent())
                    sql.Line($"REFERENCES [{SchemaName}].[{ProgramBytecodeImagesTableName}] ([ProgramId], [BytecodeImageId]),");
                sql.Line("CONSTRAINT [FK_sharpsql_BytecodeActivations_CurrentFrame] FOREIGN KEY ([ExecutionId], [CurrentFrameId], [BytecodeImageId])");
                using (sql.Indent())
                    sql.Line($"REFERENCES {RegisterBytecodeRuntimeSqlEmitter.FramesTable} ([__execution_id], [__id], [__image_id]),");
                sql.Line("CONSTRAINT [CK_sharpsql_BytecodeActivations_CurrentFrame] CHECK ([CurrentFrameId] > 0),");
                sql.Line("CONSTRAINT [CK_sharpsql_BytecodeActivations_SuspensionGeneration] CHECK ([SuspensionGeneration] >= 0)");
            }
            sql.Line(");");
        }
        sql.Line("END;");
        sql.Line();
        sql.Line($"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'[{SchemaName}].[{BytecodeActivationsTableName}]') AND [name] = N'IX_sharpsql_BytecodeActivations_ProgramImage')");
        using (sql.Indent())
            sql.Line($"CREATE INDEX [IX_sharpsql_BytecodeActivations_ProgramImage] ON [{SchemaName}].[{BytecodeActivationsTableName}] ([ProgramId], [BytecodeImageId], [ExecutionId], [TaskId]);");
        sql.Line();
        sql.Line($"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'[{SchemaName}].[{BytecodeActivationsTableName}]') AND [name] = N'IX_sharpsql_BytecodeActivations_Image')");
        using (sql.Indent())
            sql.Line($"CREATE INDEX [IX_sharpsql_BytecodeActivations_Image] ON [{SchemaName}].[{BytecodeActivationsTableName}] ([BytecodeImageId], [ExecutionId], [TaskId]);");
    }
}
