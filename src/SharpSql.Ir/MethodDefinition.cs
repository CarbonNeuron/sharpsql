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

[Flags]
internal enum MethodEffects
{
    None = 0,
    ReadsMutableState = 1 << 0,
    WritesMutableState = 1 << 1,
    Allocates = 1 << 2,
    MayThrow = 1 << 3,
    Nondeterministic = 1 << 4,
    PerformsIo = 1 << 5,
    InvokesUnknown = 1 << 6,
    UsesRandom = 1 << 7
}

internal sealed record MethodBehaviorSummary(
    MethodEffects Effects,
    IReadOnlySet<int> MutatedParameters,
    IReadOnlySet<int> EscapingParameters,
    IReadOnlySet<int> ReturnedParameters,
    bool ReturnsFreshReference,
    bool ReturnsUnknownReference)
{
    private const MethodEffects ObservableEffects =
        MethodEffects.WritesMutableState |
        MethodEffects.Allocates |
        MethodEffects.MayThrow |
        MethodEffects.Nondeterministic |
        MethodEffects.PerformsIo |
        MethodEffects.InvokesUnknown;

    public bool IsSideEffectFree => (Effects & ObservableEffects) == MethodEffects.None;

    public bool IsDeterministic =>
        (Effects & (MethodEffects.Nondeterministic | MethodEffects.InvokesUnknown)) == MethodEffects.None;

    public static MethodBehaviorSummary Empty { get; } = new(
        MethodEffects.None,
        new HashSet<int>(),
        new HashSet<int>(),
        new HashSet<int>(),
        ReturnsFreshReference: false,
        ReturnsUnknownReference: false);
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
    public MethodBehaviorSummary Behavior { get; init; } = MethodBehaviorSummary.Empty;

    public IrExpression? PureExpression => ExpressionBody ??
        (Body?.Statements is [ProceduralReturn { Expression: not null } statement]
            ? statement.Expression
            : null);

    public int StatementCount => Flow.StatementCount == 0 ? 1 : Flow.StatementCount;
}
