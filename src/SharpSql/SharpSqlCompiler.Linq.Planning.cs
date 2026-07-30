using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SharpSql;

public sealed partial class SharpSqlCompiler
{
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

}
