namespace SharpSql;

/// <summary>Controls loading and transpiling a C# project.</summary>
public sealed record ProjectTranspileOptions
{
    /// <summary>Gets the optional static entry method to compile.</summary>
    public string? EntryPoint { get; init; }

    /// <summary>Gets the MSBuild configuration used to load the project.</summary>
    public string Configuration { get; init; } = "Release";

    /// <summary>Gets the target framework to select for a multi-targeted project.</summary>
    public string? TargetFramework { get; init; }

    /// <summary>Gets the compiler settings applied after project loading.</summary>
    public TranspileOptions CompilerOptions { get; init; } = new();
}
