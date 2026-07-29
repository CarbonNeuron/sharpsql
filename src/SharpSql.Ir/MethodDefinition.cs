namespace SharpSql;

internal sealed record ParameterDefinition(IrSymbol Symbol)
{
    public string Name => Symbol.Name;
    public IrType Type => Symbol.Type;
}

internal sealed record MethodFlowSummary(
    bool EndPointIsReachable,
    bool ContainsReturn,
    int StatementCount,
    IReadOnlySet<string> ReadVariables,
    IReadOnlySet<string> WrittenVariables,
    IReadOnlySet<string> CapturedVariables)
{
    public static MethodFlowSummary Empty { get; } = new(
        EndPointIsReachable: true,
        ContainsReturn: false,
        StatementCount: 0,
        new HashSet<string>(StringComparer.Ordinal),
        new HashSet<string>(StringComparer.Ordinal),
        new HashSet<string>(StringComparer.Ordinal));
}

internal sealed record MethodDefinition(
    string Name,
    IrType ReturnType,
    IReadOnlyList<ParameterDefinition> Parameters,
    ProceduralBlock? Body,
    IrExpression? ExpressionBody,
    IrSource Source,
    string? ContainingType = null,
    bool IsInstance = false)
{
    public MethodFlowSummary Flow { get; init; } = MethodFlowSummary.Empty;

    public IrExpression? PureExpression => ExpressionBody ??
        (Body?.Statements is [ProceduralReturn { Expression: not null } statement]
            ? statement.Expression
            : null);

    public int StatementCount => Flow.StatementCount == 0 ? 1 : Flow.StatementCount;
}
