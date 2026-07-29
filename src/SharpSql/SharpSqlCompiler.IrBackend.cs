using System.Globalization;

namespace SharpSql;

public sealed partial class SharpSqlCompiler
{
    private bool ContainsRuntimeExpression(IrExpression expression) =>
        HasCSharpSource(expression.Source) && ContainsRuntimeExpression(CSharpExpression(expression));

    private void EmitExpressionStatement(
        IrExpression expression,
        VariableScope scope,
        InlineReturn? inlineReturn,
        string? namePrefix)
    {
        if (expression is IrAssignmentExpression assignment)
        {
            if ((assignment.Target is not IrVariableExpression variableTarget || scope.Find(variableTarget.Symbol) is null) &&
                HasCSharpSource(expression.Source) &&
                TryEmitHeapStatement(CSharpExpression(expression), scope))
                return;
            if (ContainsRuntimeExpression(assignment.Value))
            {
                var target = EmitIrAssignable(assignment.Target, scope);
                EmitVmExpression(
                    assignment.Value,
                    scope,
                    null,
                    value => _sql.Line(IrAssignmentLine(assignment, target, assignment.Target.Type, value, parenthesizeValue: true)));
                return;
            }
            if (assignment.Value is IrInvocationExpression invocation &&
                HasCSharpSource(invocation.Source) &&
                TryGetComplexMethod(
                    CSharpSyntax<Microsoft.CodeAnalysis.CSharp.Syntax.InvocationExpressionSyntax>(invocation.Source),
                    out var method))
            {
                var invocationSyntax = CSharpSyntax<Microsoft.CodeAnalysis.CSharp.Syntax.InvocationExpressionSyntax>(invocation.Source);
                EmitComplexInline(
                    method,
                    InvocationArgumentExpressions(invocationSyntax, method),
                    scope,
                    EmitIrAssignable(assignment.Target, scope),
                    assignment.Target.Type,
                    declareTarget: false);
                return;
            }
            var targetSql = EmitIrAssignable(assignment.Target, scope);
            _sql.Line(IrAssignmentLine(assignment, targetSql, assignment.Target.Type, EmitScalar(assignment.Value, scope)));
            return;
        }

        if (expression is IrInvocationExpression invocationExpression && IsConsoleWrite(invocationExpression))
        {
            if (invocationExpression.Arguments.Count == 0)
                EmitPrintSql("N''");
            else if (ContainsRuntimeExpression(invocationExpression.Arguments[0]))
                EmitVmExpression(
                    invocationExpression.Arguments[0],
                    scope,
                    null,
                    value => EmitPrintSql(FormatTextValue(invocationExpression.Arguments[0].Type, value)));
            else
                EmitPrintSql(FormatTextValue(
                    invocationExpression.Arguments[0].Type,
                    EmitScalar(invocationExpression.Arguments[0], scope)));
            return;
        }

        // Heap and stateful runtime intrinsics are backend operations whose
        // recognizers currently share the C# frontend adapter.
        if (HasCSharpSource(expression.Source) && TryEmitHeapStatement(CSharpExpression(expression), scope))
            return;

        if (!HasCSharpSource(expression.Source))
        {
            Unsupported(expression.Source, "expression statement");
            return;
        }
        var legacy = CSharpExpression(expression);
        if (expression is IrInvocationExpression call &&
            TryGetComplexMethod(
                CSharpSyntax<Microsoft.CodeAnalysis.CSharp.Syntax.InvocationExpressionSyntax>(call.Source),
                out var complexMethod))
        {
            var invocation = CSharpSyntax<Microsoft.CodeAnalysis.CSharp.Syntax.InvocationExpressionSyntax>(call.Source);
            EmitComplexInline(complexMethod, InvocationArgumentExpressions(invocation, complexMethod), scope, null, IrType.Unknown, false);
            return;
        }
        if (ContainsRuntimeExpression(expression))
        {
            EmitVmExpression(expression, scope, null, _ => { });
            return;
        }
        if (expression is IrInvocationExpression discardedCall &&
            _methods.TryGetValue(discardedCall.MethodName ?? string.Empty, out var discardedMethod))
        {
            var invocation = CSharpSyntax<Microsoft.CodeAnalysis.CSharp.Syntax.InvocationExpressionSyntax>(discardedCall.Source);
            if (discardedMethod.Behavior.IsSideEffectFree && InvocationInputsAreDiscardable(invocation))
                return;

            if (discardedMethod.ReturnType.Name != "void" && discardedMethod.PureExpression is not null)
            {
                var discarded = _names.Allocate("_discarded");
                _sql.Line($"DECLARE {discarded} {discardedMethod.ReturnType.SqlType()} = {EmitScalar(discardedCall, scope)};");
                return;
            }
        }

        switch (expression)
        {
            case IrUnaryExpression
            {
                Operator: IrUnaryOperator.PreIncrement or IrUnaryOperator.PostIncrement or
                    IrUnaryOperator.PreDecrement or IrUnaryOperator.PostDecrement
            } unary:
                var target = EmitIrAssignable(unary.Operand, scope);
                var delta = unary.Operator is IrUnaryOperator.PreIncrement or IrUnaryOperator.PostIncrement ? "+ 1" : "- 1";
                _sql.Line($"SET {target} = {target} {delta};");
                return;
            case IrInvocationExpression:
                // Intrinsic calls with statement effects are handled above by heap/runtime lowerers.
                Unsupported(expression.Source, "expression statement");
                return;
            default:
                Unsupported(expression.Source, "expression statement");
                return;
        }
    }

    private string EmitScalar(
        IrExpression expression,
        VariableScope scope,
        IReadOnlyDictionary<string, Substitution>? substitutions = null) =>
        EmitScalarExpression(expression, scope, substitutions).Sql;

    private SqlScalarExpression EmitScalarExpression(
        IrExpression expression,
        VariableScope scope,
        IReadOnlyDictionary<string, Substitution>? substitutions = null)
    {
        EmitExpressionComments(expression);
        if (expression.Facts.Type.IsBoolean && IsPredicateShape(expression))
        {
            return SqlScalarExpression.Primary(
                $"CASE WHEN {EmitPredicate(expression, scope, substitutions)} THEN CAST(1 AS BIT) ELSE CAST(0 AS BIT) END",
                IrType.Bool,
                ScalarNullability.NonNull);
        }

        var result = expression switch
        {
            IrConstantExpression constant => SqlScalarExpression.Primary(EmitIrConstant(constant)),
            IrVariableExpression variable => EmitIrVariable(variable, scope, substitutions),
            IrThisExpression @this => substitutions is not null && substitutions.TryGetValue("this", out var thisValue)
                ? thisValue.Expression
                : scope.Find(@this.Symbol)?.Scalar ?? SqlScalarExpression.Primary("NULL"),
            IrBinaryExpression binary => EmitIrBinary(binary, scope, substitutions),
            IrUnaryExpression unary => EmitIrUnary(unary, scope, substitutions),
            IrConversionExpression conversion => EmitScalarExpression(conversion.Operand, scope, substitutions).CastTo(conversion.TargetType),
            IrConditionalExpression conditional => SqlScalarExpression.Primary(
                $"CASE WHEN {EmitPredicate(conditional.Condition, scope, substitutions)} THEN {EmitScalar(conditional.WhenTrue, scope, substitutions)} ELSE {EmitScalar(conditional.WhenFalse, scope, substitutions)} END"),
            IrInterpolatedStringExpression interpolated => EmitIrInterpolatedString(interpolated, scope, substitutions),
            IrInvocationExpression invocation when HasCSharpSource(invocation.Source) => EmitInvocation(
                CSharpSyntax<Microsoft.CodeAnalysis.CSharp.Syntax.InvocationExpressionSyntax>(invocation.Source),
                scope,
                substitutions),
            IrMemberExpression member when HasCSharpSource(member.Source) => EmitHeapMemberScalar(
                CSharpSyntax<Microsoft.CodeAnalysis.CSharp.Syntax.MemberAccessExpressionSyntax>(member.Source),
                scope,
                substitutions),
            IrElementExpression element when HasCSharpSource(element.Source) => EmitHeapElementScalar(
                CSharpSyntax<Microsoft.CodeAnalysis.CSharp.Syntax.ElementAccessExpressionSyntax>(element.Source),
                scope),
            IrObjectCreationExpression creation => EmitIrObjectCreation(creation, scope, substitutions),
            IrUnsupportedExpression unsupported => SqlScalarExpression.Primary(
                UnsupportedExpression(unsupported.Source, unsupported.Description)),
            _ => SqlScalarExpression.Primary(
                UnsupportedExpression(expression.Source, $"Unsupported IR expression: {expression.GetType().Name}."))
        };
        return result.WithAnalysis(expression.Type, expression.Facts.Nullability);
    }

    private string EmitPredicate(
        IrExpression expression,
        VariableScope scope,
        IReadOnlyDictionary<string, Substitution>? substitutions = null)
    {
        if (expression.Facts.HasConstantValue && expression.Facts.ConstantValue is bool constant)
            return constant ? "1 = 1" : "1 = 0";

        return expression switch
        {
            IrConstantExpression { Value: true } => "1 = 1",
            IrConstantExpression { Value: false } => "1 = 0",
            IrVariableExpression variable => $"{EmitScalar(variable, scope, substitutions)} = 1",
            IrUnaryExpression { Operator: IrUnaryOperator.LogicalNot } unary =>
                $"NOT ({EmitPredicate(unary.Operand, scope, substitutions)})",
            IrBinaryExpression { Operator: IrBinaryOperator.LogicalAnd } binary =>
                $"({EmitPredicate(binary.Left, scope, substitutions)}) AND ({EmitPredicate(binary.Right, scope, substitutions)})",
            IrBinaryExpression { Operator: IrBinaryOperator.LogicalOr } binary =>
                $"({EmitPredicate(binary.Left, scope, substitutions)}) OR ({EmitPredicate(binary.Right, scope, substitutions)})",
            IrBinaryExpression binary when
                binary.Operator is IrBinaryOperator.Equal or IrBinaryOperator.NotEqual &&
                (IsNull(binary.Left) || IsNull(binary.Right)) =>
                $"{EmitScalar(IsNull(binary.Left) ? binary.Right : binary.Left, scope, substitutions)} " +
                (binary.Operator == IrBinaryOperator.Equal ? "IS NULL" : "IS NOT NULL"),
            IrBinaryExpression binary when IsComparison(binary.Operator) =>
                $"{EmitScalar(binary.Left, scope, substitutions)} {SqlOperator(binary.Operator)} {EmitScalar(binary.Right, scope, substitutions)}",
            _ => $"{EmitScalar(expression, scope, substitutions)} = 1"
        };
    }

    private SqlScalarExpression EmitIrVariable(
        IrVariableExpression variable,
        VariableScope scope,
        IReadOnlyDictionary<string, Substitution>? substitutions)
    {
        if (substitutions is not null && substitutions.TryGetValue(variable.Symbol.Name, out var replacement))
            return replacement.Expression;
        if (scope.Find(variable.Symbol) is { } binding)
            return binding.Scalar;
        if (HasCSharpSource(variable.Source) && TryEmitImplicitHeapField(
                CSharpSyntax<Microsoft.CodeAnalysis.CSharp.Syntax.IdentifierNameSyntax>(variable.Source),
                scope,
                substitutions,
                out var heapField))
            return heapField;
        AddDiagnostic("SS4001", $"Unknown identifier '{variable.Symbol.Name}'.", variable.Source);
        return SqlScalarExpression.Primary("NULL");
    }

    private string EmitIrAssignable(IrExpression expression, VariableScope scope) => expression switch
    {
        IrVariableExpression variable when scope.Find(variable.Symbol) is { } binding => binding.SqlName,
        _ => UnsupportedExpression(expression.Source, "Only local variables can be assigned here.")
    };

    private string IrAssignmentLine(
        IrAssignmentExpression assignment,
        string target,
        IrType targetType,
        string value,
        bool parenthesizeValue = false)
    {
        if (assignment.Operator == IrAssignmentOperator.Assign)
            return $"SET {target} = {value};";
        var op = assignment.Operator switch
        {
            IrAssignmentOperator.Add => "+",
            IrAssignmentOperator.Subtract => "-",
            IrAssignmentOperator.Multiply => "*",
            IrAssignmentOperator.Divide => "/",
            IrAssignmentOperator.Remainder => "%",
            IrAssignmentOperator.BitwiseAnd => "&",
            IrAssignmentOperator.BitwiseOr => "|",
            IrAssignmentOperator.ExclusiveOr => "^",
            _ => string.Empty
        };
        if (targetType.IsString && op == "+")
            return $"SET {target} = CONCAT({target}, {value});";
        return $"SET {target} = {target} {op} {(parenthesizeValue ? $"({value})" : value)};";
    }

    private static bool IsConsoleWrite(IrInvocationExpression invocation) =>
        invocation.Target is IrMemberExpression
        {
            Receiver: IrVariableExpression { Symbol.Name: "Console" },
            MemberName: "WriteLine" or "Write"
        };

    private SqlScalarExpression EmitIrBinary(
        IrBinaryExpression binary,
        VariableScope scope,
        IReadOnlyDictionary<string, Substitution>? substitutions)
    {
        if (binary.Operator == IrBinaryOperator.Coalesce)
            return SqlScalarExpression.Primary($"COALESCE({EmitScalar(binary.Left, scope, substitutions)}, {EmitScalar(binary.Right, scope, substitutions)})");
        if (binary.Operator == IrBinaryOperator.Add &&
            (binary.Left.Type.IsString || binary.Right.Type.IsString))
            return SqlScalarExpression.Primary($"CONCAT({EmitScalar(binary.Left, scope, substitutions)}, {EmitScalar(binary.Right, scope, substitutions)})");

        var precedence = BinaryPrecedence(binary.Operator);
        var left = EmitScalarExpression(binary.Left, scope, substitutions).Render(precedence);
        var right = EmitScalarExpression(binary.Right, scope, substitutions).Render(precedence + 1);
        return new SqlScalarExpression(
            $"{left} {SqlOperator(binary.Operator)} {right}",
            binary.Type,
            precedence,
            binary.Facts.Nullability);
    }

    private SqlScalarExpression EmitIrUnary(
        IrUnaryExpression unary,
        VariableScope scope,
        IReadOnlyDictionary<string, Substitution>? substitutions)
    {
        var operand = EmitScalarExpression(unary.Operand, scope, substitutions).Render(PrecedenceUnary);
        return unary.Operator switch
        {
            IrUnaryOperator.Identity => new SqlScalarExpression($"+{operand}", unary.Type, PrecedenceUnary),
            IrUnaryOperator.Negate => new SqlScalarExpression($"-{operand}", unary.Type, PrecedenceUnary),
            IrUnaryOperator.BitwiseNot => new SqlScalarExpression($"~{operand}", unary.Type, PrecedenceUnary),
            IrUnaryOperator.LogicalNot => SqlScalarExpression.Primary(
                $"CASE WHEN NOT ({EmitPredicate(unary.Operand, scope, substitutions)}) THEN CAST(1 AS BIT) ELSE CAST(0 AS BIT) END",
                IrType.Bool,
                ScalarNullability.NonNull),
            _ => SqlScalarExpression.Primary(
                UnsupportedExpression(unary.Source, $"Unary mutation is not a scalar value: {unary.Operator}."))
        };
    }

    private SqlScalarExpression EmitIrInterpolatedString(
        IrInterpolatedStringExpression interpolated,
        VariableScope scope,
        IReadOnlyDictionary<string, Substitution>? substitutions)
    {
        var parts = interpolated.Parts.Select(part => part switch
        {
            IrInterpolatedText text => "N'" + EscapeSqlString(text.Text) + "'",
            IrInterpolation interpolation => EmitIrInterpolation(interpolation.Expression, scope, substitutions),
            _ => "N''"
        }).ToArray();
        return parts.Length switch
        {
            0 => SqlScalarExpression.Primary("N''"),
            1 when interpolated.Parts[0] is IrInterpolatedText => SqlScalarExpression.Primary(parts[0]),
            1 => SqlScalarExpression.Primary($"CONCAT(N'', {parts[0]})"),
            _ => SqlScalarExpression.Primary($"CONCAT({string.Join(", ", parts)})")
        };
    }

    private string EmitIrInterpolation(
        IrExpression expression,
        VariableScope scope,
        IReadOnlyDictionary<string, Substitution>? substitutions)
    {
        return FormatTextValue(expression.Type, EmitScalar(expression, scope, substitutions));
    }

    private SqlScalarExpression EmitIrObjectCreation(
        IrObjectCreationExpression creation,
        VariableScope scope,
        IReadOnlyDictionary<string, Substitution>? substitutions)
    {
        if (creation.CreatedType.IsString &&
            creation.Arguments is [var characters] &&
            characters.Type.Name == "char[]")
            return SqlScalarExpression.Primary(StringFromCharacterArraySql(EmitScalar(characters, scope, substitutions)));
        if (!HasCSharpSource(creation.Source))
            return SqlScalarExpression.Primary(UnsupportedExpression(creation.Source, "Unsupported object construction."));
        return EmitIntrinsicObjectCreation(
            CSharpSyntax<Microsoft.CodeAnalysis.CSharp.Syntax.ObjectCreationExpressionSyntax>(creation.Source),
            scope,
            substitutions);
    }

    private static string EmitIrConstant(IrConstantExpression constant)
    {
        if (constant.Value is null)
            return "NULL";
        return constant.Value switch
        {
            bool value => value ? "CAST(1 AS BIT)" : "CAST(0 AS BIT)",
            string value => "N'" + EscapeSqlString(value) + "'",
            char value => "N'" + EscapeSqlString(value.ToString()) + "'",
            float value => $"CAST({value.ToString("R", CultureInfo.InvariantCulture)} AS REAL)",
            double value => $"CAST({value.ToString("R", CultureInfo.InvariantCulture)} AS FLOAT)",
            decimal value => $"CAST({value.ToString(CultureInfo.InvariantCulture)} AS DECIMAL(38,18))",
            IFormattable value => value.ToString(null, CultureInfo.InvariantCulture),
            _ => constant.SourceText
        };
    }

    private static bool IsNull(IrExpression expression) =>
        expression is IrConstantExpression { Value: null };

    private static bool IsPredicateShape(IrExpression expression) => expression switch
    {
        IrBinaryExpression binary => IsComparison(binary.Operator) ||
            binary.Operator is IrBinaryOperator.LogicalAnd or IrBinaryOperator.LogicalOr,
        IrUnaryExpression { Operator: IrUnaryOperator.LogicalNot } => true,
        _ => false
    };

    private static bool IsComparison(IrBinaryOperator op) => op is
        IrBinaryOperator.Equal or IrBinaryOperator.NotEqual or
        IrBinaryOperator.LessThan or IrBinaryOperator.LessThanOrEqual or
        IrBinaryOperator.GreaterThan or IrBinaryOperator.GreaterThanOrEqual;

    private static string SqlOperator(IrBinaryOperator op) => op switch
    {
        IrBinaryOperator.Add => "+",
        IrBinaryOperator.Subtract => "-",
        IrBinaryOperator.Multiply => "*",
        IrBinaryOperator.Divide => "/",
        IrBinaryOperator.Remainder => "%",
        IrBinaryOperator.BitwiseAnd => "&",
        IrBinaryOperator.BitwiseOr => "|",
        IrBinaryOperator.ExclusiveOr => "^",
        IrBinaryOperator.Equal => "=",
        IrBinaryOperator.NotEqual => "<>",
        IrBinaryOperator.LessThan => "<",
        IrBinaryOperator.LessThanOrEqual => "<=",
        IrBinaryOperator.GreaterThan => ">",
        IrBinaryOperator.GreaterThanOrEqual => ">=",
        _ => string.Empty
    };

    private static int BinaryPrecedence(IrBinaryOperator op) => op switch
    {
        IrBinaryOperator.Multiply or IrBinaryOperator.Divide or IrBinaryOperator.Remainder => PrecedenceMultiplicative,
        IrBinaryOperator.Add or IrBinaryOperator.Subtract => PrecedenceAdditive,
        _ => 50
    };
}
