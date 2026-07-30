namespace SharpSql;

internal static partial class ExecutionInfrastructureSqlEmitter
{
    internal const string RuntimeManifestTableName = "RuntimeManifest";
    internal const string ServiceBrokerRuntimeName = "ServiceBroker";
    internal const int MinimumSupportedSchemaVersion = 1;
    internal const int CurrentSchemaVersion = 2;
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
}
