namespace SharpSql;

/// <summary>
/// Represents an unreserved SQL Server error caught by transpiled SharpSql code.
/// </summary>
public sealed class DatabaseException : Exception
{
    public DatabaseException(int number, string message)
        : this(number, message, 0, 0, null, 0)
    {
    }

    public DatabaseException(
        int number,
        string message,
        int severity,
        int state,
        string? procedure,
        int lineNumber,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Number = number;
        Severity = severity;
        State = state;
        Procedure = procedure;
        LineNumber = lineNumber;
    }

    /// <summary>Gets the value exposed by SQL Server's ERROR_NUMBER().</summary>
    public int Number { get; }

    /// <summary>Gets the value exposed by SQL Server's ERROR_SEVERITY().</summary>
    public int Severity { get; }

    /// <summary>Gets the value exposed by SQL Server's ERROR_STATE().</summary>
    public int State { get; }

    /// <summary>Gets the value exposed by SQL Server's ERROR_PROCEDURE().</summary>
    public string? Procedure { get; }

    /// <summary>Gets the value exposed by SQL Server's ERROR_LINE().</summary>
    public int LineNumber { get; }
}
