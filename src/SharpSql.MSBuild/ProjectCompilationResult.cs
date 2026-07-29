using Microsoft.CodeAnalysis.CSharp;

namespace SharpSql;

public sealed record ProjectCompilationResult(
    CSharpCompilation? Compilation,
    IReadOnlyList<CompilerDiagnostic> Diagnostics)
{
    public bool Success => Compilation is not null && Diagnostics.Count == 0;
}
