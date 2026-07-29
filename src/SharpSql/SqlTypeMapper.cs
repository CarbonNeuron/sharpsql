namespace SharpSql;

/// <summary>
/// SQL Server backend mapping for source-neutral IR types.
/// </summary>
internal static class SqlTypeMapper
{
    public static string Map(IrType type)
    {
        type = type.ScalarRepresentation ?? type;
        return type.Name switch
        {
            "bool" => "BIT",
            "byte" => "TINYINT",
            "sbyte" or "short" => "SMALLINT",
            "ushort" or "int" => "INT",
            "uint" or "long" => "BIGINT",
            "ulong" => "DECIMAL(20,0)",
            "float" => "REAL",
            "double" => "FLOAT",
            "decimal" => "DECIMAL(38,18)",
            "char" => "NCHAR(1)",
            "string" => "NVARCHAR(MAX)",
            "DateTime" => "DATETIME2",
            "DateOnly" => "DATE",
            "TimeOnly" => "TIME",
            "Guid" => "UNIQUEIDENTIFIER",
            "byte[]" => "VARBINARY(MAX)",
            "void" => string.Empty,
            "unknown" => "SQL_VARIANT",
            _ when type.IsReference => "INT",
            _ => "SQL_VARIANT"
        };
    }

    public static string SqlType(this IrType type) => Map(type);
}
