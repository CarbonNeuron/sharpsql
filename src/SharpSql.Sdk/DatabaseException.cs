namespace SharpSql;

/// <summary>
/// Represents an unreserved SQL Server error caught by transpiled SharpSql code.
/// </summary>
public sealed class DatabaseException : Exception
{
    /// <summary>Creates an exception from a SQL Server error number and message.</summary>
    /// <param name="number">The value returned by <c>ERROR_NUMBER()</c>.</param>
    /// <param name="message">The value returned by <c>ERROR_MESSAGE()</c>.</param>
    public DatabaseException(int number, string message)
        : this(number, message, 0, 0, null, 0)
    {
    }

    /// <summary>Creates an exception from the SQL Server error metadata available to a catch block.</summary>
    /// <param name="number">The value returned by <c>ERROR_NUMBER()</c>.</param>
    /// <param name="message">The value returned by <c>ERROR_MESSAGE()</c>.</param>
    /// <param name="severity">The value returned by <c>ERROR_SEVERITY()</c>.</param>
    /// <param name="state">The value returned by <c>ERROR_STATE()</c>.</param>
    /// <param name="procedure">The value returned by <c>ERROR_PROCEDURE()</c>.</param>
    /// <param name="lineNumber">The value returned by <c>ERROR_LINE()</c>.</param>
    /// <param name="innerException">The exception that caused this exception, when available.</param>
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
