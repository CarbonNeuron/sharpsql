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
    string? FilePath,
    IReadOnlyList<IrComment> LeadingComments,
    IReadOnlyList<IrComment> TrailingComments,
    IReadOnlyList<IrComment> DescendantComments)
{
    public static IrSource None { get; } = new(
        IrSourceSpan.None,
        null,
        Array.Empty<IrComment>(),
        Array.Empty<IrComment>(),
        Array.Empty<IrComment>());
}

internal readonly record struct IrSymbolId(int Value)
{
    public static IrSymbolId None { get; } = new(0);
}

internal readonly record struct IrTypeDefinitionId(string Value)
{
    public static IrTypeDefinitionId None { get; } = new(string.Empty);
    public bool IsNone => string.IsNullOrEmpty(Value);
}

internal readonly record struct IrMemberId(string Value)
{
    public static IrMemberId None { get; } = new(string.Empty);
    public bool IsNone => string.IsNullOrEmpty(Value);
}

internal readonly record struct IrMethodId(string Value)
{
    public static IrMethodId None { get; } = new(string.Empty);
    public bool IsNone => string.IsNullOrEmpty(Value);
}

internal readonly record struct IrConstructorId(string Value)
{
    public static IrConstructorId None { get; } = new(string.Empty);
    public bool IsNone => string.IsNullOrEmpty(Value);
}

internal enum IrCallDispatch
{
    Unknown,
    Static,
    Direct,
    Virtual,
    Interface,
    Delegate
}

internal enum IrMemberKind
{
    Field,
    Property
}

internal enum IrConstructorInitializerKind
{
    None,
    This,
    Base
}

internal sealed record IrSymbol(IrSymbolId Id, string Name, IrType Type)
{
    public IrMemberId ReferencedMemberId { get; init; } = IrMemberId.None;
    public IrMethodId ReferencedMethodId { get; init; } = IrMethodId.None;
}

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

internal sealed record IrAwaitExpression(
    IrSource Source,
    ExpressionFacts Facts,
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
    string MemberName) : IrExpression(Source, Facts)
{
    public IrMemberId MemberId { get; init; } = IrMemberId.None;
    public IrMethodId ReferencedMethodId { get; init; } = IrMethodId.None;
}

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
    public IrMethodId TargetMethodId { get; init; } = IrMethodId.None;
    public IrCallDispatch Dispatch { get; init; } = IrCallDispatch.Unknown;

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
    IReadOnlyList<IrExpression> Initializers) : IrExpression(Source, Facts)
{
    public IrConstructorId ConstructorId { get; init; } = IrConstructorId.None;
}

internal sealed record IrWithExpression(
    IrSource Source,
    ExpressionFacts Facts,
    IrExpression Receiver,
    IReadOnlyList<IrAssignmentExpression> Initializers) : IrExpression(Source, Facts);

internal sealed record IrArrayCreationExpression(
    IrSource Source,
    ExpressionFacts Facts,
    IrType ElementType,
    IrExpression? Length,
    IReadOnlyList<IrExpression> Elements) : IrExpression(Source, Facts)
{
    public int Rank { get; init; } = 1;
}

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

internal sealed record IrHeapFieldDefinition(
    string Name,
    IrType Type,
    IrSource Source)
{
    public IrMemberId Id { get; init; } = IrMemberId.None;
    public IrMemberKind Kind { get; init; }
    public bool IsStatic { get; init; }
    public bool IsReadOnly { get; init; }
    public IrExpression? Initializer { get; init; }
}

internal sealed record IrHeapConstructorDefinition(
    IReadOnlyList<string> TargetFields)
{
    public IrConstructorId Id { get; init; } = IrConstructorId.None;
    public IReadOnlyList<ParameterDefinition> Parameters { get; init; } = [];
    public ProceduralBlock? Body { get; init; }
    public IrConstructorInitializerKind InitializerKind { get; init; }
    public IrConstructorId InitializerConstructorId { get; init; } = IrConstructorId.None;
    public IReadOnlyList<IrExpression> InitializerArguments { get; init; } = [];
    public bool IsFieldAssignmentOnly { get; init; } = true;
}

internal sealed record IrHeapTypeDefinition(
    string Name,
    bool IsValueType,
    bool IsRecord,
    IReadOnlyList<IrHeapFieldDefinition> Fields,
    IReadOnlyList<IrHeapConstructorDefinition> Constructors,
    IrSource Source)
{
    public IrTypeDefinitionId Id { get; init; } = IrTypeDefinitionId.None;
    public IrType? BaseType { get; init; }
    public IReadOnlyList<IrType> Interfaces { get; init; } = [];
    public bool IsAbstract { get; init; }
    public bool IsSealed { get; init; }
}

internal sealed record IrProgram(
    IReadOnlyList<MethodDefinition> Methods,
    ProceduralBlock EntryPoint,
    IReadOnlyList<IrComment> FileComments)
{
    public IReadOnlyList<IrHeapTypeDefinition> HeapTypes { get; init; } = [];
}
