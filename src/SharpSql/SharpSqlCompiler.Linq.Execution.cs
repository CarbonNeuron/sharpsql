using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SharpSql;

public sealed partial class SharpSqlCompiler
{
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
                $"(SELECT __count FROM {HeapObjects} WHERE {HeapObjectExecutionFilter()}__id = {heap.OwnerSql})",
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
        var column = IndexedItemValueColumn(query.ElementType);
        var value = IndexedItemStoredValue(query.ElementType, $"{sourceAlias}.__value");
        _sql.Line(
            $"INSERT INTO {HeapIndexedItems} ({IndexedItemInsertColumns($"__owner_id, __index, {column}")}) " +
            $"SELECT {IndexedItemInsertValues($"{collection}, CONVERT(INT, ROW_NUMBER() OVER (ORDER BY {sourceAlias}.__index) - 1), {value}")} " +
            $"FROM ({querySql}) AS {sourceAlias};");
        var materializedCount = _names.Allocate("_linq_materialized_count");
        _sql.Line($"DECLARE {materializedCount} INT = @@ROWCOUNT;");
        _sql.Line($"UPDATE {HeapObjects} SET __count = {materializedCount} WHERE {HeapObjectExecutionFilter()}__id = {collection};");
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
        var column = IndexedItemValueColumn(query.ElementType);
        var value = IndexedItemStoredValue(query.ElementType, $"{alias}.__value");
        _sql.Line(
            $"INSERT INTO {HeapIndexedItems} ({IndexedItemInsertColumns($"__owner_id, __index, {column}")}) " +
            $"SELECT {IndexedItemInsertValues($"{collection}, CONVERT(INT, ROW_NUMBER() OVER (ORDER BY {alias}.__index) - 1), {value}")} " +
            $"FROM ({querySql}) AS {alias};");
        var count = _names.Allocate("_linq_materialized_count");
        _sql.Line($"DECLARE {count} INT = @@ROWCOUNT;");
        _sql.Line($"UPDATE {HeapObjects} SET __count = {count} WHERE {HeapObjectExecutionFilter()}__id = {collection};");
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

}
