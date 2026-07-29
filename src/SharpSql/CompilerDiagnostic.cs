namespace SharpSql;

public sealed record CompilerDiagnostic(
    string Code,
    string Message,
    int Line,
    int Column,
    string? FilePath = null)
{
    public override string ToString() => string.IsNullOrWhiteSpace(FilePath)
        ? $"{Code} ({Line},{Column}): {Message}"
        : $"{FilePath}({Line},{Column}): {Code}: {Message}";
}
