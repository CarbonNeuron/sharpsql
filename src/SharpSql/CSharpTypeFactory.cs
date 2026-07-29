using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SharpSql;

/// <summary>
/// C# frontend mapping from Roslyn types into backend-neutral IR types.
/// </summary>
internal static class CSharpTypeFactory
{
    public static IrType From(TypeSyntax syntax)
    {
        if (syntax is NullableTypeSyntax nullable)
            return From(nullable.ElementType);

        if (syntax is ArrayTypeSyntax array && array.ElementType.ToString() == "byte")
            return new("byte[]");

        return FromName(syntax.ToString().Replace("global::", "", StringComparison.Ordinal));
    }

    public static IrType From(ITypeSymbol symbol)
    {
        if (symbol is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable)
            return From(nullable.TypeArguments[0]);
        if (symbol is IArrayTypeSymbol array)
        {
            var element = From(array.ElementType);
            return element.Name == "byte"
                ? new IrType("byte[]")
                : new IrType(element.Name + "[]", IsReference: true);
        }

        var special = symbol.SpecialType switch
        {
            SpecialType.System_Boolean => IrType.Bool,
            SpecialType.System_Byte => new IrType("byte"),
            SpecialType.System_SByte => new IrType("sbyte"),
            SpecialType.System_Int16 => new IrType("short"),
            SpecialType.System_UInt16 => new IrType("ushort"),
            SpecialType.System_Int32 => IrType.Int,
            SpecialType.System_UInt32 => new IrType("uint"),
            SpecialType.System_Int64 => new IrType("long"),
            SpecialType.System_UInt64 => new IrType("ulong"),
            SpecialType.System_Single => new IrType("float"),
            SpecialType.System_Double => new IrType("double"),
            SpecialType.System_Decimal => new IrType("decimal"),
            SpecialType.System_Char => new IrType("char"),
            SpecialType.System_String => IrType.String,
            SpecialType.System_Object => IrType.Unknown,
            SpecialType.System_Void => IrType.Void,
            _ => null
        };
        if (special is not null)
            return special;

        if (symbol is INamedTypeSymbol named)
        {
            var simpleName = named.Name;
            if (simpleName is "DateTime" or "DateOnly" or "TimeOnly" or "TimeSpan" or "Guid")
                return new(simpleName == "TimeSpan" ? "TimeOnly" : simpleName);
            if (named.IsGenericType)
            {
                var arguments = string.Join(",", named.TypeArguments.Select(argument => From(argument).Name));
                return new($"{simpleName}<{arguments}>", IsReference: true);
            }
            return new(simpleName, IsReference: named.IsReferenceType);
        }

        return IrType.Unknown;
    }

    private static IrType FromName(string name) => name switch
    {
        "bool" or "System.Boolean" => IrType.Bool,
        "byte" or "System.Byte" => new("byte"),
        "sbyte" or "System.SByte" => new("sbyte"),
        "short" or "System.Int16" => new("short"),
        "ushort" or "System.UInt16" => new("ushort"),
        "int" or "System.Int32" => IrType.Int,
        "uint" or "System.UInt32" => new("uint"),
        "long" or "System.Int64" or "nint" or "System.IntPtr" => new("long"),
        "ulong" or "System.UInt64" or "nuint" or "System.UIntPtr" => new("ulong"),
        "float" or "System.Single" => new("float"),
        "double" or "System.Double" => new("double"),
        "decimal" or "System.Decimal" => new("decimal"),
        "char" or "System.Char" => new("char"),
        "string" or "System.String" => IrType.String,
        "DateTime" or "System.DateTime" => new("DateTime"),
        "DateOnly" or "System.DateOnly" => new("DateOnly"),
        "TimeOnly" or "System.TimeOnly" or "TimeSpan" or "System.TimeSpan" => new("TimeOnly"),
        "Guid" or "System.Guid" => new("Guid"),
        "object" or "System.Object" => IrType.Unknown,
        "void" or "System.Void" => IrType.Void,
        "var" => IrType.Unknown,
        _ => new(name, IsReference: true)
    };
}
