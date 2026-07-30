namespace SharpSql;

/// <summary>Emits the fixed memory-optimized table types used by the legacy VM.</summary>
internal static class MemoryOptimizedRuntimeSqlEmitter
{
    internal const int MissingFilegroupErrorNumber = 51921;

    internal static string Emit()
    {
        var sql = new SqlWriter();
        sql.Line("SET ANSI_NULLS ON;");
        sql.Line("SET ANSI_PADDING ON;");
        sql.Line("SET ANSI_WARNINGS ON;");
        sql.Line("SET ARITHABORT ON;");
        sql.Line("SET CONCAT_NULL_YIELDS_NULL ON;");
        sql.Line("SET QUOTED_IDENTIFIER ON;");
        sql.Line("SET NUMERIC_ROUNDABORT OFF;");
        sql.Line();
        sql.Line("IF NOT EXISTS (SELECT 1 FROM sys.filegroups WHERE [type] = N'FX')");
        using (sql.Indent())
        {
            sql.Line(
                $"THROW {MissingFilegroupErrorNumber}, 'The database needs a MEMORY_OPTIMIZED_DATA filegroup before provisioning SharpSql memory-optimized runtime types.', 1;");
        }
        sql.Line();
        sql.Line("IF SCHEMA_ID(N'SharpSql') IS NULL");
        using (sql.Indent())
            sql.Line("EXEC(N'CREATE SCHEMA [SharpSql] AUTHORIZATION [dbo];');");
        sql.Line();
        sql.Line("IF TYPE_ID(N'SharpSql.MemoryVmStackV1') IS NULL");
        using (sql.Indent())
        {
            sql.Line("EXEC(N'CREATE TYPE [SharpSql].[MemoryVmStackV1] AS TABLE (");
            using (sql.Indent())
            {
                sql.Line("[__id] INT IDENTITY(1,1) NOT NULL,");
                sql.Line("[__function_id] INT NOT NULL,");
                sql.Line("[__return_id] INT NOT NULL,");
                sql.Line("[__caller_id] INT NULL,");
                sql.Line("PRIMARY KEY NONCLUSTERED ([__id])");
            }
            sql.Line(") WITH (MEMORY_OPTIMIZED = ON);');");
        }
        sql.Line();
        sql.Line("IF TYPE_ID(N'SharpSql.MemoryVmSlotsV1') IS NULL");
        using (sql.Indent())
        {
            sql.Line("EXEC(N'CREATE TYPE [SharpSql].[MemoryVmSlotsV1] AS TABLE (");
            using (sql.Indent())
            {
                sql.Line("[__frame_id] INT NOT NULL,");
                sql.Line("[__slot_id] INT NOT NULL,");
                sql.Line("[__scalar_value] VARBINARY(8000) NULL,");
                sql.Line("[__text_value] NVARCHAR(MAX) NULL,");
                sql.Line("[__binary_value] VARBINARY(MAX) NULL,");
                sql.Line("PRIMARY KEY NONCLUSTERED ([__frame_id], [__slot_id])");
            }
            sql.Line(") WITH (MEMORY_OPTIMIZED = ON);');");
        }
        return sql.ToString();
    }
}
