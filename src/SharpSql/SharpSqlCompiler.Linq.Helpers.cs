using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SharpSql;

public sealed partial class SharpSqlCompiler
{
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

