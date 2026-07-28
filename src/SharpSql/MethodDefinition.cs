using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SharpSql;

internal sealed record ParameterDefinition(string Name, CSharpType Type);

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
    public ExpressionSyntax? PureExpression => ExpressionBody ??
        (Body?.Statements is [ReturnStatementSyntax { Expression: not null } statement]
            ? statement.Expression
            : null);

    public int StatementCount => Body?.DescendantNodes().OfType<StatementSyntax>().Count() ?? 1;
}
