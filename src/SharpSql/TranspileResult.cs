namespace SharpSql;

/// <summary>Contains the output of a SharpSql transpilation.</summary>
/// <param name="Sql">The generated SQL batch, or an empty string when generation failed.</param>
/// <param name="Diagnostics">The diagnostics produced while binding or generating SQL.</param>
public sealed record TranspileResult(
    string Sql,
    IReadOnlyList<CompilerDiagnostic> Diagnostics)
{
    /// <summary>Gets whether transpilation completed without diagnostics.</summary>
    public bool Success => Diagnostics.Count == 0;
}
