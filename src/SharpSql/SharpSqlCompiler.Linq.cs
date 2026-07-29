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

internal sealed record SqlLinqQueryPlan(
    string SourceSql,
    IrType SourceElementType,
    IrType ElementType,
    IReadOnlyList<SqlLinqQueryStep> Steps);

public sealed partial class SharpSqlCompiler
{
    private int _linqId;
    private readonly Stack<IReadOnlyDictionary<string, SqlLinqQueryPlan>> _linqPlanSubstitutions = [];
    private readonly Stack<IReadOnlyDictionary<string, SqlLinqLambdaPlan>> _linqLambdaSubstitutions = [];

    private bool TryEmitLinqDelegateDeclaration(
        ExpressionSyntax initializer,
        string sourceName,
        string sqlName,
        IrType type,
        VariableScope scope)
    {
        if (!TryBuildLinqLambda(initializer, scope, substitutions: null, out var lambda))
            return false;
        _sql.Line($"DECLARE {sqlName} INT = NULL;");
        scope.Add(sourceName, new VariableBinding(sqlName, type, Lambda: lambda));
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
            _sql.Line($"DECLARE {sqlName} INT = NULL;");
            scope.Add(sourceName, new VariableBinding(sqlName, type));
            return true;
        }

        _sql.Line($"DECLARE {sqlName} INT = {query.SourceSql};");
        scope.Add(sourceName, new VariableBinding(
            sqlName,
            type,
            query with { SourceSql = sqlName }));
        return true;
    }

    private bool IsLinqQueryExpression(ExpressionSyntax expression, VariableScope scope)
    {
        expression = StripParentheses(expression);
        if (expression is QueryExpressionSyntax)
            return true;
        if (expression is IdentifierNameSyntax identifier &&
            scope.Find(identifier.Identifier.ValueText)?.Query is not null)
            return true;
        if (expression is InvocationExpressionSyntax userInvocation &&
            _methods.TryGetValue(InvocationName(userInvocation.Expression) ?? string.Empty, out var userMethod) &&
            userMethod.PureExpression is not null &&
            IsLinqSequenceType(userMethod.ReturnType.Name))
            return true;
        if (expression is InvocationExpressionSyntax invocation && IsEnumerableRangeInvocation(invocation))
            return true;
        return expression is InvocationExpressionSyntax
        {
            Expression: MemberAccessExpressionSyntax
            {
                Name.Identifier.ValueText: "AsEnumerable" or "AsQueryable" or "Where" or "Select" or
                    "OrderBy" or "OrderByDescending" or "ThenBy" or "ThenByDescending" or
                    "Distinct" or "Skip" or "Take" or "GroupBy" or "Join"
            }
        };
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
            scope.Find(identifier.Identifier.ValueText)?.Query is { } storedQuery)
        {
            query = storedQuery;
            return true;
        }

        if (expression is QueryExpressionSyntax queryExpression)
            return TryBuildQueryExpression(queryExpression, scope, substitutions, out query);

        if (expression is InvocationExpressionSyntax rangeInvocation &&
            TryBuildEnumerableRangeQuery(rangeInvocation, scope, substitutions, out query))
            return true;

        if (expression is InvocationExpressionSyntax userInvocation &&
            _methods.TryGetValue(InvocationName(userInvocation.Expression) ?? string.Empty, out var userMethod) &&
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
                : new Dictionary<string, Substitution>(substitutions, StringComparer.Ordinal);
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
                query = new SqlLinqQueryPlan(collection, itemType, itemType, []);
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
                EmitScalar(expression, scope, substitutions),
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

        var collection = AllocateHeapHeader(1003, "__count", count);
        var index = _names.Allocate("_range_index");
        _sql.Line($"DECLARE {index} INT = 0;");
        _sql.Line($"WHILE {index} < {count}");
        _sql.Line("BEGIN");
        using (_sql.Indent())
        {
            InsertListItem(collection, index, IrType.Int, $"{start} + {index}");
            _sql.Line($"SET {index} = {index} + 1;");
        }
        _sql.Line("END;");

        query = new SqlLinqQueryPlan(collection, IrType.Int, IrType.Int, []);
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
        var itemAlias = NextLinqAlias("item");
        var sql = $"SELECT {itemAlias}.__index AS __index, " +
            $"{CollectionReadValue(query.SourceElementType, key: false, qualifier: itemAlias)} AS __value " +
            $"FROM {HeapIndexedItems} AS {itemAlias} WHERE {itemAlias}.__owner_id = {query.SourceSql}";

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
                var numbered = $"SELECT ROW_NUMBER() OVER (ORDER BY {sourceAlias}.__index) - 1 AS __ordinal, {sourceAlias}.__value FROM ({sql}) AS {sourceAlias}";
                sql = paging.IsSkip
                    ? $"SELECT CONVERT(INT, {numberedAlias}.__ordinal - ({normalizedCount})) AS __index, {numberedAlias}.__value FROM ({numbered}) AS {numberedAlias} WHERE {numberedAlias}.__ordinal >= ({normalizedCount})"
                    : $"SELECT CONVERT(INT, {numberedAlias}.__ordinal) AS __index, {numberedAlias}.__value FROM ({numbered}) AS {numberedAlias} WHERE {numberedAlias}.__ordinal < ({normalizedCount})";
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
                    : new Dictionary<string, Substitution>(joinSubstitutions, StringComparer.Ordinal);
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

    private bool ContainsGuardedLinqExpression(ExpressionSyntax expression) =>
        expression.DescendantNodesAndSelf()
            .OfType<InvocationExpressionSyntax>()
            .Any(IsGuardedLinqInvocation);

    private bool IsGuardedLinqInvocation(InvocationExpressionSyntax invocation)
    {
        if (invocation.Expression is not MemberAccessExpressionSyntax member ||
            member.Name.Identifier.ValueText is not (
                "First" or "Last" or "Single" or "SingleOrDefault" or
                "ElementAt" or "ElementAtOrDefault" or "Min" or "Max" or "Average" or
                "MinBy" or "MaxBy"))
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
        var sourceAlias = NextLinqAlias("guarded");
        if (method is "ElementAt" or "ElementAtOrDefault")
        {
            EmitVmExpression(arguments[0].Expression, scope, context, index =>
            {
                var indexStorage = AllocateVmTemporary(IrType.Int, context);
                StoreVmTemporary(indexStorage, index);
                var savedIndex = ReadVmTemporary(indexStorage);
                if (method == "ElementAt")
                    _sql.Line($"IF {savedIndex} < 0 OR {savedIndex} >= (SELECT COUNT(*) FROM ({querySql}) AS {sourceAlias}) THROW 51009, 'LINQ index was out of range.', 1;");
                var element = LinqElementAtSql(querySql, sourceAlias, savedIndex);
                continuation(method == "ElementAt"
                    ? element
                    : $"COALESCE({element}, {DefaultSql(query.ElementType)})");
            });
            return true;
        }

        var countSql = $"(SELECT COUNT(*) FROM ({querySql}) AS {sourceAlias})";
        if (method is "First" or "Last" or "Single")
            _sql.Line($"IF {countSql} = 0 THROW 51007, 'LINQ sequence contains no elements.', 1;");
        if (method is "Single" or "SingleOrDefault")
            _sql.Line($"IF {countSql} > 1 THROW 51008, 'LINQ sequence contains more than one element.', 1;");
        if (method is "Min" or "Max" or "Average" or "MinBy" or "MaxBy" &&
            LinqResultThrowsOnEmpty(invocation))
            _sql.Line($"IF {countSql} = 0 THROW 51007, 'LINQ sequence contains no elements.', 1;");

        var terminalAlias = NextLinqAlias("guarded_value");
        var resultType = InferType(invocation, scope);
        var value = method switch
        {
            "First" or "Single" =>
                $"(SELECT TOP (1) {terminalAlias}.__value FROM ({querySql}) AS {terminalAlias} ORDER BY {terminalAlias}.__index)",
            "Last" =>
                $"(SELECT TOP (1) {terminalAlias}.__value FROM ({querySql}) AS {terminalAlias} ORDER BY {terminalAlias}.__index DESC)",
            "SingleOrDefault" =>
                $"COALESCE((SELECT TOP (1) {terminalAlias}.__value FROM ({querySql}) AS {terminalAlias}), {DefaultSql(query.ElementType)})",
            "Min" => $"(SELECT MIN({terminalAlias}.__value) FROM ({querySql}) AS {terminalAlias})",
            "Max" => $"(SELECT MAX({terminalAlias}.__value) FROM ({querySql}) AS {terminalAlias})",
            "Average" => $"(SELECT AVG(CONVERT({resultType.SqlType()}, {terminalAlias}.__value)) FROM ({querySql}) AS {terminalAlias})",
            "MinBy" or "MaxBy" =>
                $"(SELECT TOP (1) {terminalAlias}.__value FROM ({querySql}) AS {terminalAlias} ORDER BY {terminalAlias}.__index)",
            _ => "NULL"
        };
        continuation(value);
        return true;
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
        var sourceAlias = NextLinqAlias("foreach");

        _sql.Line($"DECLARE {lastIndex} INT = -1;");
        _sql.Line($"DECLARE {nextIndex} INT;");
        _sql.Line($"DECLARE {itemSql} {itemType.SqlType()};");
        scope.Add(statement.Element, new VariableBinding(itemSql, itemType));
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
    }

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
            member.Name.Identifier.ValueText is not ("ToList" or "ToArray"))
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
            member.Name.Identifier.ValueText == "ToList" ? 1001 : 1003,
            "__count",
            "0");
        var column = CollectionValueColumn(query.ElementType, key: false);
        var value = CollectionStoredValue(query.ElementType, $"{sourceAlias}.__value");
        _sql.Line(
            $"INSERT INTO {HeapIndexedItems} (__owner_id, __index, {column}) " +
            $"SELECT {collection}, CONVERT(INT, ROW_NUMBER() OVER (ORDER BY {sourceAlias}.__index) - 1), {value} " +
            $"FROM ({querySql}) AS {sourceAlias};");
        var materializedCount = _names.Allocate("_linq_materialized_count");
        _sql.Line($"DECLARE {materializedCount} INT = @@ROWCOUNT;");
        _sql.Line($"UPDATE {HeapObjects} SET __count = {materializedCount} WHERE __id = {collection};");
        continuation(collection);
        return true;
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
                    materializationMember.Name.Identifier.ValueText == "ToList" ? 1001 : 1003,
                    "__count",
                    savedCount);
                var index = _names.Allocate("_repeat_index");
                var selectorScope = scope.Child();
                selectorScope.Add(parameterName, new VariableBinding(
                    ReadVmTemporary(repeatedStorage),
                    repeatedType));

                _sql.Line($"DECLARE {index} INT = 0;");
                _sql.Line($"WHILE {index} < {savedCount}");
                _sql.Line("BEGIN");
                using (_sql.Indent())
                {
                    EmitVmExpression(selectorBody, selectorScope, context, value =>
                    {
                        InsertListItem(collection, index, resultType, value);
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
            member.Name.Identifier.ValueText is not ("ToList" or "ToArray"))
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
            : new Dictionary<string, Substitution>(substitutions, StringComparer.Ordinal);
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
        var merged = new Dictionary<string, Substitution>(captured, StringComparer.Ordinal);
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
            if (scope.Find(identifier.Identifier.ValueText)?.Lambda is { } storedLambda)
            {
                lambda = storedLambda;
                return true;
            }
        }

        if (expression is InvocationExpressionSyntax invocation &&
            _methods.TryGetValue(InvocationName(invocation.Expression) ?? string.Empty, out var method) &&
            method.PureExpression is not null &&
            IsDelegateType(method.ReturnType.Name))
        {
            var arguments = InvocationArgumentExpressions(invocation, method);
            if (arguments.Count == method.Parameters.Count && CanInline(method, arguments.Count))
            {
                var scalarReplacements = substitutions is null
                    ? new Dictionary<string, Substitution>(StringComparer.Ordinal)
                    : new Dictionary<string, Substitution>(substitutions, StringComparer.Ordinal);
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
            ? new Dictionary<string, SqlLinqScalarCapture>(scalarCaptures, StringComparer.Ordinal)
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
            if (scope.Find(name) is { } binding)
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
        name.StartsWith("IEnumerable<", StringComparison.Ordinal) ||
        name.StartsWith("IQueryable<", StringComparison.Ordinal) ||
        name.StartsWith("IOrderedEnumerable<", StringComparison.Ordinal) ||
        name.StartsWith("IOrderedQueryable<", StringComparison.Ordinal);

    private static bool IsSumType(IrType type) => type.Name is
        "int" or "long" or "float" or "double" or "decimal";
}
