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

}

