namespace SharpSql;

internal sealed record ProceduralVariable(
    IrSource Source,
    IrSymbol Symbol,
    IrExpression? Initializer)
{
    public string Name => Symbol.Name;
    public IrType DeclaredType => Symbol.Type;
}

internal sealed record ProceduralDeclaration(
    IrSource Source,
    IReadOnlyList<ProceduralVariable> Variables);

internal abstract record ProceduralStatement(IrSource Source);

internal sealed record ProceduralBlock(
    IrSource Source,
    IReadOnlyList<ProceduralStatement> Statements) : ProceduralStatement(Source);

internal sealed record ProceduralLocalFunction(IrSource Source, string Name) :
    ProceduralStatement(Source);

internal sealed record ProceduralDeclarationStatement(
    IrSource Source,
    ProceduralDeclaration Declaration) : ProceduralStatement(Source);

internal sealed record ProceduralExpressionStatement(
    IrSource Source,
    IrExpression Expression) : ProceduralStatement(Source);

internal sealed record ProceduralIf(
    IrSource Source,
    IrExpression Condition,
    ProceduralStatement Then,
    ProceduralStatement? Else) : ProceduralStatement(Source);

internal sealed record ProceduralWhile(
    IrSource Source,
    IrExpression Condition,
    ProceduralStatement Body) : ProceduralStatement(Source);

internal sealed record ProceduralDo(
    IrSource Source,
    IrExpression Condition,
    ProceduralStatement Body) : ProceduralStatement(Source);

internal sealed record ProceduralFor(
    IrSource Source,
    ProceduralDeclaration? Declaration,
    IReadOnlyList<IrExpression> Initializers,
    IrExpression? Condition,
    IReadOnlyList<IrExpression> Incrementors,
    ProceduralStatement Body) : ProceduralStatement(Source);

internal sealed record ProceduralForEach(
    IrSource Source,
    IrSymbol Element,
    IrExpression SourceExpression,
    ProceduralStatement Body) : ProceduralStatement(Source)
{
    public IrType ElementType => Element.Type;
}

internal sealed record IrExceptionType(
    string MetadataName,
    IReadOnlyList<string> BaseMetadataNames)
{
    public string Name => MetadataName.Split('.').Last();

    public bool IsOrDerivesFrom(string metadataName) =>
        string.Equals(MetadataName, metadataName, StringComparison.Ordinal) ||
        BaseMetadataNames.Contains(metadataName, StringComparer.Ordinal);
}

internal sealed record ProceduralCatch(
    IrSource Source,
    IrExceptionType? ExceptionType,
    IrSymbol? Exception,
    IrExpression? Filter,
    ProceduralBlock Body);

internal sealed record ProceduralTry(
    IrSource Source,
    ProceduralBlock Body,
    IReadOnlyList<ProceduralCatch> Catches) : ProceduralStatement(Source);

internal sealed record ProceduralThrow(
    IrSource Source,
    IrExpression? Expression,
    IrExceptionType? ExceptionType) : ProceduralStatement(Source);

internal sealed record ProceduralBreak(IrSource Source) : ProceduralStatement(Source);

internal sealed record ProceduralContinue(IrSource Source) : ProceduralStatement(Source);

internal sealed record ProceduralReturn(
    IrSource Source,
    IrExpression? Expression) : ProceduralStatement(Source);

internal sealed record ProceduralEmpty(IrSource Source) : ProceduralStatement(Source);

internal sealed record ProceduralUnsupported(IrSource Source, string Description) : ProceduralStatement(Source);
