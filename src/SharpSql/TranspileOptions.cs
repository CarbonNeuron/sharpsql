namespace SharpSql;

public sealed record TranspileOptions
{
    public int MaxInlineStatements { get; init; } = 40;
    public int MaxInlineCallSites { get; init; } = 8;
    public bool EmitNoCount { get; init; } = true;
}
