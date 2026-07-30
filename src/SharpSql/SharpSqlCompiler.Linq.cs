using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SharpSql;

internal enum LinqQueryStepKind
{
    Where,
    Select,
    OrderBy,
    ThenBy
}

internal abstract record SqlLinqQueryStep;

internal sealed record SqlLinqScalarCapture(SqlScalarExpression Expression);

internal sealed record SqlLinqLambdaPlan(
    IrExpression Expression,
    IReadOnlyDictionary<string, SqlLinqScalarCapture>? Captures = null);

internal sealed record SqlLinqLambdaQueryStep(
    LinqQueryStepKind Kind,
    string ParameterName,
    IrExpression Body,
    IrType ResultType,
    bool Negated = false,
    bool Descending = false,
    IReadOnlyDictionary<string, SqlLinqScalarCapture>? Captures = null) : SqlLinqQueryStep;

internal sealed record SqlLinqPagingQueryStep(
    bool IsSkip,
    string CountSql) : SqlLinqQueryStep;

internal sealed record SqlLinqDistinctQueryStep : SqlLinqQueryStep;

internal sealed record SqlLinqGroupQueryStep(
    string ParameterName,
    IrExpression KeyBody,
    IrType KeyType,
    IrType ElementType,
    IReadOnlyDictionary<string, SqlLinqScalarCapture>? Captures = null) : SqlLinqQueryStep;

internal sealed record SqlLinqJoinQueryStep(
    SqlLinqQueryPlan InnerQuery,
    string OuterParameterName,
    IrExpression OuterKeyBody,
    string InnerParameterName,
    IrExpression InnerKeyBody,
    IrType KeyType,
    string ResultOuterParameterName,
    string ResultInnerParameterName,
    IrExpression ResultBody,
    IrType ResultType,
    IReadOnlyDictionary<string, SqlLinqScalarCapture>? OuterCaptures = null,
    IReadOnlyDictionary<string, SqlLinqScalarCapture>? InnerCaptures = null,
    IReadOnlyDictionary<string, SqlLinqScalarCapture>? ResultCaptures = null) : SqlLinqQueryStep;

internal abstract record SqlLinqQuerySource;

internal sealed record SqlLinqHeapQuerySource(string OwnerSql) : SqlLinqQuerySource;

internal sealed record SqlLinqRangeQuerySource(
    string StartSql,
    string CountSql) : SqlLinqQuerySource;

internal sealed record SqlLinqRepeatQuerySource(
    string ValueSql,
    string CountSql) : SqlLinqQuerySource;

internal sealed record SqlLinqTaskResultQuerySource(
    string TaskIdsJsonSql,
    string ExecutionIdSql) : SqlLinqQuerySource;

internal sealed record SqlLinqQueryPlan(
    SqlLinqQuerySource Source,
    IrType SourceElementType,
    IrType ElementType,
    IReadOnlyList<SqlLinqQueryStep> Steps);

public sealed partial class SharpSqlCompiler
{
    private int _linqId;
    private readonly Stack<IReadOnlyDictionary<string, SqlLinqQueryPlan>> _linqPlanSubstitutions = [];
    private readonly Stack<IReadOnlyDictionary<string, SqlLinqLambdaPlan>> _linqLambdaSubstitutions = [];

}
