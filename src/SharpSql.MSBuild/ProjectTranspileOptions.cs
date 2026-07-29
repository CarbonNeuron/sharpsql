namespace SharpSql;

public sealed record ProjectTranspileOptions
{
    public string? EntryPoint { get; init; }
    public string Configuration { get; init; } = "Release";
    public string? TargetFramework { get; init; }
    public TranspileOptions CompilerOptions { get; init; } = new();
}
