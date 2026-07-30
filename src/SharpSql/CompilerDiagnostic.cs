namespace SharpSql;

/// <summary>Describes a compiler diagnostic associated with a source location.</summary>
/// <param name="Code">The stable SharpSql or Roslyn diagnostic identifier.</param>
/// <param name="Message">The human-readable diagnostic message.</param>
/// <param name="Line">The one-based source line, or zero when no location is available.</param>
/// <param name="Column">The one-based source column, or zero when no location is available.</param>
/// <param name="FilePath">The source file path, when known.</param>
public sealed record CompilerDiagnostic(
    string Code,
    string Message,
    int Line,
    int Column,
    string? FilePath = null)
{
    /// <summary>Formats the diagnostic using compiler-style location syntax.</summary>
    public override string ToString() => string.IsNullOrWhiteSpace(FilePath)
        ? $"{Code} ({Line},{Column}): {Message}"
        : $"{FilePath}({Line},{Column}): {Code}: {Message}";
}
