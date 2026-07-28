using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SharpSql;

internal sealed record ParameterDefinition(string Name, CSharpType Type);

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
    CSharpType ReturnType,
    IReadOnlyList<ParameterDefinition> Parameters,
    BlockSyntax? Body,
    ExpressionSyntax? ExpressionBody,
    SyntaxNode Syntax,
    string? ContainingType = null,
    bool IsInstance = false)
{
    public MethodFlowSummary Flow { get; init; } = MethodFlowSummary.Empty;

    public ExpressionSyntax? PureExpression => ExpressionBody ??
        (Body?.Statements is [ReturnStatementSyntax { Expression: not null } statement]
            ? statement.Expression
            : null);

    public int StatementCount => Flow.StatementCount == 0 ? 1 : Flow.StatementCount;
}
