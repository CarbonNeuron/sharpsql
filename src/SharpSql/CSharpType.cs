using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SharpSql;

internal sealed record CSharpType(
    string Name,
    string Sql,
    bool IsBoolean = false,
    bool IsString = false,
    bool IsReference = false)
{
    public static readonly CSharpType Bool = new("bool", "BIT", IsBoolean: true);
    public static readonly CSharpType String = new("string", "NVARCHAR(MAX)", IsString: true);
    public static readonly CSharpType Int = new("int", "INT");
    public static readonly CSharpType Unknown = new("unknown", "SQL_VARIANT");

    public static CSharpType From(TypeSyntax syntax)
    {
        if (syntax is NullableTypeSyntax nullable)
            return From(nullable.ElementType);

        if (syntax is ArrayTypeSyntax array && array.ElementType.ToString() == "byte")
            return new("byte[]", "VARBINARY(MAX)");

        return syntax.ToString().Replace("global::", "", StringComparison.Ordinal) switch
        {
            "bool" or "System.Boolean" => Bool,
            "byte" or "System.Byte" => new("byte", "TINYINT"),
            "sbyte" or "System.SByte" => new("sbyte", "SMALLINT"),
            "short" or "System.Int16" => new("short", "SMALLINT"),
            "ushort" or "System.UInt16" => new("ushort", "INT"),
            "int" or "System.Int32" => Int,
            "uint" or "System.UInt32" => new("uint", "BIGINT"),
            "long" or "System.Int64" or "nint" or "System.IntPtr" => new("long", "BIGINT"),
            "ulong" or "System.UInt64" or "nuint" or "System.UIntPtr" => new("ulong", "DECIMAL(20,0)"),
            "float" or "System.Single" => new("float", "REAL"),
            "double" or "System.Double" => new("double", "FLOAT"),
            "decimal" or "System.Decimal" => new("decimal", "DECIMAL(38,18)"),
            "char" or "System.Char" => new("char", "NCHAR(1)"),
            "string" or "System.String" => String,
            "DateTime" or "System.DateTime" => new("DateTime", "DATETIME2"),
            "DateOnly" or "System.DateOnly" => new("DateOnly", "DATE"),
            "TimeOnly" or "System.TimeOnly" or "TimeSpan" or "System.TimeSpan" => new("TimeOnly", "TIME"),
            "Guid" or "System.Guid" => new("Guid", "UNIQUEIDENTIFIER"),
            "object" or "System.Object" => Unknown,
            "void" or "System.Void" => new("void", ""),
            "var" => Unknown,
            var name => new(name, "BIGINT", IsReference: true)
        };
    }

    public static CSharpType From(ITypeSymbol symbol)
    {
        if (symbol is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable)
            return From(nullable.TypeArguments[0]);
        if (symbol is IArrayTypeSymbol array)
        {
            var element = From(array.ElementType);
            return element.Name == "byte"
                ? new CSharpType("byte[]", "VARBINARY(MAX)")
                : new CSharpType(element.Name + "[]", "BIGINT", IsReference: true);
        }

        var special = symbol.SpecialType switch
        {
            SpecialType.System_Boolean => Bool,
            SpecialType.System_Byte => new CSharpType("byte", "TINYINT"),
            SpecialType.System_SByte => new CSharpType("sbyte", "SMALLINT"),
            SpecialType.System_Int16 => new CSharpType("short", "SMALLINT"),
            SpecialType.System_UInt16 => new CSharpType("ushort", "INT"),
            SpecialType.System_Int32 => Int,
            SpecialType.System_UInt32 => new CSharpType("uint", "BIGINT"),
            SpecialType.System_Int64 => new CSharpType("long", "BIGINT"),
            SpecialType.System_UInt64 => new CSharpType("ulong", "DECIMAL(20,0)"),
            SpecialType.System_Single => new CSharpType("float", "REAL"),
            SpecialType.System_Double => new CSharpType("double", "FLOAT"),
            SpecialType.System_Decimal => new CSharpType("decimal", "DECIMAL(38,18)"),
            SpecialType.System_Char => new CSharpType("char", "NCHAR(1)"),
            SpecialType.System_String => String,
            SpecialType.System_Object => Unknown,
            SpecialType.System_Void => new CSharpType("void", string.Empty),
            _ => null
        };
        if (special is not null)
            return special;

        if (symbol is INamedTypeSymbol named)
        {
            var simpleName = named.Name;
            if (simpleName == "DateTime") return new("DateTime", "DATETIME2");
            if (simpleName == "DateOnly") return new("DateOnly", "DATE");
            if (simpleName is "TimeOnly" or "TimeSpan") return new("TimeOnly", "TIME");
            if (simpleName == "Guid") return new("Guid", "UNIQUEIDENTIFIER");
            if (named.IsGenericType)
            {
                var arguments = string.Join(",", named.TypeArguments.Select(argument => From(argument).Name));
                return new($"{simpleName}<{arguments}>", "BIGINT", IsReference: true);
            }
            return new(simpleName, "BIGINT", IsReference: named.IsReferenceType);
        }

        return Unknown;
    }
}
