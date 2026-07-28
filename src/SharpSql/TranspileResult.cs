namespace SharpSql;

public sealed record TranspileResult(
    string Sql,
    IReadOnlyList<CompilerDiagnostic> Diagnostics)
{
    public bool Success => Diagnostics.Count == 0;
}
