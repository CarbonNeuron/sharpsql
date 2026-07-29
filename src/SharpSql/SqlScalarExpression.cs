namespace SharpSql;

/// <summary>
/// Typed scalar boundary between C# analysis and SQL rendering.
/// </summary>
internal sealed record SqlScalarExpression(
    string Sql,
    IrType Type,
    int Precedence,
    ScalarNullability Nullability = ScalarNullability.Unknown)
{
    public static SqlScalarExpression Primary(
        string sql,
        IrType? type = null,
        ScalarNullability nullability = ScalarNullability.Unknown) =>
        new(sql, type ?? IrType.Unknown, 100, nullability);

    public SqlScalarExpression WithAnalysis(IrType type, ScalarNullability nullability) =>
        this with
        {
            Type = Type == IrType.Unknown ? type : Type,
            Nullability = Nullability == ScalarNullability.Unknown ? nullability : Nullability
        };

    public SqlScalarExpression CastTo(IrType targetType) =>
        Primary($"CAST({Sql} AS {targetType.SqlType()})", targetType, Nullability);

    public string Render(int requiredPrecedence) =>
        Precedence < requiredPrecedence ? $"({Sql})" : Sql;
}
