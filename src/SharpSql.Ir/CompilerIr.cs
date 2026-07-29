namespace SharpSql;

internal readonly record struct IrSourceSpan(int Start, int Length, int Line, int Column)
{
    public static IrSourceSpan None { get; } = new(-1, 0, 0, 0);
}

internal enum IrCommentKind
{
    Line,
    Block,
    Documentation
}

internal sealed record IrComment(int Start, string Text, IrCommentKind Kind);

internal sealed record IrSource(
    IrSourceSpan Span,
    IReadOnlyList<IrComment> LeadingComments,
    IReadOnlyList<IrComment> TrailingComments,
    IReadOnlyList<IrComment> DescendantComments)
{
    public static IrSource None { get; } = new(
        IrSourceSpan.None,
        Array.Empty<IrComment>(),
        Array.Empty<IrComment>(),
        Array.Empty<IrComment>());
}

internal readonly record struct IrSymbolId(int Value)
{
    public static IrSymbolId None { get; } = new(0);
}

internal sealed record IrSymbol(IrSymbolId Id, string Name, IrType Type);

internal enum IrBinaryOperator
{
    Add,
    Subtract,
    Multiply,
    Divide,
    Remainder,
    BitwiseAnd,
    BitwiseOr,
    ExclusiveOr,
    LogicalAnd,
    LogicalOr,
    Equal,
    NotEqual,
    LessThan,
    LessThanOrEqual,
    GreaterThan,
    GreaterThanOrEqual,
    Coalesce
}

internal enum IrUnaryOperator
{
    Identity,
    Negate,
    LogicalNot,
    BitwiseNot,
    PreIncrement,
    PreDecrement,
    PostIncrement,
    PostDecrement
}

internal enum IrAssignmentOperator
{
    Assign,
    Add,
    Subtract,
    Multiply,
    Divide,
    Remainder,
    BitwiseAnd,
    BitwiseOr,
    ExclusiveOr
}

internal abstract record IrExpression(IrSource Source, ExpressionFacts Facts)
{
    public IrType Type => Facts.Type;
}

internal sealed record IrConstantExpression(
    IrSource Source,
    ExpressionFacts Facts,
    object? Value,
    string SourceText) : IrExpression(Source, Facts);

internal sealed record IrVariableExpression(
    IrSource Source,
    ExpressionFacts Facts,
    IrSymbol Symbol) : IrExpression(Source, Facts);

internal sealed record IrThisExpression(
    IrSource Source,
    ExpressionFacts Facts,
    IrSymbol Symbol) : IrExpression(Source, Facts);

internal sealed record IrBinaryExpression(
    IrSource Source,
    ExpressionFacts Facts,
    IrBinaryOperator Operator,
    IrExpression Left,
    IrExpression Right) : IrExpression(Source, Facts);

internal sealed record IrUnaryExpression(
    IrSource Source,
    ExpressionFacts Facts,
    IrUnaryOperator Operator,
    IrExpression Operand) : IrExpression(Source, Facts);

internal sealed record IrConversionExpression(
    IrSource Source,
    ExpressionFacts Facts,
    IrType TargetType,
    IrExpression Operand) : IrExpression(Source, Facts);

internal sealed record IrConditionalExpression(
    IrSource Source,
    ExpressionFacts Facts,
    IrExpression Condition,
    IrExpression WhenTrue,
    IrExpression WhenFalse) : IrExpression(Source, Facts);

internal sealed record IrMemberExpression(
    IrSource Source,
    ExpressionFacts Facts,
    IrExpression Receiver,
    string MemberName) : IrExpression(Source, Facts);

internal sealed record IrElementExpression(
    IrSource Source,
    ExpressionFacts Facts,
    IrExpression Receiver,
    IReadOnlyList<IrExpression> Arguments) : IrExpression(Source, Facts);

internal sealed record IrInvocationExpression(
    IrSource Source,
    ExpressionFacts Facts,
    IrExpression Target,
    IReadOnlyList<IrExpression> Arguments) : IrExpression(Source, Facts)
{
    public string? MethodName => Target is IrMemberExpression member
        ? member.MemberName
        : Target is IrVariableExpression variable
            ? variable.Symbol.Name
            : null;
}

internal sealed record IrObjectCreationExpression(
    IrSource Source,
    ExpressionFacts Facts,
    IrType CreatedType,
    IReadOnlyList<IrExpression> Arguments,
    IReadOnlyList<IrExpression> Initializers) : IrExpression(Source, Facts);

internal sealed record IrArrayCreationExpression(
    IrSource Source,
    ExpressionFacts Facts,
    IrType ElementType,
    IrExpression? Length,
    IReadOnlyList<IrExpression> Elements) : IrExpression(Source, Facts);

internal sealed record IrInterpolatedText(string Text);
internal sealed record IrInterpolation(IrExpression Expression);

internal sealed record IrInterpolatedStringExpression(
    IrSource Source,
    ExpressionFacts Facts,
    IReadOnlyList<object> Parts) : IrExpression(Source, Facts);

internal sealed record IrAssignmentExpression(
    IrSource Source,
    ExpressionFacts Facts,
    IrAssignmentOperator Operator,
    IrExpression Target,
    IrExpression Value) : IrExpression(Source, Facts);

internal sealed record IrLambdaExpression(
    IrSource Source,
    ExpressionFacts Facts,
    IReadOnlyList<IrSymbol> Parameters,
    IrExpression? ExpressionBody,
    ProceduralBlock? StatementBody) : IrExpression(Source, Facts);

internal sealed record IrQueryExpression(
    IrSource Source,
    ExpressionFacts Facts,
    IrSymbol RangeVariable,
    IrExpression SourceExpression,
    IReadOnlyList<IrQueryClause> Clauses) : IrExpression(Source, Facts);

internal abstract record IrQueryClause(IrSource Source);
internal sealed record IrWhereClause(IrSource Source, IrExpression Predicate) : IrQueryClause(Source);
internal sealed record IrOrderClause(IrSource Source, IrExpression Key, bool Descending, bool IsThenBy) : IrQueryClause(Source);
internal sealed record IrSelectClause(IrSource Source, IrExpression Projection) : IrQueryClause(Source);
internal sealed record IrGroupClause(IrSource Source, IrExpression Element, IrExpression Key) : IrQueryClause(Source);

internal sealed record IrUnsupportedExpression(
    IrSource Source,
    ExpressionFacts Facts,
    string Description) : IrExpression(Source, Facts);

internal sealed record IrProgram(
    IReadOnlyList<MethodDefinition> Methods,
    ProceduralBlock EntryPoint,
    IReadOnlyList<IrComment> FileComments);
