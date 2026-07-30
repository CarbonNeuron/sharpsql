using Microsoft.CodeAnalysis.CSharp;

namespace SharpSql;

/// <summary>Contains the result of loading a Roslyn compilation from an MSBuild project.</summary>
/// <param name="Compilation">The loaded compilation, or <see langword="null"/> when loading failed.</param>
/// <param name="Diagnostics">The project-loading and C# compilation diagnostics.</param>
public sealed record ProjectCompilationResult(
    CSharpCompilation? Compilation,
    IReadOnlyList<CompilerDiagnostic> Diagnostics)
{
    /// <summary>Gets whether a compilation was loaded without diagnostics.</summary>
    public bool Success => Compilation is not null && Diagnostics.Count == 0;
}
