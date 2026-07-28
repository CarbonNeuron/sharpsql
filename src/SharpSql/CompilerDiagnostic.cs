namespace SharpSql;

public sealed record CompilerDiagnostic(
    string Code,
    string Message,
    int Line,
    int Column)
{
    public override string ToString() => $"{Code} ({Line},{Column}): {Message}";
}
