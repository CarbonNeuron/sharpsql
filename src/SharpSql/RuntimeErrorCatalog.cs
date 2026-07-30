namespace SharpSql;

internal static class RuntimeErrorCatalog
{
    public const int ApplicationExceptionErrorNumber = 51012;

    public static bool IsSharpSqlRuntimeError(int number) => number is >= 51000 and <= 51999;

    public static bool IsDatabaseException(IrExceptionType type) =>
        type.MetadataName == "SharpSql.DatabaseException";

    public static bool IsCatchAll(IrExceptionType type) =>
        type.MetadataName == "System.Exception";

    public static IReadOnlyList<int>? ErrorNumbersCaughtBy(IrExceptionType type) =>
        type.MetadataName switch
        {
            "System.ApplicationException" => [ApplicationExceptionErrorNumber],
            "System.ArgumentException" => [51001, 51002, 51004, 51005, 51006, 51009],
            "System.ArgumentOutOfRangeException" => [51002, 51004, 51005, 51006, 51009],
            "System.IndexOutOfRangeException" => [51003],
            "System.InvalidOperationException" => [51007, 51008],
            "System.Collections.Generic.KeyNotFoundException" => [51010],
            "System.NullReferenceException" => [51011],
            "System.SystemException" => [51001, 51002, 51003, 51004, 51005, 51006, 51007, 51008, 51009, 51010, 51011],
            _ => null
        };

    public static string? ExceptionTypeName(int number) => number switch
    {
        51001 => nameof(ArgumentException),
        51002 or 51004 or 51005 or 51006 or 51009 => nameof(ArgumentOutOfRangeException),
        51003 => nameof(IndexOutOfRangeException),
        51007 or 51008 => nameof(InvalidOperationException),
        51010 => nameof(KeyNotFoundException),
        51011 => nameof(NullReferenceException),
        ApplicationExceptionErrorNumber => nameof(ApplicationException),
        _ => null
    };

    public static (string Type, string Message, int Code) NormalizeSqlFailure(int number, string message) =>
        (ExceptionTypeName(number) ?? "SqlException", message, number);
}
