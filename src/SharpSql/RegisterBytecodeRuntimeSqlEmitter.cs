namespace SharpSql;

internal static class RegisterBytecodeRuntimeSqlEmitter
{
    internal const string ImagesTable = "[SharpSql].[BytecodeImages]";
    internal const string InstructionsTable = "[SharpSql].[BytecodeInstructionsV1]";
    internal const string ArgumentsTable = "[SharpSql].[BytecodeArgumentsV1]";
    internal const string ParametersTable = "[SharpSql].[BytecodeParametersV1]";
    internal const string FramesTable = "[SharpSql].[BytecodeFramesV1]";
    internal const string RegistersTable = "[SharpSql].[BytecodeRegistersV1]";
    internal const int SchemaVersion = 1;
    internal const int UnsupportedSchemaVersionErrorNumber = 51937;
    internal const int IncompatibleImageErrorNumber = 51938;
    internal const int ImageLockErrorNumber = 51939;

    internal static void EmitProvisioning(SqlWriter sql)
    {
        EmitManifest(sql);
        sql.Line();
        EmitImages(sql);
        sql.Line();
        EmitInstructions(sql);
        sql.Line();
        EmitArguments(sql);
        sql.Line();
        EmitParameters(sql);
        sql.Line();
        EmitFrames(sql);
        sql.Line();
        EmitRegisters(sql);
    }

    private static void EmitManifest(SqlWriter sql)
    {
        sql.Line("IF OBJECT_ID(N'[SharpSql].[RuntimeManifest]', N'U') IS NULL");
        sql.Line("BEGIN");
        using (sql.Indent())
        {
            sql.Line("CREATE TABLE [SharpSql].[RuntimeManifest] (");
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
        sql.Line("IF NOT EXISTS (SELECT 1 FROM [SharpSql].[RuntimeManifest] WITH (UPDLOCK, HOLDLOCK) WHERE [RuntimeName] = N'RegisterBytecode')");
        using (sql.Indent())
            sql.Line($"INSERT INTO [SharpSql].[RuntimeManifest] ([RuntimeName], [SchemaVersion]) VALUES (N'RegisterBytecode', {SchemaVersion});");
        sql.Line("DECLARE @__sharpsql_bc_schema_version INT;");
        sql.Line("SELECT @__sharpsql_bc_schema_version = [SchemaVersion] FROM [SharpSql].[RuntimeManifest] WITH (UPDLOCK, HOLDLOCK) WHERE [RuntimeName] = N'RegisterBytecode';");
        sql.Line($"IF @__sharpsql_bc_schema_version <> {SchemaVersion}");
        using (sql.Indent())
            sql.Line($"THROW {UnsupportedSchemaVersionErrorNumber}, 'The installed SharpSql register-bytecode schema is not supported by this compiler.', 1;");
    }

    private static void EmitImages(SqlWriter sql)
    {
        sql.Line($"IF OBJECT_ID(N'{ImagesTable}', N'U') IS NULL");
        sql.Line("BEGIN");
        using (sql.Indent())
        {
            sql.Line($"CREATE TABLE {ImagesTable} (");
            using (sql.Indent())
            {
                sql.Line("[__image_id] BINARY(32) NOT NULL,");
                sql.Line("[__abi_major] SMALLINT NOT NULL,");
                sql.Line("[__abi_minor] SMALLINT NOT NULL,");
                sql.Line("[__instruction_count] INT NOT NULL,");
                sql.Line("[__argument_count] INT NOT NULL,");
                sql.Line("[__parameter_count] INT NOT NULL,");
                sql.Line("[__installed_at_utc] DATETIME2(7) NOT NULL,");
                sql.Line("[__last_used_at_utc] DATETIME2(7) NOT NULL,");
                sql.Line("CONSTRAINT [PK_sharpsql_BytecodeImages] PRIMARY KEY ([__image_id])");
            }
            sql.Line(");");
        }
        sql.Line("END;");
    }

    private static void EmitInstructions(SqlWriter sql)
    {
        sql.Line($"IF OBJECT_ID(N'{InstructionsTable}', N'U') IS NULL");
        sql.Line("BEGIN");
        using (sql.Indent())
        {
            sql.Line($"CREATE TABLE {InstructionsTable} (");
            using (sql.Indent())
            {
                sql.Line("[__image_id] BINARY(32) NOT NULL,");
                sql.Line("[__method_id] INT NOT NULL,");
                sql.Line("[__pc] INT NOT NULL,");
                sql.Line("[__opcode] TINYINT NOT NULL,");
                sql.Line("[__destination] INT NULL,");
                sql.Line("[__type] TINYINT NULL,");
                sql.Line("[__operand_a] INT NULL,");
                sql.Line("[__operand_b] INT NULL,");
                sql.Line("[__operation] INT NULL,");
                sql.Line("[__target] INT NULL,");
                sql.Line("[__false_target] INT NULL,");
                sql.Line("[__constant] BIGINT NULL,");
                sql.Line("[__constant_text] NVARCHAR(MAX) NULL,");
                sql.Line("CONSTRAINT [PK_sharpsql_BytecodeInstructionsV1] PRIMARY KEY ([__image_id], [__method_id], [__pc])");
            }
            sql.Line(");");
        }
        sql.Line("END;");
    }

    private static void EmitArguments(SqlWriter sql)
    {
        sql.Line($"IF OBJECT_ID(N'{ArgumentsTable}', N'U') IS NULL");
        sql.Line("BEGIN");
        using (sql.Indent())
        {
            sql.Line($"CREATE TABLE {ArgumentsTable} (");
            using (sql.Indent())
            {
                sql.Line("[__image_id] BINARY(32) NOT NULL,");
                sql.Line("[__method_id] INT NOT NULL,");
                sql.Line("[__pc] INT NOT NULL,");
                sql.Line("[__argument_index] INT NOT NULL,");
                sql.Line("[__register_id] INT NOT NULL,");
                sql.Line("[__type] TINYINT NOT NULL,");
                sql.Line("CONSTRAINT [PK_sharpsql_BytecodeArgumentsV1] PRIMARY KEY ([__image_id], [__method_id], [__pc], [__argument_index])");
            }
            sql.Line(");");
        }
        sql.Line("END;");
    }

    private static void EmitParameters(SqlWriter sql)
    {
        sql.Line($"IF OBJECT_ID(N'{ParametersTable}', N'U') IS NULL");
        sql.Line("BEGIN");
        using (sql.Indent())
        {
            sql.Line($"CREATE TABLE {ParametersTable} (");
            using (sql.Indent())
            {
                sql.Line("[__image_id] BINARY(32) NOT NULL,");
                sql.Line("[__method_id] INT NOT NULL,");
                sql.Line("[__parameter_index] INT NOT NULL,");
                sql.Line("[__register_id] INT NOT NULL,");
                sql.Line("[__type] TINYINT NOT NULL,");
                sql.Line("CONSTRAINT [PK_sharpsql_BytecodeParametersV1] PRIMARY KEY ([__image_id], [__method_id], [__parameter_index])");
            }
            sql.Line(");");
        }
        sql.Line("END;");
    }

    private static void EmitFrames(SqlWriter sql)
    {
        sql.Line($"IF OBJECT_ID(N'{FramesTable}', N'U') IS NULL");
        sql.Line("BEGIN");
        using (sql.Indent())
        {
            sql.Line($"CREATE TABLE {FramesTable} (");
            using (sql.Indent())
            {
                sql.Line("[__execution_id] UNIQUEIDENTIFIER NOT NULL,");
                sql.Line("[__id] BIGINT IDENTITY(1,1) NOT NULL,");
                sql.Line("[__image_id] BINARY(32) NOT NULL,");
                sql.Line("[__method_id] INT NOT NULL,");
                sql.Line("[__pc] INT NOT NULL,");
                sql.Line("[__return_id] INT NOT NULL,");
                sql.Line("[__caller_id] BIGINT NULL,");
                sql.Line("[__result_destination] INT NULL,");
                sql.Line("CONSTRAINT [PK_sharpsql_BytecodeFramesV1] PRIMARY KEY ([__execution_id], [__id])");
            }
            sql.Line(");");
        }
        sql.Line("END;");
    }

    private static void EmitRegisters(SqlWriter sql)
    {
        sql.Line($"IF OBJECT_ID(N'{RegistersTable}', N'U') IS NULL");
        sql.Line("BEGIN");
        using (sql.Indent())
        {
            sql.Line($"CREATE TABLE {RegistersTable} (");
            using (sql.Indent())
            {
                sql.Line("[__execution_id] UNIQUEIDENTIFIER NOT NULL,");
                sql.Line("[__frame_id] BIGINT NOT NULL,");
                sql.Line("[__register_id] INT NOT NULL,");
                sql.Line("[__type] TINYINT NOT NULL,");
                sql.Line("[__value] BIGINT NULL,");
                sql.Line("[__text_value] NVARCHAR(MAX) NULL,");
                sql.Line("CONSTRAINT [PK_sharpsql_BytecodeRegistersV1] PRIMARY KEY ([__execution_id], [__frame_id], [__register_id])");
            }
            sql.Line(");");
        }
        sql.Line("END;");
    }
}
