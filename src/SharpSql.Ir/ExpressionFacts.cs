namespace SharpSql;

internal enum ScalarNullability
{
    Unknown,
    NonNull,
    MaybeNull,
    Null
}

internal sealed record ExpressionFacts(
    IrType Type,
    ScalarNullability Nullability,
    bool HasConstantValue,
    object? ConstantValue);
