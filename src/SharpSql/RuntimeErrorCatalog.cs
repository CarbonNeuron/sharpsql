namespace SharpSql;

internal static class RuntimeErrorCatalog
{
    public static bool IsSharpSqlRuntimeError(int number) => number is >= 51000 and <= 51999;

    public static string? ExceptionTypeName(int number) => number switch
    {
        51001 => nameof(ArgumentException),
        51002 or 51004 or 51005 or 51006 or 51009 => nameof(ArgumentOutOfRangeException),
        51003 => nameof(IndexOutOfRangeException),
        51007 or 51008 => nameof(InvalidOperationException),
        51010 => nameof(KeyNotFoundException),
        51011 => nameof(NullReferenceException),
        _ => null
    };
}
