namespace SharpSql;

internal enum ScalarNullability
{
    Unknown,
    NonNull,
    MaybeNull,
    Null
}

/// <summary>
/// Typed scalar boundary between C# analysis and SQL rendering.
/// </summary>
internal sealed record SqlScalarExpression(
    string Sql,
    CSharpType Type,
    int Precedence,
    ScalarNullability Nullability = ScalarNullability.Unknown)
{
    public static SqlScalarExpression Primary(
        string sql,
        CSharpType? type = null,
        ScalarNullability nullability = ScalarNullability.Unknown) =>
        new(sql, type ?? CSharpType.Unknown, 100, nullability);

    public SqlScalarExpression WithAnalysis(CSharpType type, ScalarNullability nullability) =>
        this with
        {
            Type = Type == CSharpType.Unknown ? type : Type,
            Nullability = Nullability == ScalarNullability.Unknown ? nullability : Nullability
        };

    public SqlScalarExpression CastTo(CSharpType targetType) =>
        Primary($"CAST({Sql} AS {targetType.Sql})", targetType, Nullability);

    public string Render(int requiredPrecedence) =>
        Precedence < requiredPrecedence ? $"({Sql})" : Sql;
}

internal sealed record ExpressionFacts(
    CSharpType Type,
    ScalarNullability Nullability,
    bool HasConstantValue,
    object? ConstantValue);
