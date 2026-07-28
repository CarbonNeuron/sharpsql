using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SharpSql;

internal sealed record ProceduralExpression(
    ExpressionSyntax Syntax,
    ExpressionFacts Facts);

internal sealed record ProceduralVariable(
    VariableDeclaratorSyntax Syntax,
    string Name,
    CSharpType DeclaredType,
    ProceduralExpression? Initializer);

internal sealed record ProceduralDeclaration(
    VariableDeclarationSyntax Syntax,
    IReadOnlyList<ProceduralVariable> Variables);

internal abstract record ProceduralStatement(StatementSyntax Source);

internal sealed record ProceduralBlock(
    BlockSyntax Syntax,
    IReadOnlyList<ProceduralStatement> Statements) : ProceduralStatement(Syntax);

internal sealed record ProceduralLocalFunction(LocalFunctionStatementSyntax Syntax) :
    ProceduralStatement(Syntax);

internal sealed record ProceduralDeclarationStatement(
    LocalDeclarationStatementSyntax Syntax,
    ProceduralDeclaration Declaration) : ProceduralStatement(Syntax);

internal sealed record ProceduralExpressionStatement(
    ExpressionStatementSyntax Syntax,
    ProceduralExpression Expression) : ProceduralStatement(Syntax);

internal sealed record ProceduralIf(
    IfStatementSyntax Syntax,
    ProceduralExpression Condition,
    ProceduralStatement Then,
    ProceduralStatement? Else) : ProceduralStatement(Syntax);

internal sealed record ProceduralWhile(
    WhileStatementSyntax Syntax,
    ProceduralExpression Condition,
    ProceduralStatement Body) : ProceduralStatement(Syntax);

internal sealed record ProceduralDo(
    DoStatementSyntax Syntax,
    ProceduralExpression Condition,
    ProceduralStatement Body) : ProceduralStatement(Syntax);

internal sealed record ProceduralFor(
    ForStatementSyntax Syntax,
    ProceduralDeclaration? Declaration,
    IReadOnlyList<ProceduralExpression> Initializers,
    ProceduralExpression? Condition,
    IReadOnlyList<ProceduralExpression> Incrementors,
    ProceduralStatement Body) : ProceduralStatement(Syntax);

internal sealed record ProceduralForEach(
    ForEachStatementSyntax Syntax,
    ProceduralExpression SourceExpression,
    CSharpType ElementType,
    ProceduralStatement Body) : ProceduralStatement(Syntax);

internal sealed record ProceduralBreak(BreakStatementSyntax Syntax) : ProceduralStatement(Syntax);

internal sealed record ProceduralContinue(ContinueStatementSyntax Syntax) : ProceduralStatement(Syntax);

internal sealed record ProceduralReturn(
    ReturnStatementSyntax Syntax,
    ProceduralExpression? Expression) : ProceduralStatement(Syntax);

internal sealed record ProceduralEmpty(EmptyStatementSyntax Syntax) : ProceduralStatement(Syntax);

internal sealed record ProceduralUnsupported(StatementSyntax Syntax) : ProceduralStatement(Syntax);
