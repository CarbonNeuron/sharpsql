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

    private bool TryEmitLinqDelegateDeclaration(IrExpression initializer, string sourceName, IrType type, VariableScope scope)
    {
        if (!TryBuildLinqLambda(initializer, scope, null, out var lambda)) return false;
        scope.Add(sourceName, new LambdaVariableBinding(type, lambda));
        return true;
    }

    private bool TryEmitLinqQueryDeclaration(IrExpression initializer, string sourceName, string sqlName, IrType type, VariableScope scope)
    {
        if (!IsLinqQueryExpression(initializer, scope)) return false;
        if (!TryBuildLinqQuery(initializer, scope, null, out var query))
        {
            if (HasCSharpSource(initializer.Source))
                return false;
            scope.Add(sourceName, new UnavailableVariableBinding(type));
            return true;
        }
        var stored = query;
        if (query.Source is SqlLinqHeapQuerySource heap)
        {
            _sql.Line($"DECLARE {sqlName} INT = {heap.OwnerSql};");
            stored = query with { Source = heap with { OwnerSql = sqlName } };
        }
        scope.Add(sourceName, new QueryVariableBinding(type, stored));
        return true;
    }

    private bool IsLinqQueryExpression(IrExpression expression, VariableScope scope) => expression switch
    {
        IrQueryExpression => true,
        IrVariableExpression variable when scope.Find(variable.Symbol) is QueryVariableBinding => true,
        IrInvocationExpression invocation when
            invocation.MethodName is "Range" or "Repeat" => true,
        IrInvocationExpression invocation when invocation.MethodName is
            "AsEnumerable" or "AsQueryable" or "Where" or "Select" or
            "OrderBy" or "OrderByDescending" or "ThenBy" or "ThenByDescending" or
            "Distinct" or "Skip" or "Take" or "GroupBy" or "Join" => true,
        IrInvocationExpression invocation when TryGetMethod(invocation, out var method) &&
            method.PureExpression is not null &&
            KnownTypeFacts.IsLinqSequence(method.ReturnType.Name) => true,
        _ => false
    };

    private bool TryBuildLinqQuery(
        IrExpression expression,
        VariableScope scope,
        IReadOnlyDictionary<string, Substitution>? substitutions,
        out SqlLinqQueryPlan query)
    {
        if (expression is IrVariableExpression variable)
        {
            if (TryGetLinqPlanSubstitution(variable.Symbol.Name, out query!)) return true;
            if (scope.Find(variable.Symbol) is QueryVariableBinding { Query: var stored })
            {
                query = stored;
                return true;
            }
        }
        if (expression is IrQueryExpression queryExpression)
            return TryBuildQueryExpression(queryExpression, scope, substitutions, out query);
        if (expression is IrInvocationExpression invocation && TryBuildVirtualLinqSource(invocation, scope, substitutions, out query))
            return true;
        if (expression is IrInvocationExpression
            {
                Target: IrMemberExpression materializationMember,
                Arguments.Count: 0
            } materialization && IntrinsicCatalog.IsMaterializer(materializationMember.MemberName))
        {
            string? collection = null;
            if (!TryEmitLinqMaterialization(
                    materialization,
                    scope,
                    context: null,
                    value => collection = value,
                    substitutions) || collection is null)
            {
                query = null!;
                return false;
            }
            var itemType = SequenceElementType(materialization.Type.Name);
            query = new SqlLinqQueryPlan(
                new SqlLinqHeapQuerySource(collection),
                itemType,
                itemType,
                []);
            return true;
        }

        if (expression is IrInvocationExpression userInvocation &&
            TryGetMethod(userInvocation, out var userMethod) &&
            userMethod.PureExpression is not null &&
            KnownTypeFacts.IsLinqSequence(userMethod.ReturnType.Name))
        {
            var arguments = InvocationArgumentExpressions(userInvocation, userMethod);
            if (arguments.Count != userMethod.Parameters.Count)
            {
                AddDiagnostic("SS3001", $"Method '{userMethod.Name}' expects {userMethod.Parameters.Count} arguments.", userInvocation.Source);
                query = null!;
                return false;
            }
            var scalarReplacements = substitutions is null
                ? new Dictionary<string, Substitution>(StringComparer.Ordinal)
                : substitutions.CopyToDictionary(StringComparer.Ordinal);
            var planReplacements = new Dictionary<string, SqlLinqQueryPlan>(StringComparer.Ordinal);
            var lambdaReplacements = new Dictionary<string, SqlLinqLambdaPlan>(StringComparer.Ordinal);
            for (var index = 0; index < arguments.Count; index++)
            {
                var parameter = userMethod.Parameters[index];
                var argument = arguments[index];
                if (TryBuildLinqLambda(argument, scope, substitutions, out var lambdaArgument))
                    lambdaReplacements[parameter.Name] = lambdaArgument;
                else if ((IsSequenceType(parameter.Type.Name) || KnownTypeFacts.IsLinqSequence(parameter.Type.Name)) &&
                         TryBuildLinqQuery(argument, scope, substitutions, out var argumentQuery))
                    planReplacements[parameter.Name] = argumentQuery;
                else
                    scalarReplacements[parameter.Name] = CaptureLinqMethodArgument(argument, scope, substitutions);
            }
            _linqPlanSubstitutions.Push(planReplacements);
            _linqLambdaSubstitutions.Push(lambdaReplacements);
            try
            {
                return TryBuildLinqQuery(userMethod.PureExpression, scope, scalarReplacements, out query);
            }
            finally
            {
                _linqLambdaSubstitutions.Pop();
                _linqPlanSubstitutions.Pop();
            }
        }

        if (expression is IrInvocationExpression { Target: IrMemberExpression member } chained)
        {
            var method = member.MemberName;
            if (method is "AsEnumerable" or "AsQueryable")
            {
                if (chained.Arguments.Count == 0)
                    return TryBuildLinqQuery(member.Receiver, scope, substitutions, out query);
                query = null!;
                return false;
            }
            if (method is "Where" or "Select" or "OrderBy" or "OrderByDescending" or "ThenBy" or "ThenByDescending")
            {
                if (!TryBuildLinqQuery(member.Receiver, scope, substitutions, out query) || chained.Arguments.Count != 1) return false;
                var kind = method switch
                {
                    "Where" => LinqQueryStepKind.Where,
                    "Select" => LinqQueryStepKind.Select,
                    "OrderBy" or "OrderByDescending" => LinqQueryStepKind.OrderBy,
                    _ => LinqQueryStepKind.ThenBy
                };
                return TryAppendLinqLambda(query, chained.Arguments[0], kind, scope, substitutions, false,
                    method.EndsWith("Descending", StringComparison.Ordinal), out query);
            }
            if (method == "Distinct" && chained.Arguments.Count == 0 && TryBuildLinqQuery(member.Receiver, scope, substitutions, out query))
            {
                query = query with { Steps = query.Steps.Append<SqlLinqQueryStep>(new SqlLinqDistinctQueryStep()).ToArray() };
                return true;
            }
            if (method is "Skip" or "Take" && chained.Arguments.Count == 1 && TryBuildLinqQuery(member.Receiver, scope, substitutions, out query))
            {
                query = query with
                {
                    Steps = query.Steps.Append<SqlLinqQueryStep>(new SqlLinqPagingQueryStep(
                        method == "Skip",
                        EmitScalar(chained.Arguments[0], scope, substitutions))).ToArray()
                };
                return true;
            }
            if (method == "GroupBy" && chained.Arguments.Count == 1 &&
                TryBuildLinqQuery(member.Receiver, scope, substitutions, out query) &&
                TryGetSingleParameterLambda(
                    chained.Arguments[0], scope, substitutions,
                    out var parameterName, out var keyBody, out var captures))
            {
                var keyType = keyBody.Type;
                var elementType = query.ElementType;
                query = query with
                {
                    ElementType = GroupingType(keyType, elementType),
                    Steps = query.Steps.Append<SqlLinqQueryStep>(new SqlLinqGroupQueryStep(
                        parameterName,
                        keyBody,
                        keyType,
                        elementType,
                        CaptureLinqSubstitutions(MergeLinqSubstitutions(captures, substitutions)))).ToArray()
                };
                return true;
            }
            if (method == "Join" && chained.Arguments.Count == 4 &&
                TryBuildLinqQuery(member.Receiver, scope, substitutions, out query) &&
                TryBuildLinqQuery(chained.Arguments[0], scope, substitutions, out var innerQuery) &&
                TryGetSingleParameterLambda(chained.Arguments[1], scope, substitutions, out var outerParameter, out var outerKey, out var outerCaptures) &&
                TryGetSingleParameterLambda(chained.Arguments[2], scope, substitutions, out var innerParameter, out var innerKey, out var innerCaptures) &&
                TryGetTwoParameterLambda(chained.Arguments[3], scope, substitutions, out var resultOuter, out var resultInner, out var resultBody, out var resultCaptures))
            {
                var resultType = resultBody.Type;
                query = query with
                {
                    ElementType = resultType,
                    Steps = query.Steps.Append<SqlLinqQueryStep>(new SqlLinqJoinQueryStep(
                        innerQuery,
                        outerParameter,
                        outerKey,
                        innerParameter,
                        innerKey,
                        outerKey.Type,
                        resultOuter,
                        resultInner,
                        resultBody,
                        resultType,
                        CaptureLinqSubstitutions(MergeLinqSubstitutions(outerCaptures, substitutions)),
                        CaptureLinqSubstitutions(MergeLinqSubstitutions(innerCaptures, substitutions)),
                        CaptureLinqSubstitutions(MergeLinqSubstitutions(resultCaptures, substitutions)))).ToArray()
                };
                return true;
            }
        }
        if (IsSequenceType(expression.Type.Name))
        {
            var itemType = SequenceElementType(expression.Type.Name);
            query = new SqlLinqQueryPlan(new SqlLinqHeapQuerySource(EmitScalar(expression, scope, substitutions)), itemType, itemType, []);
            return true;
        }
        query = null!;
        return false;
    }

    private bool TryBuildVirtualLinqSource(
        IrInvocationExpression invocation,
        VariableScope scope,
        IReadOnlyDictionary<string, Substitution>? substitutions,
        out SqlLinqQueryPlan query)
    {
        if (invocation.Target is not IrMemberExpression
            {
                Receiver: IrVariableExpression { Symbol.Name: "Enumerable" },
                MemberName: ("Range" or "Repeat") and var method
            } || invocation.Arguments.Count != 2)
        {
            query = null!;
            return false;
        }
        var first = _names.Allocate(method == "Range" ? "_range_start" : "_repeat_value");
        var count = _names.Allocate(method == "Range" ? "_range_count" : "_repeat_count");
        _sql.Line($"DECLARE {first} {invocation.Arguments[0].Type.SqlType()} = {EmitScalar(invocation.Arguments[0], scope, substitutions)};");
        _sql.Line($"DECLARE {count} INT = {EmitScalar(invocation.Arguments[1], scope, substitutions)};");
        if (method == "Range")
            _sql.Line($"IF {count} < 0 OR ({count} > 0 AND CONVERT(BIGINT, {first}) + CONVERT(BIGINT, {count}) - 1 > 2147483647) THROW 51006, 'Enumerable.Range arguments are out of range.', 1;");
        else
            _sql.Line($"IF {count} < 0 THROW 51006, 'Enumerable.Repeat count must be non-negative.', 1;");
        query = method == "Range"
            ? new SqlLinqQueryPlan(new SqlLinqRangeQuerySource(first, count), IrType.Int, IrType.Int, [])
            : new SqlLinqQueryPlan(new SqlLinqRepeatQuerySource(first, count), invocation.Arguments[0].Type, invocation.Arguments[0].Type, []);
        return true;
    }

    private bool TryEmitLinqDelegateDeclaration(
        ExpressionSyntax initializer,
        string sourceName,
        string sqlName,
        IrType type,
        VariableScope scope)
    {
        if (!TryBuildLinqLambda(initializer, scope, substitutions: null, out var lambda))
            return false;
        scope.Add(sourceName, new LambdaVariableBinding(type, lambda));
        return true;
    }

    private bool TryEmitLinqQueryDeclaration(
        ExpressionSyntax initializer,
        string sourceName,
        string sqlName,
        IrType type,
        VariableScope scope)
    {
        if (!IsLinqQueryExpression(initializer, scope))
            return false;

        if (!TryBuildLinqQuery(initializer, scope, substitutions: null, out var query))
        {
            scope.Add(sourceName, new UnavailableVariableBinding(type));
            return true;
        }

        var storedQuery = query;
        if (query.Source is SqlLinqHeapQuerySource heap)
        {
            _sql.Line($"DECLARE {sqlName} INT = {heap.OwnerSql};");
            storedQuery = query with { Source = heap with { OwnerSql = sqlName } };
        }
        scope.Add(sourceName, new QueryVariableBinding(type, storedQuery));
        return true;
    }

    private bool IsLinqQueryExpression(ExpressionSyntax expression, VariableScope scope)
    {
        expression = StripParentheses(expression);
        if (expression is QueryExpressionSyntax)
            return true;
        if (expression is IdentifierNameSyntax identifier &&
            scope.Find(identifier.Identifier.ValueText) is QueryVariableBinding)
            return true;
        if (expression is InvocationExpressionSyntax userInvocation &&
            TryGetMethod(userInvocation, out var userMethod) &&
            userMethod.PureExpression is not null &&
            IsLinqSequenceType(userMethod.ReturnType.Name))
            return true;
        if (expression is InvocationExpressionSyntax invocation &&
            (IsEnumerableRangeInvocation(invocation) || IsEnumerableRepeatInvocation(invocation)))
            return true;
        return expression is InvocationExpressionSyntax
        {
            Expression: MemberAccessExpressionSyntax member
        } && IntrinsicCatalog.IsDeferredLinqOperator(member.Name.Identifier.ValueText);
    }

    private bool TryBuildLinqQuery(
        ExpressionSyntax expression,
        VariableScope scope,
        IReadOnlyDictionary<string, Substitution>? substitutions,
        out SqlLinqQueryPlan query)
    {
        expression = StripParentheses(expression);
        if (expression is IdentifierNameSyntax substitutedIdentifier &&
            TryGetLinqPlanSubstitution(substitutedIdentifier.Identifier.ValueText, out var substitutedQuery))
        {
            query = substitutedQuery;
            return true;
        }
        if (expression is IdentifierNameSyntax identifier &&
            scope.Find(identifier.Identifier.ValueText) is QueryVariableBinding { Query: var storedQuery })
        {
            query = storedQuery;
            return true;
        }

        if (expression is QueryExpressionSyntax queryExpression)
            return TryBuildQueryExpression(queryExpression, scope, substitutions, out query);

        if (expression is InvocationExpressionSyntax rangeInvocation &&
            TryBuildEnumerableRangeQuery(rangeInvocation, scope, substitutions, out query))
            return true;

        if (expression is InvocationExpressionSyntax repeatInvocation &&
            TryBuildEnumerableRepeatQuery(repeatInvocation, scope, substitutions, out query))
            return true;

        if (expression is InvocationExpressionSyntax userInvocation &&
            TryGetMethod(userInvocation, out var userMethod) &&
            userMethod.PureExpression is not null &&
            IsLinqSequenceType(userMethod.ReturnType.Name))
        {
            var arguments = InvocationArgumentExpressions(userInvocation, userMethod);
            if (arguments.Count != userMethod.Parameters.Count)
            {
                AddDiagnostic("SS3001", $"Method '{userMethod.Name}' expects {userMethod.Parameters.Count} arguments.", userInvocation);
                query = null!;
                return false;
            }

            var scalarReplacements = substitutions is null
                ? new Dictionary<string, Substitution>(StringComparer.Ordinal)
                : substitutions.CopyToDictionary(StringComparer.Ordinal);
            var planReplacements = new Dictionary<string, SqlLinqQueryPlan>(StringComparer.Ordinal);
            var lambdaReplacements = new Dictionary<string, SqlLinqLambdaPlan>(StringComparer.Ordinal);
            for (var index = 0; index < arguments.Count; index++)
            {
                var parameter = userMethod.Parameters[index];
                var argument = arguments[index];
                if (TryBuildLinqLambda(argument, scope, substitutions, out var lambdaArgument))
                {
                    lambdaReplacements[parameter.Name] = lambdaArgument;
                    continue;
                }
                if ((IsSequenceType(parameter.Type.Name) || IsLinqSequenceType(parameter.Type.Name)) &&
                    TryBuildLinqQuery(argument, scope, substitutions, out var argumentQuery))
                {
                    planReplacements[parameter.Name] = argumentQuery;
                    continue;
                }
                scalarReplacements[parameter.Name] = CaptureLinqMethodArgument(
                    argument,
                    scope,
                    substitutions);
            }

            _linqPlanSubstitutions.Push(planReplacements);
            _linqLambdaSubstitutions.Push(lambdaReplacements);
            try
            {
                return TryBuildLinqQuery(CSharpExpression(userMethod.PureExpression), scope, scalarReplacements, out query);
            }
            finally
            {
                _linqLambdaSubstitutions.Pop();
                _linqPlanSubstitutions.Pop();
            }
        }

        if (expression is InvocationExpressionSyntax
            {
                Expression: MemberAccessExpressionSyntax member
            } invocation)
        {
            if (IsLinqMaterialization(invocation))
            {
                string? collection = null;
                if (!TryEmitLinqMaterialization(
                        invocation,
                        scope,
                        context: null,
                        value => collection = value,
                        substitutions) ||
                    collection is null)
                {
                    query = null!;
                    return false;
                }

                var collectionType = InferType(invocation, scope, substitutions);
                var itemType = SequenceElementType(collectionType.Name);
                query = new SqlLinqQueryPlan(
                    new SqlLinqHeapQuerySource(collection),
                    itemType,
                    itemType,
                    []);
                return true;
            }

            var method = member.Name.Identifier.ValueText;
            if (method is "AsEnumerable" or "AsQueryable")
            {
                if (invocation.ArgumentList.Arguments.Count != 0)
                {
                    AddDiagnostic("SS6401", $"Enumerable.{method} expects no arguments.", invocation);
                    query = null!;
                    return false;
                }
                return TryBuildLinqQuery(member.Expression, scope, substitutions, out query);
            }

            if (method is "Where" or "Select" or "OrderBy" or "OrderByDescending" or "ThenBy" or "ThenByDescending")
            {
                if (!TryBuildLinqQuery(member.Expression, scope, substitutions, out query))
                    return false;
                if (invocation.ArgumentList.Arguments.Count != 1)
                {
                    AddDiagnostic("SS6401", $"Enumerable.{method} expects one lambda.", invocation);
                    query = null!;
                    return false;
                }

                var kind = method switch
                {
                    "Where" => LinqQueryStepKind.Where,
                    "Select" => LinqQueryStepKind.Select,
                    "OrderBy" or "OrderByDescending" => LinqQueryStepKind.OrderBy,
                    _ => LinqQueryStepKind.ThenBy
                };
                return TryAppendLinqLambda(
                    query,
                    invocation.ArgumentList.Arguments[0].Expression,
                    kind,
                    scope,
                    substitutions,
                    negated: false,
                    descending: method.EndsWith("Descending", StringComparison.Ordinal),
                    out query);
            }

            if (method == "Distinct")
            {
                if (!TryBuildLinqQuery(member.Expression, scope, substitutions, out query))
                    return false;
                if (invocation.ArgumentList.Arguments.Count != 0)
                {
                    AddDiagnostic("SS6401", "Enumerable.Distinct expects no arguments.", invocation);
                    return false;
                }
                var steps = query.Steps.ToList();
                steps.Add(new SqlLinqDistinctQueryStep());
                query = query with { Steps = steps };
                return true;
            }

            if (method is "Skip" or "Take")
            {
                if (!TryBuildLinqQuery(member.Expression, scope, substitutions, out query))
                    return false;
                if (invocation.ArgumentList.Arguments.Count != 1)
                {
                    AddDiagnostic("SS6401", $"Enumerable.{method} expects one count argument.", invocation);
                    return false;
                }
                var steps = query.Steps.ToList();
                steps.Add(new SqlLinqPagingQueryStep(
                    method == "Skip",
                    EmitScalar(invocation.ArgumentList.Arguments[0].Expression, scope, substitutions)));
                query = query with { Steps = steps };
                return true;
            }

            if (method == "GroupBy")
            {
                if (!TryBuildLinqQuery(member.Expression, scope, substitutions, out query))
                    return false;
                if (invocation.ArgumentList.Arguments.Count != 1 ||
                    !TryGetSingleParameterLambda(
                        invocation.ArgumentList.Arguments[0].Expression,
                        scope,
                        out var parameterName,
                        out var keyBody,
                        out var lambdaCaptures))
                {
                    AddDiagnostic("SS6401", "Enumerable.GroupBy currently expects one key selector.", invocation);
                    return false;
                }
                var keyType = keyBody.Type;
                var elementType = query.ElementType;
                var groupingType = GroupingType(keyType, elementType);
                var steps = query.Steps.ToList();
                steps.Add(new SqlLinqGroupQueryStep(
                    parameterName,
                    keyBody,
                    keyType,
                    elementType,
                    CaptureLinqSubstitutions(MergeLinqSubstitutions(lambdaCaptures, substitutions))));
                query = query with { ElementType = groupingType, Steps = steps };
                return true;
            }

            if (method == "Join")
            {
                if (!TryBuildLinqQuery(member.Expression, scope, substitutions, out query))
                    return false;
                var arguments = invocation.ArgumentList.Arguments;
                if (arguments.Count != 4 ||
                    !TryBuildLinqQuery(arguments[0].Expression, scope, substitutions, out var innerQuery) ||
                    !TryGetSingleParameterLambda(arguments[1].Expression, scope, out var outerParameter, out var outerKey, out var outerCaptures) ||
                    !TryGetSingleParameterLambda(arguments[2].Expression, scope, out var innerParameter, out var innerKey, out var innerCaptures) ||
                    !TryGetTwoParameterLambda(arguments[3].Expression, scope, out var resultOuter, out var resultInner, out var resultBody, out var resultCaptures))
                {
                    AddDiagnostic("SS6401", "Enumerable.Join expects an inner sequence, two key selectors, and a two-parameter result selector.", invocation);
                    return false;
                }
                var keyType = outerKey.Type;
                var resultType = resultBody.Type;
                var steps = query.Steps.ToList();
                steps.Add(new SqlLinqJoinQueryStep(
                    innerQuery,
                    outerParameter,
                    outerKey,
                    innerParameter,
                    innerKey,
                    keyType,
                    resultOuter,
                    resultInner,
                    resultBody,
                    resultType,
                    CaptureLinqSubstitutions(MergeLinqSubstitutions(outerCaptures, substitutions)),
                    CaptureLinqSubstitutions(MergeLinqSubstitutions(innerCaptures, substitutions)),
                    CaptureLinqSubstitutions(MergeLinqSubstitutions(resultCaptures, substitutions))));
                query = query with { ElementType = resultType, Steps = steps };
                return true;
            }
        }

        var sequenceType = InferType(expression, scope, substitutions);
        if (IsSequenceType(sequenceType.Name))
        {
            var itemType = SequenceElementType(sequenceType.Name);
            query = new SqlLinqQueryPlan(
                new SqlLinqHeapQuerySource(EmitScalar(expression, scope, substitutions)),
                itemType,
                itemType,
                []);
            return true;
        }

        query = null!;
        return false;
    }

    private bool TryBuildEnumerableRangeQuery(
        InvocationExpressionSyntax invocation,
        VariableScope scope,
        IReadOnlyDictionary<string, Substitution>? substitutions,
        out SqlLinqQueryPlan query)
    {
        if (!IsEnumerableRangeInvocation(invocation))
        {
            query = null!;
            return false;
        }

        var arguments = invocation.ArgumentList.Arguments;
        var start = _names.Allocate("_range_start");
        var count = _names.Allocate("_range_count");
        _sql.Line($"DECLARE {start} INT = {EmitScalar(arguments[0].Expression, scope, substitutions)};");
        _sql.Line($"DECLARE {count} INT = {EmitScalar(arguments[1].Expression, scope, substitutions)};");
        _sql.Line(
            $"IF {count} < 0 OR ({count} > 0 AND CONVERT(BIGINT, {start}) + CONVERT(BIGINT, {count}) - 1 > 2147483647) " +
            "THROW 51006, 'Enumerable.Range arguments are out of range.', 1;");

        query = new SqlLinqQueryPlan(
            new SqlLinqRangeQuerySource(start, count),
            IrType.Int,
            IrType.Int,
            []);
        return true;
    }

    private bool IsEnumerableRangeInvocation(InvocationExpressionSyntax invocation)
    {
        if (invocation.ArgumentList.Arguments.Count != 2)
            return false;
        var method = SemanticModelFor(invocation)?.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
        return method is
        {
            Name: "Range",
            ContainingType.Name: "Enumerable",
            ContainingType.ContainingNamespace.Name: "Linq"
        };
    }

    private bool TryBuildEnumerableRepeatQuery(
        InvocationExpressionSyntax invocation,
        VariableScope scope,
        IReadOnlyDictionary<string, Substitution>? substitutions,
        out SqlLinqQueryPlan query)
    {
        if (!IsEnumerableRepeatInvocation(invocation))
        {
            query = null!;
            return false;
        }

        var arguments = invocation.ArgumentList.Arguments;
        var elementType = InferType(arguments[0].Expression, scope, substitutions);
        var value = _names.Allocate("_repeat_value");
        var count = _names.Allocate("_repeat_count");
        _sql.Line($"DECLARE {value} {elementType.SqlType()} = {EmitScalar(arguments[0].Expression, scope, substitutions)};");
        _sql.Line($"DECLARE {count} INT = {EmitScalar(arguments[1].Expression, scope, substitutions)};");
        _sql.Line($"IF {count} < 0 THROW 51006, 'Enumerable.Repeat count must be non-negative.', 1;");

        query = new SqlLinqQueryPlan(
            new SqlLinqRepeatQuerySource(value, count),
            elementType,
            elementType,
            []);
        return true;
    }

    private bool IsEnumerableRepeatInvocation(InvocationExpressionSyntax invocation)
    {
        if (invocation.ArgumentList.Arguments.Count != 2)
            return false;
        var method = SemanticModelFor(invocation)?.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
        return method is
        {
            Name: "Repeat",
            ContainingType.Name: "Enumerable",
            ContainingType.ContainingNamespace.Name: "Linq"
        };
    }

    private bool TryBuildQueryExpression(
        QueryExpressionSyntax expression,
        VariableScope scope,
        IReadOnlyDictionary<string, Substitution>? substitutions,
        out SqlLinqQueryPlan query)
    {
        if (!TryBuildLinqQuery(expression.FromClause.Expression, scope, substitutions, out query))
            return false;

        var rangeVariable = expression.FromClause.Identifier.ValueText;
        foreach (var clause in expression.Body.Clauses)
        {
            if (clause is WhereClauseSyntax where)
            {
                query = AppendLinqStep(
                    query,
                    LinqQueryStepKind.Where,
                    rangeVariable,
                    BindIrExpression(where.Condition, scope),
                    IrType.Bool,
                    negated: false,
                    captures: substitutions);
                continue;
            }

            if (clause is OrderByClauseSyntax orderBy)
            {
                for (var index = 0; index < orderBy.Orderings.Count; index++)
                {
                    var ordering = orderBy.Orderings[index];
                    query = AppendLinqStep(
                        query,
                        index == 0 ? LinqQueryStepKind.OrderBy : LinqQueryStepKind.ThenBy,
                        rangeVariable,
                        BindIrExpression(ordering.Expression, scope),
                        InferType(ordering.Expression, scope, substitutions),
                        negated: false,
                        descending: ordering.AscendingOrDescendingKeyword.ValueText == "descending",
                        captures: substitutions);
                }
                continue;
            }

            AddDiagnostic("SS6410", "Query expressions currently support where, orderby, select, and identity group clauses.", clause);
            query = null!;
            return false;
        }

        if (expression.Body.SelectOrGroup is SelectClauseSyntax select)
        {
            query = AppendLinqStep(
                query,
                LinqQueryStepKind.Select,
                rangeVariable,
                BindIrExpression(select.Expression, scope),
                InferType(select.Expression, scope, substitutions),
                negated: false,
                captures: substitutions);
        }
        else if (expression.Body.SelectOrGroup is GroupClauseSyntax group &&
                 group.GroupExpression is IdentifierNameSyntax groupedIdentifier &&
                 groupedIdentifier.Identifier.ValueText == rangeVariable)
        {
            var keyType = InferType(group.ByExpression, scope, substitutions);
            var elementType = query.ElementType;
            var steps = query.Steps.ToList();
            steps.Add(new SqlLinqGroupQueryStep(
                rangeVariable,
                BindIrExpression(group.ByExpression, scope),
                keyType,
                elementType,
                CaptureLinqSubstitutions(substitutions)));
            query = query with
            {
                ElementType = GroupingType(keyType, elementType),
                Steps = steps
            };
        }
        else
        {
            AddDiagnostic("SS6410", "Query group clauses currently require 'group item by key'.", expression.Body.SelectOrGroup);
            query = null!;
            return false;
        }
        if (expression.Body.Continuation is not null)
        {
            AddDiagnostic("SS6410", "Query continuations are not supported yet.", expression.Body.Continuation);
            query = null!;
            return false;
        }

        return true;
    }

    private bool TryBuildQueryExpression(
        IrQueryExpression expression,
        VariableScope scope,
        IReadOnlyDictionary<string, Substitution>? substitutions,
        out SqlLinqQueryPlan query)
    {
        if (!TryBuildLinqQuery(expression.SourceExpression, scope, substitutions, out query))
            return false;
        var rangeName = expression.RangeVariable.Name;
        foreach (var clause in expression.Clauses)
        {
            switch (clause)
            {
                case IrWhereClause where:
                    query = AppendLinqStep(
                        query, LinqQueryStepKind.Where, rangeName, where.Predicate,
                        IrType.Bool, false, captures: substitutions);
                    break;
                case IrOrderClause order:
                    query = AppendLinqStep(
                        query,
                        order.IsThenBy ? LinqQueryStepKind.ThenBy : LinqQueryStepKind.OrderBy,
                        rangeName,
                        order.Key,
                        order.Key.Type,
                        false,
                        order.Descending,
                        substitutions);
                    break;
                case IrSelectClause select:
                    query = AppendLinqStep(
                        query, LinqQueryStepKind.Select, rangeName, select.Projection,
                        select.Projection.Type, false, captures: substitutions);
                    break;
                case IrGroupClause group when
                    group.Element is IrVariableExpression grouped &&
                    (grouped.Symbol.Id == expression.RangeVariable.Id || grouped.Symbol.Name == rangeName):
                    var elementType = query.ElementType;
                    query = query with
                    {
                        ElementType = GroupingType(group.Key.Type, elementType),
                        Steps = query.Steps.Append<SqlLinqQueryStep>(new SqlLinqGroupQueryStep(
                            rangeName,
                            group.Key,
                            group.Key.Type,
                            elementType,
                            CaptureLinqSubstitutions(substitutions))).ToArray()
                    };
                    break;
                default:
                    AddDiagnostic("SS6410", "Unsupported IR query clause.", clause.Source);
                    query = null!;
                    return false;
            }
        }
        return true;
    }

    private bool TryAppendLinqLambda(
        SqlLinqQueryPlan query,
        ExpressionSyntax lambda,
        LinqQueryStepKind kind,
        VariableScope scope,
        IReadOnlyDictionary<string, Substitution>? substitutions,
        bool negated,
        bool descending,
        out SqlLinqQueryPlan result)
    {
        if (!TryGetSingleParameterLambda(lambda, scope, out var parameterName, out var body, out var lambdaCaptures))
        {
            AddDiagnostic("SS6402", "LINQ operators currently require an expression lambda with one parameter.", lambda);
            result = query;
            return false;
        }

        var resultType = kind == LinqQueryStepKind.Where ? query.ElementType : body.Type;
        result = AppendLinqStep(
            query,
            kind,
            parameterName,
            body,
            resultType,
            negated,
            descending,
            MergeLinqSubstitutions(lambdaCaptures, substitutions));
        return true;
    }

    private bool TryAppendLinqLambda(
        SqlLinqQueryPlan query,
        IrExpression lambdaExpression,
        LinqQueryStepKind kind,
        VariableScope scope,
        IReadOnlyDictionary<string, Substitution>? substitutions,
        bool negated,
        bool descending,
        out SqlLinqQueryPlan result)
    {
        if (!TryBuildLinqLambda(lambdaExpression, scope, substitutions, out var lambda) ||
            lambda.Expression is not IrLambdaExpression { Parameters.Count: 1, ExpressionBody: not null } body)
        {
            AddDiagnostic("SS6402", "LINQ operators currently require an expression lambda with one parameter.", lambdaExpression.Source);
            result = query;
            return false;
        }
        var resultType = kind == LinqQueryStepKind.Where ? query.ElementType : body.ExpressionBody.Type;
        result = AppendLinqStep(
            query, kind, body.Parameters[0].Name, body.ExpressionBody, resultType, negated, descending,
            MergeLinqSubstitutions(RestoreLinqSubstitutions(lambda.Captures), substitutions));
        return true;
    }

    private static SqlLinqQueryPlan AppendLinqStep(
        SqlLinqQueryPlan query,
        LinqQueryStepKind kind,
        string parameterName,
        IrExpression body,
        IrType resultType,
        bool negated,
        bool descending = false,
        IReadOnlyDictionary<string, Substitution>? captures = null)
    {
        var steps = query.Steps.ToList();
        steps.Add(new SqlLinqLambdaQueryStep(
            kind,
            parameterName,
            body,
            resultType,
            negated,
            descending,
            CaptureLinqSubstitutions(captures)));
        return query with
        {
            ElementType = kind == LinqQueryStepKind.Select ? resultType : query.ElementType,
            Steps = steps
        };
    }

    private string RenderLinqQuery(
        SqlLinqQueryPlan query,
        VariableScope scope,
        IReadOnlyDictionary<string, Substitution>? substitutions)
    {
        var sql = RenderLinqSource(query, LeadingVirtualSourceTakeCount(query));

        var currentType = query.SourceElementType;
        for (var stepIndex = 0; stepIndex < query.Steps.Count; stepIndex++)
        {
            var step = query.Steps[stepIndex];
            if (step is SqlLinqLambdaQueryStep lambda && lambda.Kind is LinqQueryStepKind.Where or LinqQueryStepKind.Select)
            {
                var sourceAlias = NextLinqAlias("source");
                var replacements = LinqReplacements(
                    MergeLinqSubstitutions(RestoreLinqSubstitutions(lambda.Captures), substitutions),
                    lambda.ParameterName,
                    $"{sourceAlias}.__value",
                    currentType);
                if (lambda.Kind == LinqQueryStepKind.Where)
                {
                    var predicate = EmitPredicate(lambda.Body, scope, replacements);
                    if (lambda.Negated)
                        predicate = $"NOT ({predicate})";
                    sql = $"SELECT {sourceAlias}.__index, {sourceAlias}.__value FROM ({sql}) AS {sourceAlias} WHERE {predicate}";
                }
                else
                {
                    var value = EmitScalar(lambda.Body, scope, replacements);
                    sql = $"SELECT {sourceAlias}.__index, {value} AS __value FROM ({sql}) AS {sourceAlias}";
                    currentType = lambda.ResultType;
                }
                continue;
            }

            if (step is SqlLinqLambdaQueryStep { Kind: LinqQueryStepKind.OrderBy or LinqQueryStepKind.ThenBy })
            {
                var orders = new List<SqlLinqLambdaQueryStep>
                {
                    (SqlLinqLambdaQueryStep)step
                };
                stepIndex++;
                while (stepIndex < query.Steps.Count &&
                       query.Steps[stepIndex] is SqlLinqLambdaQueryStep
                       {
                           Kind: LinqQueryStepKind.ThenBy
                       } thenBy)
                {
                    orders.Add(thenBy);
                    stepIndex++;
                }
                stepIndex--;

                var sourceAlias = NextLinqAlias("order_source");
                var keySql = new List<(string Name, IrType Type, bool Descending)>();
                var projections = new List<string>();
                for (var orderIndex = 0; orderIndex < orders.Count; orderIndex++)
                {
                    var order = orders[orderIndex];
                    var replacements = LinqReplacements(
                        MergeLinqSubstitutions(RestoreLinqSubstitutions(order.Captures), substitutions),
                        order.ParameterName,
                        $"{sourceAlias}.__value",
                        currentType);
                    var name = $"__key_{orderIndex}";
                    projections.Add($"{EmitScalar(order.Body, scope, replacements)} AS {name}");
                    keySql.Add((name, order.ResultType, order.Descending));
                }

                var keyedAlias = NextLinqAlias("ordered_keys");
                var keyed = $"SELECT {sourceAlias}.__index, {sourceAlias}.__value, {string.Join(", ", projections)} FROM ({sql}) AS {sourceAlias}";
                var orderBy = string.Join(", ", keySql.Select(key =>
                    LinqOrderValue($"{keyedAlias}.{key.Name}", key.Type) + (key.Descending ? " DESC" : " ASC")));
                sql = $"SELECT CONVERT(INT, ROW_NUMBER() OVER (ORDER BY {orderBy}, {keyedAlias}.__index) - 1) AS __index, {keyedAlias}.__value FROM ({keyed}) AS {keyedAlias}";
                continue;
            }

            if (step is SqlLinqDistinctQueryStep)
            {
                var sourceAlias = NextLinqAlias("distinct_source");
                var groupedAlias = NextLinqAlias("distinct_group");
                var value = LinqEqualityValue($"{sourceAlias}.__value", currentType);
                var grouped = $"SELECT {value} AS __value, MIN({sourceAlias}.__index) AS __first_index FROM ({sql}) AS {sourceAlias} GROUP BY {value}";
                sql = $"SELECT CONVERT(INT, ROW_NUMBER() OVER (ORDER BY {groupedAlias}.__first_index) - 1) AS __index, {groupedAlias}.__value FROM ({grouped}) AS {groupedAlias}";
                continue;
            }

            if (step is SqlLinqPagingQueryStep paging)
            {
                var sourceAlias = NextLinqAlias("page_source");
                var numberedAlias = NextLinqAlias("page_numbered");
                var count = paging.CountSql;
                var normalizedCount = $"CASE WHEN {count} < 0 THEN 0 ELSE {count} END";
                if (paging.IsSkip)
                {
                    var numbered = $"SELECT ROW_NUMBER() OVER (ORDER BY {sourceAlias}.__index) - 1 AS __ordinal, {sourceAlias}.__value FROM ({sql}) AS {sourceAlias}";
                    sql = $"SELECT CONVERT(INT, {numberedAlias}.__ordinal - ({normalizedCount})) AS __index, {numberedAlias}.__value FROM ({numbered}) AS {numberedAlias} WHERE {numberedAlias}.__ordinal >= ({normalizedCount})";
                }
                else
                {
                    var limited = $"SELECT TOP ({normalizedCount}) {sourceAlias}.__index, {sourceAlias}.__value FROM ({sql}) AS {sourceAlias} ORDER BY {sourceAlias}.__index";
                    sql = $"SELECT CONVERT(INT, ROW_NUMBER() OVER (ORDER BY {numberedAlias}.__index) - 1) AS __index, {numberedAlias}.__value FROM ({limited}) AS {numberedAlias}";
                }
                continue;
            }

            if (step is SqlLinqGroupQueryStep group)
            {
                var sourceAlias = NextLinqAlias("group_source");
                var replacements = LinqReplacements(
                    MergeLinqSubstitutions(RestoreLinqSubstitutions(group.Captures), substitutions),
                    group.ParameterName,
                    $"{sourceAlias}.__value",
                    currentType);
                var projectedAlias = NextLinqAlias("group_keys");
                var groupedAlias = NextLinqAlias("groups");
                var key = EmitScalar(group.KeyBody, scope, replacements);
                var projected = $"SELECT {sourceAlias}.__index, {key} AS __key FROM ({sql}) AS {sourceAlias}";
                var equalityKey = LinqEqualityValue($"{projectedAlias}.__key", group.KeyType);
                var grouped = $"SELECT {equalityKey} AS __value, MIN({projectedAlias}.__index) AS __first_index FROM ({projected}) AS {projectedAlias} GROUP BY {equalityKey}";
                sql = $"SELECT CONVERT(INT, ROW_NUMBER() OVER (ORDER BY {groupedAlias}.__first_index) - 1) AS __index, {groupedAlias}.__value FROM ({grouped}) AS {groupedAlias}";
                currentType = GroupingType(group.KeyType, group.ElementType);
                continue;
            }

            if (step is SqlLinqJoinQueryStep join)
            {
                var outerAlias = NextLinqAlias("join_outer");
                var innerAlias = NextLinqAlias("join_inner");
                var innerSql = RenderLinqQuery(join.InnerQuery, scope, substitutions);
                var outerKeyReplacements = LinqReplacements(
                    MergeLinqSubstitutions(RestoreLinqSubstitutions(join.OuterCaptures), substitutions),
                    join.OuterParameterName,
                    $"{outerAlias}.__value",
                    currentType);
                var innerKeyReplacements = LinqReplacements(
                    MergeLinqSubstitutions(RestoreLinqSubstitutions(join.InnerCaptures), substitutions),
                    join.InnerParameterName,
                    $"{innerAlias}.__value",
                    join.InnerQuery.ElementType);
                var outerKey = EmitScalar(join.OuterKeyBody, scope, outerKeyReplacements);
                var innerKey = EmitScalar(join.InnerKeyBody, scope, innerKeyReplacements);
                var joinSubstitutions = MergeLinqSubstitutions(
                    RestoreLinqSubstitutions(join.ResultCaptures),
                    substitutions);
                var resultReplacements = joinSubstitutions is null
                    ? new Dictionary<string, Substitution>(StringComparer.Ordinal)
                    : joinSubstitutions.CopyToDictionary(StringComparer.Ordinal);
                resultReplacements[join.ResultOuterParameterName] = new Substitution(
                    SqlScalarExpression.Primary($"{outerAlias}.__value", currentType));
                resultReplacements[join.ResultInnerParameterName] = new Substitution(
                    SqlScalarExpression.Primary($"{innerAlias}.__value", join.InnerQuery.ElementType));
                var result = EmitScalar(join.ResultBody, scope, resultReplacements);
                var joinedAlias = NextLinqAlias("joined");
                var joined = $"SELECT {outerAlias}.__index AS __outer_index, {innerAlias}.__index AS __inner_index, {result} AS __value FROM ({sql}) AS {outerAlias} INNER JOIN ({innerSql}) AS {innerAlias} ON {LinqJoinEquality(outerKey, innerKey, join.KeyType)}";
                sql = $"SELECT CONVERT(INT, ROW_NUMBER() OVER (ORDER BY {joinedAlias}.__outer_index, {joinedAlias}.__inner_index) - 1) AS __index, {joinedAlias}.__value FROM ({joined}) AS {joinedAlias}";
                currentType = join.ResultType;
            }
        }
        return sql;
    }

    private static string? LeadingVirtualSourceTakeCount(SqlLinqQueryPlan query)
    {
        if (query.Source is not (SqlLinqRangeQuerySource or SqlLinqRepeatQuerySource))
            return null;

        foreach (var step in query.Steps)
        {
            if (step is SqlLinqPagingQueryStep { IsSkip: false } take)
                return take.CountSql;
            if (step is not SqlLinqLambdaQueryStep { Kind: LinqQueryStepKind.Select })
                return null;
        }
        return null;
    }

    private string RenderLinqSource(SqlLinqQueryPlan query, string? takeCount)
    {
        if (query.Source is SqlLinqHeapQuerySource heap)
        {
            var itemAlias = NextLinqAlias("item");
            return $"SELECT {itemAlias}.__index AS __index, " +
                $"{CollectionReadValue(query.SourceElementType, key: false, qualifier: itemAlias)} AS __value " +
                $"FROM {HeapIndexedItems} AS {itemAlias} WHERE {HeapExecutionFilter(itemAlias)}{itemAlias}.__owner_id = {heap.OwnerSql}";
        }

        if (query.Source is SqlLinqRangeQuerySource range)
        {
            var rangeAlias = NextLinqAlias("range");
            var start = $"CONVERT(BIGINT, {range.StartSql})";
            var count = $"CONVERT(BIGINT, {range.CountSql})";
            var generatedCount = count;
            if (takeCount is not null)
            {
                var normalizedTake = $"CONVERT(BIGINT, CASE WHEN {takeCount} < 0 THEN 0 ELSE {takeCount} END)";
                generatedCount = $"CASE WHEN {normalizedTake} < {count} THEN {normalizedTake} ELSE {count} END";
            }
            return $"SELECT CONVERT(INT, {rangeAlias}.[value] - {start}) AS __index, " +
                $"CONVERT(INT, {rangeAlias}.[value]) AS __value " +
                $"FROM GENERATE_SERIES({start}, {start} + ({generatedCount}) - 1, CONVERT(BIGINT, 1)) AS {rangeAlias}";
        }

        if (query.Source is SqlLinqRepeatQuerySource repeat)
        {
            var repeatAlias = NextLinqAlias("repeat");
            var count = $"CONVERT(BIGINT, {repeat.CountSql})";
            var generatedCount = count;
            if (takeCount is not null)
            {
                var normalizedTake = $"CONVERT(BIGINT, CASE WHEN {takeCount} < 0 THEN 0 ELSE {takeCount} END)";
                generatedCount = $"CASE WHEN {normalizedTake} < {count} THEN {normalizedTake} ELSE {count} END";
            }
            return $"SELECT CONVERT(INT, {repeatAlias}.[value]) AS __index, " +
                $"{repeat.ValueSql} AS __value " +
                $"FROM GENERATE_SERIES(CONVERT(BIGINT, 0), ({generatedCount}) - 1, CONVERT(BIGINT, 1)) AS {repeatAlias}";
        }

        if (query.Source is SqlLinqTaskResultQuerySource taskResults)
        {
            var idsAlias = NextLinqAlias("task_ids");
            var tasksAlias = NextLinqAlias("tasks");
            var result = query.SourceElementType.IsReference
                ? $"CONVERT(INT, {tasksAlias}.[ResultReferenceId])"
                : query.SourceElementType.IsString
                    ? $"{tasksAlias}.[ResultText]"
                    : query.SourceElementType.Name == "byte[]"
                        ? $"{tasksAlias}.[ResultBinary]"
                        : $"CONVERT({query.SourceElementType.SqlType()}, {tasksAlias}.[ResultScalar])";
            return $"SELECT CONVERT(INT, {idsAlias}.[key]) AS __index, {result} AS __value " +
                $"FROM OPENJSON({taskResults.TaskIdsJsonSql}) AS {idsAlias} " +
                $"INNER JOIN [SharpSql].[Tasks] AS {tasksAlias} " +
                $"ON {tasksAlias}.[ExecutionId] = {taskResults.ExecutionIdSql} " +
                $"AND {tasksAlias}.[TaskId] = CONVERT(BIGINT, {idsAlias}.[value]) " +
                $"WHERE {tasksAlias}.[State] = 4";
        }

        throw new InvalidOperationException($"Unknown LINQ source '{query.Source.GetType().Name}'.");
    }

    private bool TryEmitRawLinqCardinality(
        SqlLinqQueryPlan query,
        string method,
        out SqlScalarExpression expression)
    {
        expression = null!;
        if (query.Steps.Count != 0 || method is not ("Count" or "LongCount" or "Any"))
            return false;

        var count = query.Source switch
        {
            SqlLinqHeapQuerySource heap =>
                $"(SELECT __count FROM {HeapObjects} WHERE {HeapExecutionFilter()}__id = {heap.OwnerSql})",
            SqlLinqRangeQuerySource range => range.CountSql,
            SqlLinqRepeatQuerySource repeat => repeat.CountSql,
            _ => null
        };
        if (count is null)
            return false;

        expression = method switch
        {
            "Count" => SqlScalarExpression.Primary($"CONVERT(INT, {count})"),
            "LongCount" => SqlScalarExpression.Primary($"CONVERT(BIGINT, {count})"),
            "Any" => SqlScalarExpression.Primary(
                $"CASE WHEN {count} > 0 THEN CAST(1 AS BIT) ELSE CAST(0 AS BIT) END"),
            _ => null!
        };
        return true;
    }

    private bool TryEmitLinqInvocation(
        InvocationExpressionSyntax invocation,
        VariableScope scope,
        IReadOnlyDictionary<string, Substitution>? substitutions,
        out SqlScalarExpression expression)
    {
        expression = null!;
        if (invocation.Expression is not MemberAccessExpressionSyntax member)
            return false;

        var method = member.Name.Identifier.ValueText;
        if (method is not (
            "Sum" or "Count" or "LongCount" or "Any" or "All" or "Contains" or
            "FirstOrDefault" or "LastOrDefault" or "ElementAtOrDefault"))
            return false;
        if (!TryBuildLinqQuery(member.Expression, scope, substitutions, out var query))
            return false;
        if (IsGroupingType(query.ElementType.Name) &&
            method is not ("Count" or "LongCount" or "Any" or "All"))
        {
            AddDiagnostic(
                "SS6411",
                "This LINQ terminal requires full IGrouping values; project group.Key before consuming the sequence.",
                invocation);
            expression = SqlScalarExpression.Primary("NULL");
            return true;
        }

        var arguments = invocation.ArgumentList.Arguments;
        if (method is "Count" or "LongCount" or "Any")
        {
            if (arguments.Count > 1 ||
                (arguments.Count == 1 && !TryAppendLinqLambda(
                    query,
                    arguments[0].Expression,
                    LinqQueryStepKind.Where,
                    scope,
                    substitutions,
                    negated: false,
                    descending: false,
                    out query)))
            {
                if (arguments.Count > 1)
                    AddDiagnostic("SS6401", $"Enumerable.{method} expects zero arguments or one predicate.", invocation);
                expression = SqlScalarExpression.Primary("NULL");
                return true;
            }
        }
        else if (method == "All")
        {
            if (arguments.Count != 1 || !TryAppendLinqLambda(
                    query,
                    arguments[0].Expression,
                    LinqQueryStepKind.Where,
                    scope,
                    substitutions,
                    negated: true,
                    descending: false,
                    out query))
            {
                if (arguments.Count != 1)
                    AddDiagnostic("SS6401", "Enumerable.All expects one predicate.", invocation);
                expression = SqlScalarExpression.Primary("NULL");
                return true;
            }
        }
        else if (method == "Sum")
        {
            if (arguments.Count > 1 ||
                (arguments.Count == 1 && !TryAppendLinqLambda(
                    query,
                    arguments[0].Expression,
                    LinqQueryStepKind.Select,
                    scope,
                    substitutions,
                    negated: false,
                    descending: false,
                    out query)))
            {
                if (arguments.Count > 1)
                    AddDiagnostic("SS6401", "Enumerable.Sum expects zero arguments or one selector.", invocation);
                expression = SqlScalarExpression.Primary("NULL");
                return true;
            }
            if (!IsSumType(query.ElementType))
            {
                AddDiagnostic("SS6403", $"Enumerable.Sum does not support selector type '{query.ElementType.Name}'.", invocation);
                expression = SqlScalarExpression.Primary("NULL");
                return true;
            }
        }
        else if (method == "Contains")
        {
            if (arguments.Count != 1)
            {
                AddDiagnostic("SS6401", "Enumerable.Contains expects one argument.", invocation);
                expression = SqlScalarExpression.Primary("NULL");
                return true;
            }
        }
        else if (method is "FirstOrDefault" or "LastOrDefault")
        {
            if (arguments.Count > 1 ||
                (arguments.Count == 1 && !TryAppendLinqLambda(
                    query,
                    arguments[0].Expression,
                    LinqQueryStepKind.Where,
                    scope,
                    substitutions,
                    negated: false,
                    descending: false,
                    out query)))
            {
                if (arguments.Count > 1)
                    AddDiagnostic("SS6401", $"Enumerable.{method} expects zero arguments or one predicate.", invocation);
                expression = SqlScalarExpression.Primary("NULL");
                return true;
            }
        }
        else if (method == "ElementAtOrDefault" && arguments.Count != 1)
        {
            AddDiagnostic("SS6401", "Enumerable.ElementAtOrDefault expects one index.", invocation);
            expression = SqlScalarExpression.Primary("NULL");
            return true;
        }

        if (arguments.Count == 0 && TryEmitRawLinqCardinality(query, method, out expression))
            return true;

        var querySql = RenderLinqQuery(query, scope, substitutions);
        var terminalAlias = NextLinqAlias("terminal");
        switch (method)
        {
            case "Count":
                expression = SqlScalarExpression.Primary($"(SELECT COUNT(*) FROM ({querySql}) AS {terminalAlias})");
                return true;
            case "LongCount":
                expression = SqlScalarExpression.Primary($"(SELECT COUNT_BIG(*) FROM ({querySql}) AS {terminalAlias})");
                return true;
            case "Any":
                expression = SqlScalarExpression.Primary($"CASE WHEN EXISTS (SELECT 1 FROM ({querySql}) AS {terminalAlias}) THEN CAST(1 AS BIT) ELSE CAST(0 AS BIT) END");
                return true;
            case "All":
                expression = SqlScalarExpression.Primary($"CASE WHEN NOT EXISTS (SELECT 1 FROM ({querySql}) AS {terminalAlias}) THEN CAST(1 AS BIT) ELSE CAST(0 AS BIT) END");
                return true;
            case "Sum":
                var resultType = InferType(invocation, scope, substitutions);
                if (resultType == IrType.Unknown)
                    resultType = query.ElementType;
                expression = SqlScalarExpression.Primary(
                    $"COALESCE((SELECT SUM({terminalAlias}.__value) FROM ({querySql}) AS {terminalAlias}), CAST(0 AS {resultType.SqlType()}))");
                return true;
            case "Contains":
                var value = EmitScalar(arguments[0].Expression, scope, substitutions);
                var equality = LinqValueEquality($"{terminalAlias}.__value", value, query.ElementType);
                expression = SqlScalarExpression.Primary(
                    $"CASE WHEN EXISTS (SELECT 1 FROM ({querySql}) AS {terminalAlias} WHERE {equality}) THEN CAST(1 AS BIT) ELSE CAST(0 AS BIT) END");
                return true;
            case "FirstOrDefault":
                expression = SqlScalarExpression.Primary(
                    $"COALESCE((SELECT TOP (1) {terminalAlias}.__value FROM ({querySql}) AS {terminalAlias} ORDER BY {terminalAlias}.__index), {DefaultSql(query.ElementType)})");
                return true;
            case "LastOrDefault":
                expression = SqlScalarExpression.Primary(
                    $"COALESCE((SELECT TOP (1) {terminalAlias}.__value FROM ({querySql}) AS {terminalAlias} ORDER BY {terminalAlias}.__index DESC), {DefaultSql(query.ElementType)})");
                return true;
            case "ElementAtOrDefault":
                var index = EmitScalar(arguments[0].Expression, scope, substitutions);
                expression = SqlScalarExpression.Primary(
                    $"COALESCE({LinqElementAtSql(querySql, terminalAlias, index)}, {DefaultSql(query.ElementType)})");
                return true;
            default:
                return false;
        }
    }

    private bool TryEmitLinqInvocation(
        IrInvocationExpression invocation,
        VariableScope scope,
        IReadOnlyDictionary<string, Substitution>? substitutions,
        out SqlScalarExpression expression)
    {
        expression = null!;
        if (invocation.Target is not IrMemberExpression member ||
            member.MemberName is not ("Sum" or "Count" or "LongCount" or "Any" or "All" or "Contains" or
                "FirstOrDefault" or "LastOrDefault" or "ElementAtOrDefault"))
            return false;
        var method = member.MemberName;
        if (!TryBuildLinqQuery(member.Receiver, scope, substitutions, out var query)) return false;
        var arguments = invocation.Arguments;
        if (method is "Count" or "LongCount" or "Any" or "FirstOrDefault" or "LastOrDefault")
        {
            if (arguments.Count > 1 || arguments.Count == 1 &&
                !TryAppendLinqLambda(query, arguments[0], LinqQueryStepKind.Where, scope, substitutions, false, false, out query))
            {
                AddDiagnostic("SS6401", $"Enumerable.{method} expects zero arguments or one predicate.", invocation.Source);
                expression = SqlScalarExpression.Primary("NULL");
                return true;
            }
        }
        else if (method == "All")
        {
            if (arguments.Count != 1 || !TryAppendLinqLambda(
                    query, arguments[0], LinqQueryStepKind.Where, scope, substitutions, true, false, out query))
            {
                AddDiagnostic("SS6401", "Enumerable.All expects one predicate.", invocation.Source);
                expression = SqlScalarExpression.Primary("NULL");
                return true;
            }
        }
        else if (method == "Sum")
        {
            if (arguments.Count > 1 || arguments.Count == 1 &&
                !TryAppendLinqLambda(query, arguments[0], LinqQueryStepKind.Select, scope, substitutions, false, false, out query))
            {
                AddDiagnostic("SS6401", "Enumerable.Sum expects zero arguments or one selector.", invocation.Source);
                expression = SqlScalarExpression.Primary("NULL");
                return true;
            }
        }
        else if (method is "Contains" or "ElementAtOrDefault" && arguments.Count != 1)
        {
            AddDiagnostic("SS6401", $"Enumerable.{method} expects one argument.", invocation.Source);
            expression = SqlScalarExpression.Primary("NULL");
            return true;
        }

        if (arguments.Count == 0 && TryEmitRawLinqCardinality(query, method, out expression))
            return true;

        var querySql = RenderLinqQuery(query, scope, substitutions);
        var alias = NextLinqAlias("terminal");
        expression = method switch
        {
            "Count" => SqlScalarExpression.Primary($"(SELECT COUNT(*) FROM ({querySql}) AS {alias})"),
            "LongCount" => SqlScalarExpression.Primary($"(SELECT COUNT_BIG(*) FROM ({querySql}) AS {alias})"),
            "Any" => SqlScalarExpression.Primary($"CASE WHEN EXISTS (SELECT 1 FROM ({querySql}) AS {alias}) THEN CAST(1 AS BIT) ELSE CAST(0 AS BIT) END"),
            "All" => SqlScalarExpression.Primary($"CASE WHEN NOT EXISTS (SELECT 1 FROM ({querySql}) AS {alias}) THEN CAST(1 AS BIT) ELSE CAST(0 AS BIT) END"),
            "Sum" => SqlScalarExpression.Primary($"COALESCE((SELECT SUM({alias}.__value) FROM ({querySql}) AS {alias}), CAST(0 AS {invocation.Type.SqlType()}))"),
            "Contains" => SqlScalarExpression.Primary(
                $"CASE WHEN EXISTS (SELECT 1 FROM ({querySql}) AS {alias} WHERE {LinqValueEquality($"{alias}.__value", EmitScalar(arguments[0], scope, substitutions), query.ElementType)}) THEN CAST(1 AS BIT) ELSE CAST(0 AS BIT) END"),
            "FirstOrDefault" => SqlScalarExpression.Primary(
                $"COALESCE((SELECT TOP (1) {alias}.__value FROM ({querySql}) AS {alias} ORDER BY {alias}.__index), {DefaultSql(query.ElementType)})"),
            "LastOrDefault" => SqlScalarExpression.Primary(
                $"COALESCE((SELECT TOP (1) {alias}.__value FROM ({querySql}) AS {alias} ORDER BY {alias}.__index DESC), {DefaultSql(query.ElementType)})"),
            "ElementAtOrDefault" => SqlScalarExpression.Primary(
                $"COALESCE({LinqElementAtSql(querySql, alias, EmitScalar(arguments[0], scope, substitutions))}, {DefaultSql(query.ElementType)})"),
            _ => null!
        };
        return true;
    }

    private bool ContainsGuardedLinqExpression(ExpressionSyntax expression) =>
        expression.DescendantNodesAndSelf()
            .OfType<InvocationExpressionSyntax>()
            .Any(IsGuardedLinqInvocation);

    private bool IsGuardedLinqInvocation(InvocationExpressionSyntax invocation)
    {
        if (invocation.Expression is not MemberAccessExpressionSyntax member ||
            !IntrinsicCatalog.IsGuardedLinqOperator(member.Name.Identifier.ValueText))
            return false;
        return SemanticModelFor(invocation)?.GetSymbolInfo(invocation).Symbol is IMethodSymbol method &&
            method.ContainingType.Name is "Enumerable" or "Queryable";
    }

    private bool TryEmitGuardedLinqExpression(
        ExpressionSyntax expression,
        VariableScope scope,
        VmMethod? context,
        Action<string> continuation)
    {
        if (expression is not InvocationExpressionSyntax invocation ||
            !IsGuardedLinqInvocation(invocation) ||
            invocation.Expression is not MemberAccessExpressionSyntax member ||
            !TryBuildLinqQuery(member.Expression, scope, substitutions: null, out var query))
            return false;
        if (IsGroupingType(query.ElementType.Name))
        {
            AddDiagnostic(
                "SS6411",
                "This LINQ terminal requires full IGrouping values; project group.Key before consuming the sequence.",
                invocation);
            continuation("NULL");
            return true;
        }

        var method = member.Name.Identifier.ValueText;
        var arguments = invocation.ArgumentList.Arguments;
        if (method is "First" or "Last" or "Single" or "SingleOrDefault")
        {
            if (arguments.Count > 1 ||
                (arguments.Count == 1 && !TryAppendLinqLambda(
                    query,
                    arguments[0].Expression,
                    LinqQueryStepKind.Where,
                    scope,
                    substitutions: null,
                    negated: false,
                    descending: false,
                    out query)))
            {
                AddDiagnostic("SS6401", $"Enumerable.{method} expects zero arguments or one predicate.", invocation);
                continuation("NULL");
                return true;
            }
        }
        else if (method is "MinBy" or "MaxBy")
        {
            if (arguments.Count != 1 || !TryAppendLinqLambda(
                    query,
                    arguments[0].Expression,
                    LinqQueryStepKind.OrderBy,
                    scope,
                    substitutions: null,
                    negated: false,
                    descending: method == "MaxBy",
                    out query))
            {
                if (arguments.Count != 1)
                    AddDiagnostic("SS6401", $"Enumerable.{method} expects one key selector.", invocation);
                continuation("NULL");
                return true;
            }
        }
        else if (method is "Min" or "Max" or "Average")
        {
            if (arguments.Count > 1 ||
                (arguments.Count == 1 && !TryAppendLinqLambda(
                    query,
                    arguments[0].Expression,
                    LinqQueryStepKind.Select,
                    scope,
                    substitutions: null,
                    negated: false,
                    descending: false,
                    out query)))
            {
                AddDiagnostic("SS6401", $"Enumerable.{method} expects zero arguments or one selector.", invocation);
                continuation("NULL");
                return true;
            }
        }
        else if (method is "ElementAt" or "ElementAtOrDefault" && arguments.Count != 1)
        {
            AddDiagnostic("SS6401", $"Enumerable.{method} expects one index.", invocation);
            continuation("NULL");
            return true;
        }

        var querySql = RenderLinqQuery(query, scope, substitutions: null);
        if (method is "ElementAt" or "ElementAtOrDefault")
        {
            EmitVmExpression(arguments[0].Expression, scope, context, index =>
                EmitGuardedLinqElementAt(querySql, query.ElementType, method, index, continuation));
            return true;
        }

        var resultType = InferType(invocation, scope);
        EmitGuardedLinqTerminal(
            querySql,
            query.ElementType,
            resultType,
            method,
            method is "First" or "Last" or "Single" ||
                method is "Min" or "Max" or "Average" or "MinBy" or "MaxBy" &&
                LinqResultThrowsOnEmpty(invocation),
            continuation);
        return true;
    }

    private bool TryEmitGuardedLinqExpression(
        IrExpression expression,
        VariableScope scope,
        VmMethod? context,
        Action<string> continuation)
    {
        if (expression is not IrInvocationExpression
            {
                Target: IrMemberExpression member
            } invocation ||
            !IntrinsicCatalog.IsGuardedLinqOperator(member.MemberName) ||
            !TryBuildLinqQuery(member.Receiver, scope, substitutions: null, out var query))
            return false;
        if (IsGroupingType(query.ElementType.Name))
        {
            AddDiagnostic(
                "SS6411",
                "This LINQ terminal requires full IGrouping values; project group.Key before consuming the sequence.",
                invocation.Source);
            continuation("NULL");
            return true;
        }

        var method = member.MemberName;
        var arguments = invocation.Arguments;
        if (method is "First" or "Last" or "Single" or "SingleOrDefault")
        {
            if (arguments.Count > 1 ||
                arguments.Count == 1 && !TryAppendLinqLambda(
                    query, arguments[0], LinqQueryStepKind.Where, scope, null, false, false, out query))
            {
                AddDiagnostic("SS6401", $"Enumerable.{method} expects zero arguments or one predicate.", invocation.Source);
                continuation("NULL");
                return true;
            }
        }
        else if (method is "MinBy" or "MaxBy")
        {
            if (arguments.Count != 1 || !TryAppendLinqLambda(
                    query, arguments[0], LinqQueryStepKind.OrderBy, scope, null, false,
                    method == "MaxBy", out query))
            {
                AddDiagnostic("SS6401", $"Enumerable.{method} expects one key selector.", invocation.Source);
                continuation("NULL");
                return true;
            }
        }
        else if (method is "Min" or "Max" or "Average")
        {
            if (arguments.Count > 1 ||
                arguments.Count == 1 && !TryAppendLinqLambda(
                    query, arguments[0], LinqQueryStepKind.Select, scope, null, false, false, out query))
            {
                AddDiagnostic("SS6401", $"Enumerable.{method} expects zero arguments or one selector.", invocation.Source);
                continuation("NULL");
                return true;
            }
        }
        else if (method is "ElementAt" or "ElementAtOrDefault" && arguments.Count != 1)
        {
            AddDiagnostic("SS6401", $"Enumerable.{method} expects one index.", invocation.Source);
            continuation("NULL");
            return true;
        }

        var querySql = RenderLinqQuery(query, scope, null);
        if (method is "ElementAt" or "ElementAtOrDefault")
        {
            EmitVmExpression(arguments[0], scope, context, index =>
                EmitGuardedLinqElementAt(querySql, query.ElementType, method, index, continuation));
            return true;
        }

        EmitGuardedLinqTerminal(
            querySql,
            query.ElementType,
            invocation.Type,
            method,
            method is "First" or "Last" or "Single" ||
                method is "Min" or "Max" or "Average" or "MinBy" or "MaxBy" &&
                !invocation.Type.IsReference && invocation.Type != IrType.Unknown,
            continuation);
        return true;
    }

    private void EmitGuardedLinqElementAt(
        string querySql,
        IrType elementType,
        string method,
        string index,
        Action<string> continuation)
    {
        var sourceAlias = NextLinqAlias("guarded_element");
        var numberedAlias = NextLinqAlias("guarded_numbered");
        var value = _names.Allocate("_linq_element");
        var found = _names.Allocate("_linq_found");
        var numbered = $"SELECT ROW_NUMBER() OVER (ORDER BY {sourceAlias}.__index) - 1 AS __ordinal, {sourceAlias}.__value FROM ({querySql}) AS {sourceAlias}";

        _sql.Line($"DECLARE {value} {elementType.SqlType()};");
        _sql.Line($"DECLARE {found} BIT = 0;");
        _sql.Line($"SELECT {value} = {numberedAlias}.__value, {found} = 1 FROM ({numbered}) AS {numberedAlias} WHERE {numberedAlias}.__ordinal = {index};");
        if (method == "ElementAt")
            _sql.Line($"IF {found} = 0 THROW 51009, 'LINQ index was out of range.', 1;");
        continuation(method == "ElementAt" ? value : $"COALESCE({value}, {DefaultSql(elementType)})");
    }

    private void EmitGuardedLinqTerminal(
        string querySql,
        IrType elementType,
        IrType resultType,
        string method,
        bool throwsOnEmpty,
        Action<string> continuation)
    {
        var alias = NextLinqAlias("guarded_value");
        var value = _names.Allocate("_linq_value");
        var count = _names.Allocate("_linq_count");
        var valueType = resultType == IrType.Unknown ? elementType : resultType;
        _sql.Line($"DECLARE {value} {valueType.SqlType()};");

        if (method is "Single" or "SingleOrDefault")
        {
            _sql.Line($"SELECT TOP (2) {value} = {alias}.__value FROM ({querySql}) AS {alias} ORDER BY {alias}.__index;");
            _sql.Line($"DECLARE {count} INT = @@ROWCOUNT;");
        }
        else if (method is "Min" or "Max" or "Average")
        {
            _sql.Line($"DECLARE {count} BIGINT;");
            var aggregate = method == "Average"
                ? $"AVG(CONVERT({valueType.SqlType()}, {alias}.__value))"
                : $"{method.ToUpperInvariant()}({alias}.__value)";
            _sql.Line($"SELECT {count} = COUNT_BIG(*), {value} = {aggregate} FROM ({querySql}) AS {alias};");
        }
        else
        {
            var descending = method == "Last" ? " DESC" : string.Empty;
            _sql.Line($"SELECT TOP (1) {value} = {alias}.__value FROM ({querySql}) AS {alias} ORDER BY {alias}.__index{descending};");
            _sql.Line($"DECLARE {count} INT = @@ROWCOUNT;");
        }

        if (throwsOnEmpty)
            _sql.Line($"IF {count} = 0 THROW 51007, 'LINQ sequence contains no elements.', 1;");
        if (method is "Single" or "SingleOrDefault")
            _sql.Line($"IF {count} > 1 THROW 51008, 'LINQ sequence contains more than one element.', 1;");

        continuation(method == "SingleOrDefault"
            ? $"COALESCE({value}, {DefaultSql(elementType)})"
            : value);
    }

    private bool LinqResultThrowsOnEmpty(InvocationExpressionSyntax invocation)
    {
        var type = SemanticModelFor(invocation)?.GetTypeInfo(invocation).Type;
        if (type is null || type.IsReferenceType)
            return false;
        return type is not INamedTypeSymbol
        {
            OriginalDefinition.SpecialType: SpecialType.System_Nullable_T
        };
    }

    private string LinqElementAtSql(string querySql, string alias, string index)
    {
        var numberedAlias = NextLinqAlias("element_numbered");
        var numbered = $"SELECT ROW_NUMBER() OVER (ORDER BY {alias}.__index) - 1 AS __ordinal, {alias}.__value FROM ({querySql}) AS {alias}";
        return $"(SELECT {numberedAlias}.__value FROM ({numbered}) AS {numberedAlias} WHERE {numberedAlias}.__ordinal = {index})";
    }

    private void EmitLinqForEach(
        ProceduralForEach statement,
        SqlLinqQueryPlan query,
        VariableScope parentScope,
        InlineReturn? inlineReturn,
        string? namePrefix)
    {
        if (IsGroupingType(query.ElementType.Name))
        {
            AddDiagnostic(
                "SS6411",
                "Iterating full IGrouping values is not supported; project group.Key before foreach.",
                statement.SourceExpression.Source);
            return;
        }

        var scope = parentScope.Child();
        var lastIndex = _names.Allocate("_linq_last_index");
        var nextIndex = _names.Allocate("_linq_next_index");
        var itemSql = _names.Allocate(statement.Element.Name);
        var itemType = statement.ElementType == IrType.Unknown ? query.ElementType : statement.ElementType;
        var conditionLabel = _names.AllocateLabel("linq_foreach_condition");
        var continueLabel = _names.AllocateLabel("linq_foreach_continue");
        var breakLabel = _names.AllocateLabel("linq_foreach_break");
        var querySql = RenderLinqQuery(query, parentScope, substitutions: null);
        string? bufferedTable = null;
        if (LinqPlanRequiresBuffering(query))
        {
            bufferedTable = $"#__sharpsql_linq_foreach_{++_linqId}";
            var bufferAlias = NextLinqAlias("foreach_buffer");
            _sql.Line($"DROP TABLE IF EXISTS {bufferedTable};");
            _sql.Line($"SELECT {bufferAlias}.__index, {bufferAlias}.__value INTO {bufferedTable} FROM ({querySql}) AS {bufferAlias};");
            _sql.Line($"CREATE UNIQUE CLUSTERED INDEX IX_linq_index ON {bufferedTable} (__index);");
            querySql = $"SELECT __index, __value FROM {bufferedTable}";
        }
        var sourceAlias = NextLinqAlias("foreach");

        _sql.Line($"DECLARE {lastIndex} INT = -1;");
        _sql.Line($"DECLARE {nextIndex} INT;");
        _sql.Line($"DECLARE {itemSql} {itemType.SqlType()};");
        scope.Add(statement.Element, new ScalarVariableBinding(itemSql, itemType));
        EmitLabel(conditionLabel);
        _sql.Line($"SET {nextIndex} = NULL;");
        _sql.Line($"SELECT TOP (1) {nextIndex} = {sourceAlias}.__index, {itemSql} = {sourceAlias}.__value FROM ({querySql}) AS {sourceAlias} WHERE {sourceAlias}.__index > {lastIndex} ORDER BY {sourceAlias}.__index;");
        _sql.Line($"IF {nextIndex} IS NULL GOTO {breakLabel};");
        _sql.Line($"SET {lastIndex} = {nextIndex};");
        EmitEmbeddedContents(
            statement.Body,
            scope,
            inlineReturn,
            new LoopContext(breakLabel, continueLabel),
            namePrefix);
        EmitLabel(continueLabel);
        _sql.Line($"GOTO {conditionLabel};");
        EmitLabel(breakLabel);
        if (bufferedTable is not null)
            _sql.Line($"DROP TABLE IF EXISTS {bufferedTable};");
    }

    private static bool LinqPlanRequiresBuffering(SqlLinqQueryPlan query) =>
        query.Steps.Any(step => step is
            SqlLinqDistinctQueryStep or
            SqlLinqGroupQueryStep or
            SqlLinqJoinQueryStep or
            SqlLinqLambdaQueryStep { Kind: LinqQueryStepKind.OrderBy or LinqQueryStepKind.ThenBy });

    private bool TryEmitLinqMaterialization(
        ExpressionSyntax expression,
        VariableScope scope,
        VmMethod? context,
        Action<string> continuation,
        IReadOnlyDictionary<string, Substitution>? substitutions = null)
    {
        if (expression is not InvocationExpressionSyntax
            {
                Expression: MemberAccessExpressionSyntax member,
                ArgumentList.Arguments.Count: 0
            } invocation ||
            !IntrinsicCatalog.IsMaterializer(member.Name.Identifier.ValueText))
            return false;

        if (TryEmitRepeatSelectMaterialization(member, scope, context, continuation))
            return true;

        if (!TryBuildLinqQuery(member.Expression, scope, substitutions, out var query))
            return false;
        if (IsGroupingType(query.ElementType.Name))
        {
            AddDiagnostic(
                "SS6411",
                "Materializing full IGrouping values is not supported; project group.Key first.",
                invocation);
            continuation("NULL");
            return true;
        }

        var querySql = RenderLinqQuery(query, scope, substitutions);
        var sourceAlias = NextLinqAlias("materialize");
        var collection = AllocateHeapHeader(
            member.Name.Identifier.ValueText == "ToList" ? ListHeapTypeId : ArrayHeapTypeId,
            "__count",
            "0");
        var column = CollectionValueColumn(query.ElementType, key: false);
        var value = CollectionStoredValue(query.ElementType, $"{sourceAlias}.__value");
        _sql.Line(
            $"INSERT INTO {HeapIndexedItems} ({HeapInsertColumns($"__owner_id, __index, {column}")}) " +
            $"SELECT {HeapInsertValues($"{collection}, CONVERT(INT, ROW_NUMBER() OVER (ORDER BY {sourceAlias}.__index) - 1), {value}")} " +
            $"FROM ({querySql}) AS {sourceAlias};");
        var materializedCount = _names.Allocate("_linq_materialized_count");
        _sql.Line($"DECLARE {materializedCount} INT = @@ROWCOUNT;");
        _sql.Line($"UPDATE {HeapObjects} SET __count = {materializedCount} WHERE {HeapExecutionFilter()}__id = {collection};");
        continuation(collection);
        return true;
    }

    private bool TryEmitLinqMaterialization(
        IrExpression expression,
        VariableScope scope,
        VmMethod? context,
        Action<string> continuation,
        IReadOnlyDictionary<string, Substitution>? substitutions = null)
    {
        if (expression is not IrInvocationExpression
            {
                Target: IrMemberExpression member,
                Arguments.Count: 0
            } invocation || !IntrinsicCatalog.IsMaterializer(member.MemberName))
            return false;
        if (TryEmitRepeatSelectMaterialization(member, scope, context, continuation, substitutions))
            return true;
        if (!TryBuildLinqQuery(member.Receiver, scope, substitutions, out var query))
            return false;
        if (IsGroupingType(query.ElementType.Name))
        {
            AddDiagnostic("SS6411", "Materializing full IGrouping values is not supported; project group.Key first.", invocation.Source);
            continuation("NULL");
            return true;
        }
        var querySql = RenderLinqQuery(query, scope, substitutions);
        var alias = NextLinqAlias("materialize");
        var collection = AllocateHeapHeader(
            member.MemberName == "ToList" ? ListHeapTypeId : ArrayHeapTypeId,
            "__count",
            "0");
        var column = CollectionValueColumn(query.ElementType, false);
        var value = CollectionStoredValue(query.ElementType, $"{alias}.__value");
        _sql.Line(
            $"INSERT INTO {HeapIndexedItems} ({HeapInsertColumns($"__owner_id, __index, {column}")}) " +
            $"SELECT {HeapInsertValues($"{collection}, CONVERT(INT, ROW_NUMBER() OVER (ORDER BY {alias}.__index) - 1), {value}")} " +
            $"FROM ({querySql}) AS {alias};");
        var count = _names.Allocate("_linq_materialized_count");
        _sql.Line($"DECLARE {count} INT = @@ROWCOUNT;");
        _sql.Line($"UPDATE {HeapObjects} SET __count = {count} WHERE {HeapExecutionFilter()}__id = {collection};");
        continuation(collection);
        return true;
    }

    private bool TryEmitRepeatSelectMaterialization(
        IrMemberExpression materializationMember,
        VariableScope scope,
        VmMethod? context,
        Action<string> continuation,
        IReadOnlyDictionary<string, Substitution>? substitutions)
    {
        if (materializationMember.Receiver is not IrInvocationExpression
            {
                Target: IrMemberExpression { MemberName: "Select" } selectMember,
                Arguments.Count: 1
            } select ||
            selectMember.Receiver is not IrInvocationExpression
            {
                Target: IrMemberExpression
                {
                    Receiver: IrVariableExpression { Symbol.Name: "Enumerable" },
                    MemberName: "Repeat"
                },
                Arguments.Count: 2
            } repeat ||
            !TryGetSingleParameterLambda(
                select.Arguments[0],
                scope,
                substitutions,
                out var parameterName,
                out var selectorBody,
                out var captures))
            return false;

        var repeatedExpression = repeat.Arguments[0];
        var countExpression = repeat.Arguments[1];
        var repeatedType = repeatedExpression.Type;
        var resultType = selectorBody.Type;

        EmitVmExpression(repeatedExpression, scope, context, repeatedValue =>
        {
            var repeatedStorage = AllocateVmTemporary(repeatedType, context);
            StoreVmTemporary(repeatedStorage, repeatedValue);
            EmitVmExpression(countExpression, scope, context, count =>
            {
                var countStorage = AllocateVmTemporary(IrType.Int, context);
                StoreVmTemporary(countStorage, count);
                var savedCount = ReadVmTemporary(countStorage);
                _sql.Line($"IF {savedCount} < 0 THROW 51006, 'Enumerable.Repeat count must be non-negative.', 1;");

                var collection = AllocateHeapHeader(
                    materializationMember.MemberName == "ToList" ? ListHeapTypeId : ArrayHeapTypeId,
                    "__count",
                    savedCount);
                var index = _names.Allocate("_repeat_index");
                var selectorScope = scope.Child();
                selectorScope.Add(parameterName, new ScalarVariableBinding(
                    ReadVmTemporary(repeatedStorage),
                    repeatedType));
                AddLinqCaptureBindings(selectorBody, captures, selectorScope);

                _sql.Line($"DECLARE {index} INT = 0;");
                _sql.Line($"WHILE {index} < {savedCount}");
                _sql.Line("BEGIN");
                using (_sql.Indent())
                {
                    EmitVmExpression(selectorBody, selectorScope, context, value =>
                    {
                        InsertIndexedItem(collection, index, resultType, value);
                        _sql.Line($"SET {index} = {index} + 1;");
                    });
                }
                _sql.Line("END;");
                continuation(collection);
            });
        });
        return true;
    }

    private static void AddLinqCaptureBindings(
        IrExpression expression,
        IReadOnlyDictionary<string, Substitution>? captures,
        VariableScope scope)
    {
        if (captures is null)
            return;
        Visit(expression);

        void Visit(IrExpression current)
        {
            if (current is IrVariableExpression variable &&
                captures.TryGetValue(variable.Symbol.Name, out var capture))
            {
                scope.Add(variable.Symbol, new ScalarVariableBinding(
                    capture.Expression.Sql,
                    capture.Type));
                return;
            }
            switch (current)
            {
                case IrBinaryExpression binary: Visit(binary.Left); Visit(binary.Right); break;
                case IrUnaryExpression unary: Visit(unary.Operand); break;
                case IrConversionExpression conversion: Visit(conversion.Operand); break;
                case IrAwaitExpression awaitExpression: Visit(awaitExpression.Operand); break;
                case IrConditionalExpression conditional:
                    Visit(conditional.Condition); Visit(conditional.WhenTrue); Visit(conditional.WhenFalse); break;
                case IrMemberExpression member: Visit(member.Receiver); break;
                case IrElementExpression element:
                    Visit(element.Receiver); foreach (var argument in element.Arguments) Visit(argument); break;
                case IrInvocationExpression invocation:
                    Visit(invocation.Target); foreach (var argument in invocation.Arguments) Visit(argument); break;
                case IrObjectCreationExpression creation:
                    foreach (var argument in creation.Arguments) Visit(argument);
                    foreach (var initializer in creation.Initializers) Visit(initializer);
                    break;
                case IrWithExpression with:
                    Visit(with.Receiver); foreach (var initializer in with.Initializers) Visit(initializer); break;
                case IrArrayCreationExpression array:
                    if (array.Length is not null) Visit(array.Length);
                    foreach (var element in array.Elements) Visit(element);
                    break;
                case IrInterpolatedStringExpression interpolated:
                    foreach (var part in interpolated.Parts.OfType<IrInterpolation>()) Visit(part.Expression); break;
                case IrAssignmentExpression assignment: Visit(assignment.Target); Visit(assignment.Value); break;
            }
        }
    }

    private bool TryEmitRepeatSelectMaterialization(
        MemberAccessExpressionSyntax materializationMember,
        VariableScope scope,
        VmMethod? context,
        Action<string> continuation)
    {
        if (materializationMember.Expression is not InvocationExpressionSyntax
            {
                Expression: MemberAccessExpressionSyntax
                {
                    Name.Identifier.ValueText: "Select"
                } selectMember,
                ArgumentList.Arguments.Count: 1
            } select ||
            selectMember.Expression is not InvocationExpressionSyntax
            {
                Expression: MemberAccessExpressionSyntax
                {
                    Expression: IdentifierNameSyntax { Identifier.ValueText: "Enumerable" },
                    Name.Identifier.ValueText: "Repeat"
                },
                ArgumentList.Arguments.Count: 2
            } repeat ||
            !TryGetSingleParameterLambda(
                select.ArgumentList.Arguments[0].Expression,
                scope,
                out var parameterName,
                out var selectorBody,
                out _))
            return false;

        var repeatedExpression = repeat.ArgumentList.Arguments[0].Expression;
        var countExpression = repeat.ArgumentList.Arguments[1].Expression;
        var repeatedType = InferType(repeatedExpression, scope);
        var resultType = selectorBody.Type;

        EmitVmExpression(repeatedExpression, scope, context, repeatedValue =>
        {
            var repeatedStorage = AllocateVmTemporary(repeatedType, context);
            StoreVmTemporary(repeatedStorage, repeatedValue);
            EmitVmExpression(countExpression, scope, context, count =>
            {
                var countStorage = AllocateVmTemporary(IrType.Int, context);
                StoreVmTemporary(countStorage, count);
                var savedCount = ReadVmTemporary(countStorage);
                _sql.Line($"IF {savedCount} < 0 THROW 51006, 'Enumerable.Repeat count must be non-negative.', 1;");

                var collection = AllocateHeapHeader(
                    materializationMember.Name.Identifier.ValueText == "ToList" ? ListHeapTypeId : ArrayHeapTypeId,
                    "__count",
                    savedCount);
                var index = _names.Allocate("_repeat_index");
                var selectorScope = scope.Child();
                selectorScope.Add(parameterName, new ScalarVariableBinding(
                    ReadVmTemporary(repeatedStorage),
                    repeatedType));

                _sql.Line($"DECLARE {index} INT = 0;");
                _sql.Line($"WHILE {index} < {savedCount}");
                _sql.Line("BEGIN");
                using (_sql.Indent())
                {
                    EmitVmExpression(selectorBody, selectorScope, context, value =>
                    {
                        InsertIndexedItem(collection, index, resultType, value);
                        _sql.Line($"SET {index} = {index} + 1;");
                    });
                }
                _sql.Line("END;");
                continuation(collection);
            });
        });
        return true;
    }

    private bool IsLinqMaterialization(InvocationExpressionSyntax invocation)
    {
        if (invocation.Expression is not MemberAccessExpressionSyntax member ||
            !IntrinsicCatalog.IsMaterializer(member.Name.Identifier.ValueText))
            return false;
        var receiverType = SemanticModelFor(member)?.GetTypeInfo(member.Expression).Type;
        if (receiverType is null)
            return false;
        var type = CSharpTypeFactory.From(receiverType);
        return IsSequenceType(type.Name) || IsLinqSequenceType(type.Name);
    }

    private string NextLinqAlias(string role) => $"__linq_{role}_{++_linqId}";

    private static Dictionary<string, Substitution> LinqReplacements(
        IReadOnlyDictionary<string, Substitution>? substitutions,
        string parameterName,
        string value,
        IrType type)
    {
        var replacements = substitutions is null
            ? new Dictionary<string, Substitution>(StringComparer.Ordinal)
            : substitutions.CopyToDictionary(StringComparer.Ordinal);
        replacements[parameterName] = new Substitution(SqlScalarExpression.Primary(value, type));
        return replacements;
    }

    private static IReadOnlyDictionary<string, SqlLinqScalarCapture>? CaptureLinqSubstitutions(
        IReadOnlyDictionary<string, Substitution>? substitutions)
    {
        if (substitutions is null || substitutions.Count == 0)
            return null;

        return substitutions.ToDictionary(
            item => item.Key,
            item => new SqlLinqScalarCapture(item.Value.Expression),
            StringComparer.Ordinal);
    }

    private Substitution CaptureLinqMethodArgument(
        ExpressionSyntax argument,
        VariableScope scope,
        IReadOnlyDictionary<string, Substitution>? substitutions)
    {
        var type = InferType(argument, scope, substitutions);
        var value = EmitScalarExpression(argument, scope, substitutions);
        var sqlName = _names.Allocate("_linq_capture");
        _sql.Line($"DECLARE {sqlName} {type.SqlType()} = {value.Sql};");
        return new Substitution(SqlScalarExpression.Primary(sqlName, type));
    }

    private Substitution CaptureLinqMethodArgument(
        IrExpression argument,
        VariableScope scope,
        IReadOnlyDictionary<string, Substitution>? substitutions)
    {
        var value = EmitScalarExpression(argument, scope, substitutions);
        var sqlName = _names.Allocate("_linq_capture");
        _sql.Line($"DECLARE {sqlName} {argument.Type.SqlType()} = {value.Sql};");
        return new Substitution(SqlScalarExpression.Primary(sqlName, argument.Type));
    }

    private static IReadOnlyDictionary<string, Substitution>? RestoreLinqSubstitutions(
        IReadOnlyDictionary<string, SqlLinqScalarCapture>? captures)
    {
        if (captures is null || captures.Count == 0)
            return null;

        return captures.ToDictionary(
            item => item.Key,
            item => new Substitution(item.Value.Expression),
            StringComparer.Ordinal);
    }

    private static IReadOnlyDictionary<string, Substitution>? MergeLinqSubstitutions(
        IReadOnlyDictionary<string, Substitution>? captured,
        IReadOnlyDictionary<string, Substitution>? current)
    {
        if (captured is null || captured.Count == 0)
            return current;
        if (current is null || current.Count == 0)
            return captured;
        var merged = captured.CopyToDictionary(StringComparer.Ordinal);
        foreach (var item in current)
            merged[item.Key] = item.Value;
        return merged;
    }

    private static string LinqEqualityValue(string value, IrType type) =>
        type.IsString ? $"{value} COLLATE Latin1_General_100_BIN2" : value;

    private static string LinqOrderValue(string value, IrType type) =>
        type.IsString ? $"{value} COLLATE Latin1_General_100_BIN2" : value;

    private static string LinqValueEquality(string left, string right, IrType type)
    {
        var equality = type.IsString
            ? $"{left} COLLATE Latin1_General_100_BIN2 = {right} COLLATE Latin1_General_100_BIN2"
            : $"{left} = {right}";
        return $"(({left} IS NULL AND {right} IS NULL) OR {equality})";
    }

    private static string LinqJoinEquality(string left, string right, IrType type) =>
        type.IsString
            ? $"{left} COLLATE Latin1_General_100_BIN2 = {right} COLLATE Latin1_General_100_BIN2"
            : $"{left} = {right}";

    private bool TryGetLinqPlanSubstitution(string name, out SqlLinqQueryPlan query)
    {
        foreach (var substitutions in _linqPlanSubstitutions)
        {
            if (substitutions.TryGetValue(name, out query!))
                return true;
        }
        query = null!;
        return false;
    }

    private bool TryBuildLinqLambda(
        IrExpression expression,
        VariableScope scope,
        IReadOnlyDictionary<string, Substitution>? substitutions,
        out SqlLinqLambdaPlan lambda)
    {
        if (expression is IrLambdaExpression lambdaExpression)
        {
            lambda = new SqlLinqLambdaPlan(
                lambdaExpression,
                CaptureLinqLambdaBindings(lambdaExpression, scope, substitutions));
            return true;
        }
        if (expression is IrVariableExpression variable)
        {
            foreach (var lambdaSubstitutions in _linqLambdaSubstitutions)
                if (lambdaSubstitutions.TryGetValue(variable.Symbol.Name, out lambda!))
                    return true;
            if (scope.Find(variable.Symbol) is LambdaVariableBinding { Lambda: var stored })
            {
                lambda = stored;
                return true;
            }
        }
        if (expression is IrInvocationExpression invocation &&
            TryGetMethod(invocation, out var method) &&
            method.PureExpression is not null && IsDelegateType(method.ReturnType.Name))
        {
            var arguments = InvocationArgumentExpressions(invocation, method);
            if (arguments.Count == method.Parameters.Count && CanInline(method, arguments.Count))
            {
                var scalarReplacements = substitutions is null
                    ? new Dictionary<string, Substitution>(StringComparer.Ordinal)
                    : substitutions.CopyToDictionary(StringComparer.Ordinal);
                var planReplacements = new Dictionary<string, SqlLinqQueryPlan>(StringComparer.Ordinal);
                var lambdaReplacements = new Dictionary<string, SqlLinqLambdaPlan>(StringComparer.Ordinal);
                for (var index = 0; index < arguments.Count; index++)
                {
                    var parameter = method.Parameters[index];
                    var argument = arguments[index];
                    if (TryBuildLinqLambda(argument, scope, substitutions, out var lambdaArgument))
                        lambdaReplacements[parameter.Name] = lambdaArgument;
                    else if ((IsSequenceType(parameter.Type.Name) || KnownTypeFacts.IsLinqSequence(parameter.Type.Name)) &&
                             TryBuildLinqQuery(argument, scope, substitutions, out var argumentQuery))
                        planReplacements[parameter.Name] = argumentQuery;
                    else
                        scalarReplacements[parameter.Name] = CaptureLinqMethodArgument(argument, scope, substitutions);
                }
                _linqPlanSubstitutions.Push(planReplacements);
                _linqLambdaSubstitutions.Push(lambdaReplacements);
                try
                {
                    return TryBuildLinqLambda(method.PureExpression, scope, scalarReplacements, out lambda);
                }
                finally
                {
                    _linqLambdaSubstitutions.Pop();
                    _linqPlanSubstitutions.Pop();
                }
            }
        }
        lambda = null!;
        return false;
    }

    private static IReadOnlyDictionary<string, SqlLinqScalarCapture>? CaptureLinqLambdaBindings(
        IrLambdaExpression lambda,
        VariableScope scope,
        IReadOnlyDictionary<string, Substitution>? substitutions)
    {
        var captures = CaptureLinqSubstitutions(substitutions) is { } existing
            ? existing.CopyToDictionary(StringComparer.Ordinal)
            : new Dictionary<string, SqlLinqScalarCapture>(StringComparer.Ordinal);
        var parameters = lambda.Parameters.Select(parameter => parameter.Name).ToHashSet(StringComparer.Ordinal);
        if (lambda.ExpressionBody is not null) Visit(lambda.ExpressionBody);
        return captures.Count == 0 ? null : captures;

        void Visit(IrExpression expression)
        {
            if (expression is IrVariableExpression variable)
            {
                var name = variable.Symbol.Name;
                if (!parameters.Contains(name) && !captures.ContainsKey(name) &&
                    scope.Find(variable.Symbol) is ScalarVariableBinding binding)
                    captures[name] = new SqlLinqScalarCapture(binding.Scalar);
                return;
            }
            switch (expression)
            {
                case IrBinaryExpression binary: Visit(binary.Left); Visit(binary.Right); break;
                case IrUnaryExpression unary: Visit(unary.Operand); break;
                case IrConversionExpression conversion: Visit(conversion.Operand); break;
                case IrAwaitExpression awaitExpression: Visit(awaitExpression.Operand); break;
                case IrConditionalExpression conditional:
                    Visit(conditional.Condition); Visit(conditional.WhenTrue); Visit(conditional.WhenFalse); break;
                case IrMemberExpression member: Visit(member.Receiver); break;
                case IrElementExpression element:
                    Visit(element.Receiver); foreach (var argument in element.Arguments) Visit(argument); break;
                case IrInvocationExpression invocation:
                    Visit(invocation.Target); foreach (var argument in invocation.Arguments) Visit(argument); break;
                case IrInterpolatedStringExpression interpolated:
                    foreach (var part in interpolated.Parts.OfType<IrInterpolation>()) Visit(part.Expression); break;
            }
        }
    }

    private bool TryBuildLinqLambda(
        ExpressionSyntax expression,
        VariableScope scope,
        IReadOnlyDictionary<string, Substitution>? substitutions,
        out SqlLinqLambdaPlan lambda)
    {
        expression = StripParentheses(expression);
        if (expression is LambdaExpressionSyntax lambdaExpression)
        {
            lambda = new SqlLinqLambdaPlan(
                BindIrExpression(expression, scope),
                CaptureLinqLambdaBindings(lambdaExpression, scope, substitutions));
            return true;
        }

        if (expression is IdentifierNameSyntax identifier)
        {
            foreach (var lambdaSubstitutions in _linqLambdaSubstitutions)
            {
                if (lambdaSubstitutions.TryGetValue(identifier.Identifier.ValueText, out lambda!))
                    return true;
            }
            if (scope.Find(identifier.Identifier.ValueText) is LambdaVariableBinding { Lambda: var storedLambda })
            {
                lambda = storedLambda;
                return true;
            }
        }

        if (expression is InvocationExpressionSyntax invocation &&
            TryGetMethod(invocation, out var method) &&
            method.PureExpression is not null &&
            IsDelegateType(method.ReturnType.Name))
        {
            var arguments = InvocationArgumentExpressions(invocation, method);
            if (arguments.Count == method.Parameters.Count && CanInline(method, arguments.Count))
            {
                var scalarReplacements = substitutions is null
                    ? new Dictionary<string, Substitution>(StringComparer.Ordinal)
                    : substitutions.CopyToDictionary(StringComparer.Ordinal);
                var planReplacements = new Dictionary<string, SqlLinqQueryPlan>(StringComparer.Ordinal);
                var lambdaReplacements = new Dictionary<string, SqlLinqLambdaPlan>(StringComparer.Ordinal);
                for (var index = 0; index < arguments.Count; index++)
                {
                    var parameter = method.Parameters[index];
                    var argument = arguments[index];
                    if (TryBuildLinqLambda(argument, scope, substitutions, out var lambdaArgument))
                    {
                        lambdaReplacements[parameter.Name] = lambdaArgument;
                        continue;
                    }
                    if ((IsSequenceType(parameter.Type.Name) || IsLinqSequenceType(parameter.Type.Name)) &&
                        TryBuildLinqQuery(argument, scope, substitutions, out var argumentQuery))
                    {
                        planReplacements[parameter.Name] = argumentQuery;
                        continue;
                    }
                    scalarReplacements[parameter.Name] = CaptureLinqMethodArgument(
                        argument,
                        scope,
                        substitutions);
                }

                _linqPlanSubstitutions.Push(planReplacements);
                _linqLambdaSubstitutions.Push(lambdaReplacements);
                try
                {
                    return TryBuildLinqLambda(CSharpExpression(method.PureExpression), scope, scalarReplacements, out lambda);
                }
                finally
                {
                    _linqLambdaSubstitutions.Pop();
                    _linqPlanSubstitutions.Pop();
                }
            }
        }

        lambda = null!;
        return false;
    }

    private static IReadOnlyDictionary<string, SqlLinqScalarCapture>? CaptureLinqLambdaBindings(
        LambdaExpressionSyntax lambda,
        VariableScope scope,
        IReadOnlyDictionary<string, Substitution>? substitutions)
    {
        var captures = CaptureLinqSubstitutions(substitutions) is { } scalarCaptures
            ? scalarCaptures.CopyToDictionary(StringComparer.Ordinal)
            : new Dictionary<string, SqlLinqScalarCapture>(StringComparer.Ordinal);
        var parameters = lambda switch
        {
            SimpleLambdaExpressionSyntax simple => [simple.Parameter.Identifier.ValueText],
            ParenthesizedLambdaExpressionSyntax parenthesized =>
                parenthesized.ParameterList.Parameters
                    .Select(parameter => parameter.Identifier.ValueText)
                    .ToHashSet(StringComparer.Ordinal),
            _ => []
        };
        foreach (var identifier in lambda.Body.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>())
        {
            var name = identifier.Identifier.ValueText;
            if (parameters.Contains(name) || captures.ContainsKey(name))
                continue;
            if (scope.Find(name) is ScalarVariableBinding binding)
                captures[name] = new SqlLinqScalarCapture(binding.Scalar);
        }
        return captures.Count == 0 ? null : captures;
    }

    private bool TryGetSingleParameterLambda(
        ExpressionSyntax expression,
        VariableScope scope,
        out string parameterName,
        out IrExpression body,
        out IReadOnlyDictionary<string, Substitution>? captures)
    {
        if (!TryBuildLinqLambda(expression, scope, substitutions: null, out var lambda))
        {
            parameterName = string.Empty;
            body = null!;
            captures = null;
            return false;
        }
        captures = RestoreLinqSubstitutions(lambda.Captures);
        if (lambda.Expression is IrLambdaExpression
            {
                Parameters.Count: 1,
                ExpressionBody: not null
            } single)
        {
            parameterName = single.Parameters[0].Name;
            body = single.ExpressionBody;
            return true;
        }

        parameterName = string.Empty;
        body = null!;
        captures = null;
        return false;
    }

    private bool TryGetSingleParameterLambda(
        IrExpression expression,
        VariableScope scope,
        IReadOnlyDictionary<string, Substitution>? substitutions,
        out string parameterName,
        out IrExpression body,
        out IReadOnlyDictionary<string, Substitution>? captures)
    {
        if (TryBuildLinqLambda(expression, scope, substitutions, out var lambda) &&
            lambda.Expression is IrLambdaExpression { Parameters.Count: 1, ExpressionBody: not null } single)
        {
            parameterName = single.Parameters[0].Name;
            body = single.ExpressionBody;
            captures = RestoreLinqSubstitutions(lambda.Captures);
            return true;
        }
        parameterName = string.Empty;
        body = null!;
        captures = null;
        return false;
    }

    private bool TryGetTwoParameterLambda(
        ExpressionSyntax expression,
        VariableScope scope,
        out string firstParameterName,
        out string secondParameterName,
        out IrExpression body,
        out IReadOnlyDictionary<string, Substitution>? captures)
    {
        if (!TryBuildLinqLambda(expression, scope, substitutions: null, out var lambda))
        {
            firstParameterName = string.Empty;
            secondParameterName = string.Empty;
            body = null!;
            captures = null;
            return false;
        }
        captures = RestoreLinqSubstitutions(lambda.Captures);
        if (lambda.Expression is IrLambdaExpression
            {
                Parameters.Count: 2,
                ExpressionBody: not null
            } lambdaIr)
        {
            firstParameterName = lambdaIr.Parameters[0].Name;
            secondParameterName = lambdaIr.Parameters[1].Name;
            body = lambdaIr.ExpressionBody;
            return true;
        }

        firstParameterName = string.Empty;
        secondParameterName = string.Empty;
        body = null!;
        captures = null;
        return false;
    }

    private bool TryGetTwoParameterLambda(
        IrExpression expression,
        VariableScope scope,
        IReadOnlyDictionary<string, Substitution>? substitutions,
        out string firstParameterName,
        out string secondParameterName,
        out IrExpression body,
        out IReadOnlyDictionary<string, Substitution>? captures)
    {
        if (TryBuildLinqLambda(expression, scope, substitutions, out var lambda) &&
            lambda.Expression is IrLambdaExpression { Parameters.Count: 2, ExpressionBody: not null } pair)
        {
            firstParameterName = pair.Parameters[0].Name;
            secondParameterName = pair.Parameters[1].Name;
            body = pair.ExpressionBody;
            captures = RestoreLinqSubstitutions(lambda.Captures);
            return true;
        }
        firstParameterName = string.Empty;
        secondParameterName = string.Empty;
        body = null!;
        captures = null;
        return false;
    }

    private static bool IsDelegateType(string name) =>
        name.StartsWith("Func<", StringComparison.Ordinal) ||
        name.StartsWith("Action<", StringComparison.Ordinal) ||
        name.StartsWith("Predicate<", StringComparison.Ordinal);

    private static IrType GroupingType(IrType keyType, IrType elementType) => new(
        $"IGrouping<{keyType.Name},{elementType.Name}>",
        keyType.IsBoolean,
        keyType.IsString,
        keyType.IsReference,
        keyType);

    private static bool IsGroupingType(string name) =>
        name.StartsWith("IGrouping<", StringComparison.Ordinal);

    private static bool IsLinqSequenceType(string name) =>
        KnownTypeFacts.IsLinqSequence(name);

    private static bool IsSumType(IrType type) => type.Name is
        "int" or "long" or "float" or "double" or "decimal";
}
