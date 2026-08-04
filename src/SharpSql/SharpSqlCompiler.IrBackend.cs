using System.Globalization;

namespace SharpSql;

public sealed partial class SharpSqlCompiler
{
    private bool ContainsRuntimeExpression(IrExpression expression)
    {
        if (IsRuntimeNode(expression))
            return true;
        return expression switch
        {
            IrBinaryExpression binary =>
                ContainsRuntimeExpression(binary.Left) || ContainsRuntimeExpression(binary.Right),
            IrUnaryExpression unary => ContainsRuntimeExpression(unary.Operand),
            IrConversionExpression conversion => ContainsRuntimeExpression(conversion.Operand),
            IrAwaitExpression awaitExpression => ContainsRuntimeExpression(awaitExpression.Operand),
            IrConditionalExpression conditional =>
                ContainsRuntimeExpression(conditional.Condition) ||
                ContainsRuntimeExpression(conditional.WhenTrue) ||
                ContainsRuntimeExpression(conditional.WhenFalse),
            IrMemberExpression member => ContainsRuntimeExpression(member.Receiver),
            IrElementExpression element =>
                ContainsRuntimeExpression(element.Receiver) || element.Arguments.Any(ContainsRuntimeExpression),
            IrInvocationExpression invocation =>
                ContainsRuntimeExpression(invocation.Target) || invocation.Arguments.Any(ContainsRuntimeExpression),
            IrObjectCreationExpression creation =>
                creation.Arguments.Any(ContainsRuntimeExpression) || creation.Initializers.Any(ContainsRuntimeExpression),
            IrWithExpression withExpression =>
                ContainsRuntimeExpression(withExpression.Receiver) ||
                withExpression.Initializers.Any(ContainsRuntimeExpression),
            IrArrayCreationExpression array =>
                array.Length is not null && ContainsRuntimeExpression(array.Length) ||
                array.Elements.Any(ContainsRuntimeExpression),
            IrInterpolatedStringExpression interpolated => interpolated.Parts
                .OfType<IrInterpolation>()
                .Any(item => ContainsRuntimeExpression(item.Expression)),
            IrAssignmentExpression assignment =>
                ContainsRuntimeExpression(assignment.Target) || ContainsRuntimeExpression(assignment.Value),
            IrLambdaExpression lambda => LambdaContainsRuntimeExpression(lambda),
            IrQueryExpression query => ContainsRuntimeExpression(query.SourceExpression),
            _ => false
        };
    }

    private bool LambdaContainsRuntimeExpression(IrLambdaExpression lambda)
    {
        if (lambda.ExpressionBody is null)
            return false;
        if (lambda.ExpressionBody is IrInvocationExpression invocation &&
            CanEmitRuntimeDispatchScalar(invocation))
            return false;
        return ContainsRuntimeExpression(lambda.ExpressionBody);
    }

    private bool IsRuntimeNode(IrExpression expression)
    {
        switch (expression)
        {
            case IrElementExpression:
                return true;
            case IrArrayCreationExpression:
                return true;
            case IrObjectCreationExpression creation:
                return _heapTypes.ContainsKey(creation.CreatedType.Name) ||
                    KnownTypeFacts.IsList(creation.CreatedType.Name) ||
                    KnownTypeFacts.IsDictionary(creation.CreatedType.Name) ||
                    KnownTypeFacts.IsRandom(creation.CreatedType.Name);
            case IrWithExpression withExpression:
                return _heapTypes.ContainsKey(withExpression.Type.Name);
            case IrInvocationExpression invocation when TryGetRuntimeDispatch(invocation, out _):
                return true;
            case IrInvocationExpression invocation when
                TryGetMethod(invocation, out var vmMethod) && _vmMethods.ContainsKey(vmMethod.Id):
                return true;
            case IrInvocationExpression invocation when
                TryGetMethod(invocation, out var bytecodeMethod) && _bytecodeMethods.ContainsKey(bytecodeMethod.Id):
                return true;
            case IrInvocationExpression invocation when invocation.Target is IrMemberExpression member:
                var method = invocation.MethodName ?? string.Empty;
                var receiverType = member.Receiver.Type.Name;
                return KnownTypeFacts.IsRandom(receiverType) && method is "Next" or "NextDouble" ||
                    IntrinsicCatalog.IsMaterializer(method) &&
                    (IsSequenceType(receiverType) || KnownTypeFacts.IsLinqSequence(receiverType)) ||
                    IntrinsicCatalog.IsGuardedLinqOperator(method) &&
                    (IsSequenceType(receiverType) || KnownTypeFacts.IsLinqSequence(receiverType));
            default:
                return false;
        }
    }

    private void EmitExpressionStatement(
        IrExpression expression,
        VariableScope scope,
        InlineReturn? inlineReturn,
        string? namePrefix)
    {
        if (expression is IrAssignmentExpression assignment)
        {
            if (TryEmitHeapStatement(assignment, scope, _proceduralVmContext))
                return;
            if (ContainsRuntimeExpression(assignment.Value))
            {
                var target = EmitIrAssignable(assignment.Target, scope);
                EmitVmExpression(
                    assignment.Value,
                    scope,
                    _proceduralVmContext,
                    value => _sql.Line(IrAssignmentLine(assignment, target, assignment.Target.Type, value, parenthesizeValue: true)));
                return;
            }
            if (assignment.Value is IrInvocationExpression invocation &&
                TryGetComplexMethod(invocation, out var method))
            {
                EmitComplexInline(
                    method,
                    InvocationArgumentExpressions(invocation, method),
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
                    _proceduralVmContext,
                    value => EmitPrintSql(FormatTextValue(invocationExpression.Arguments[0].Type, value)));
            else
                EmitPrintSql(FormatTextValue(
                    invocationExpression.Arguments[0].Type,
                    EmitScalar(invocationExpression.Arguments[0], scope)));
            return;
        }

        if (TryEmitHeapStatement(expression, scope, _proceduralVmContext))
            return;

        if (expression is IrInvocationExpression bytecodeCall &&
            TryGetRegisterBytecodeMethod(bytecodeCall, out _))
        {
            EmitVmExpression(bytecodeCall, scope, _proceduralVmContext, _ => { });
            return;
        }

        if (expression is IrInvocationExpression call &&
            TryGetComplexMethod(call, out var complexMethod))
        {
            EmitComplexInline(
                complexMethod,
                InvocationArgumentExpressions(call, complexMethod),
                scope,
                null,
                IrType.Unknown,
                false);
            return;
        }
        if (ContainsRuntimeExpression(expression))
        {
            EmitVmExpression(expression, scope, _proceduralVmContext, _ => { });
            return;
        }
        if (expression is IrInvocationExpression discardedCall &&
            TryGetMethod(discardedCall, out var discardedMethod))
        {
            if (discardedMethod.Behavior.IsSideEffectFree && InvocationInputsAreDiscardable(discardedCall))
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
            case IrAwaitExpression awaitExpression:
                UnsupportedAwait(awaitExpression);
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
            IrDefaultValueExpression => SqlScalarExpression.Primary(DefaultSql(expression.Type)),
            IrVariableExpression variable => EmitIrVariable(variable, scope, substitutions),
            IrThisExpression @this => substitutions is not null && substitutions.TryGetValue("this", out var thisValue)
                ? thisValue.Expression
                : scope.Find(@this.Symbol) is ScalarVariableBinding thisBinding
                    ? thisBinding.Scalar
                    : SqlScalarExpression.Primary("NULL"),
            IrBinaryExpression binary => EmitIrBinary(binary, scope, substitutions),
            IrUnaryExpression unary => EmitIrUnary(unary, scope, substitutions),
            IrConversionExpression conversion => EmitScalarExpression(conversion.Operand, scope, substitutions).CastTo(conversion.TargetType),
            IrAwaitExpression awaitExpression => SqlScalarExpression.Primary(UnsupportedAwait(awaitExpression)),
            IrConditionalExpression conditional => SqlScalarExpression.Primary(
                $"CASE WHEN {EmitPredicate(conditional.Condition, scope, substitutions)} THEN {EmitScalar(conditional.WhenTrue, scope, substitutions)} ELSE {EmitScalar(conditional.WhenFalse, scope, substitutions)} END"),
            IrInterpolatedStringExpression interpolated => EmitIrInterpolatedString(interpolated, scope, substitutions),
            IrInvocationExpression invocation => EmitIrInvocation(invocation, scope, substitutions),
            IrMemberExpression member => EmitIrMember(member, scope, substitutions),
            IrElementExpression element => EmitIrElement(element, scope, substitutions),
            IrObjectCreationExpression creation => EmitIrObjectCreation(creation, scope, substitutions),
            IrUnsupportedExpression unsupported => SqlScalarExpression.Primary(
                UnsupportedExpression(unsupported.Source, unsupported.Description)),
            _ => SqlScalarExpression.Primary(
                UnsupportedExpression(expression.Source, $"Unsupported IR expression: {expression.GetType().Name}."))
        };
        return result.WithAnalysis(expression.Type, expression.Facts.Nullability);
    }

    private string UnsupportedAwait(IrAwaitExpression awaitExpression) =>
        UnsupportedExpression(
            awaitExpression.Source,
            "Await expressions require async scheduling, which is not supported by the SQL backend.");

    private SqlScalarExpression EmitIrInvocation(
        IrInvocationExpression invocation,
        VariableScope scope,
        IReadOnlyDictionary<string, Substitution>? substitutions)
    {
        if (IntrinsicCatalog.IsThreadGetCurrentProcessorId(invocation))
            return SqlScalarExpression.Primary("CONVERT(INT, @@SPID)", IrType.Int, ScalarNullability.NonNull);
        if (TryEmitLinqInvocation(invocation, scope, substitutions, out var linqExpression))
            return linqExpression;
        if (TryEmitHeapInvocationScalar(invocation, scope, substitutions, out var heapExpression))
            return heapExpression;
        if (TryEmitRuntimeDispatchScalar(invocation, scope, substitutions, out var dispatchExpression))
            return dispatchExpression;

        if (TryGetMethod(invocation, out var method) && method.PureExpression is not null)
        {
            EmitLeadingComments(method.Source);
            if (method.Body?.Statements.OfType<ProceduralReturn>().FirstOrDefault() is { } returnStatement)
                EmitLeadingComments(returnStatement.Source);
            var arguments = InvocationArgumentExpressions(invocation, method);
            if (!CanInline(method, arguments.Count))
                return SqlScalarExpression.Primary("NULL");

            var replacements = new Dictionary<string, Substitution>(StringComparer.Ordinal);
            var planReplacements = new Dictionary<string, SqlLinqQueryPlan>(StringComparer.Ordinal);
            var lambdaReplacements = new Dictionary<string, SqlLinqLambdaPlan>(StringComparer.Ordinal);
            for (var index = 0; index < method.Parameters.Count; index++)
            {
                var parameter = method.Parameters[index];
                var argument = arguments[index];
                if (TryBuildLinqLambda(argument, scope, substitutions, out var lambdaArgument))
                {
                    lambdaReplacements[parameter.Name] = lambdaArgument;
                    continue;
                }
                if ((IsSequenceType(parameter.Type.Name) || KnownTypeFacts.IsLinqSequence(parameter.Type.Name)) &&
                    TryBuildLinqQuery(argument, scope, substitutions, out var argumentQuery))
                {
                    planReplacements[parameter.Name] = argumentQuery;
                    continue;
                }
                replacements[parameter.Name] = new Substitution(
                    EmitScalarExpression(argument, scope, substitutions));
            }

            _linqPlanSubstitutions.Push(planReplacements);
            _linqLambdaSubstitutions.Push(lambdaReplacements);
            try
            {
                return EmitScalarExpression(method.PureExpression, scope, replacements);
            }
            finally
            {
                _linqLambdaSubstitutions.Pop();
                _linqPlanSubstitutions.Pop();
            }
        }

        return SqlScalarExpression.Primary(
            UnsupportedExpression(invocation.Source, "Only user-defined methods and supported intrinsics can be invoked."));
    }

    private SqlScalarExpression EmitIrMember(
        IrMemberExpression member,
        VariableScope scope,
        IReadOnlyDictionary<string, Substitution>? substitutions)
    {
        if (member.Receiver is IrVariableExpression exceptionVariable &&
            scope.Find(exceptionVariable.Symbol) is ExceptionVariableBinding exception)
        {
            var exceptionMember = member.MemberName switch
            {
                "Message" => exception.MessageSql,
                "Number" => exception.NumberSql,
                "Severity" => exception.SeveritySql,
                "State" => exception.StateSql,
                "Procedure" => exception.ProcedureSql,
                "LineNumber" => exception.LineNumberSql,
                _ => null
            };
            if (exceptionMember is not null)
                return SqlScalarExpression.Primary(exceptionMember);
            return SqlScalarExpression.Primary(
                UnsupportedExpression(member.Source, $"Exception member '{member.MemberName}' is not available in SQL Server CATCH metadata."));
        }

        var receiverType = member.Receiver.Type;
        var receiver = EmitScalar(member.Receiver, scope, substitutions);
        if (IsGroupingType(receiverType.Name) && member.MemberName == "Key")
            return SqlScalarExpression.Primary(receiver);
        if (receiverType.IsString && member.MemberName == "Length")
            return SqlScalarExpression.Primary($"CONVERT(INT, DATALENGTH({receiver}) / 2)");
        if (receiverType.Name == "byte[]" && member.MemberName == "Length")
            return SqlScalarExpression.Primary(ByteArrayLengthSql(receiver));
        if ((IsListType(receiverType.Name) && member.MemberName == "Count") ||
            (IsArrayType(receiverType.Name) && member.MemberName == "Length") ||
            (IsDictionaryType(receiverType.Name) && member.MemberName == "Count"))
            return SqlScalarExpression.Primary($"(SELECT __count FROM {HeapObjects} WHERE {HeapObjectExecutionFilter()}__id = {receiver})");
        if (TryResolveHeapField(
                receiverType,
                member.MemberName,
                member.MemberId,
                out var heapType,
                out var field))
            return SqlScalarExpression.Primary(HeapFieldReadValue(heapType, field, receiver));
        return SqlScalarExpression.Primary(
            UnsupportedExpression(member.Source, $"Unknown member '{member.MemberName}' on '{receiverType.Name}'."));
    }

    private SqlScalarExpression EmitIrElement(
        IrElementExpression element,
        VariableScope scope,
        IReadOnlyDictionary<string, Substitution>? substitutions)
    {
        if (element.Arguments.Count != 1)
            return SqlScalarExpression.Primary(
                UnsupportedExpression(element.Source, "Only single-argument indexing is supported."));

        var receiver = EmitScalar(element.Receiver, scope, substitutions);
        var key = EmitScalar(element.Arguments[0], scope, substitutions);
        if (TryGetHeapElementSql(element.Receiver.Type, receiver, key, out var value))
            return SqlScalarExpression.Primary(value);
        return SqlScalarExpression.Primary(
            UnsupportedExpression(element.Source, "Only string, list, array, and dictionary indexing is supported."));
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
        if (scope.Find(variable.Symbol) is ScalarVariableBinding binding)
            return binding.Scalar;
        if (TryResolveImplicitHeapField(
                variable.Symbol.Name,
                variable.Symbol.ReferencedMemberId,
                scope,
                substitutions,
                out var heapType,
                out var field,
                out var receiver))
            return SqlScalarExpression.Primary(HeapFieldReadValue(heapType, field, receiver));
        AddDiagnostic("SS4001", $"Unknown identifier '{variable.Symbol.Name}'.", variable.Source);
        return SqlScalarExpression.Primary("NULL");
    }

    private string EmitIrAssignable(IrExpression expression, VariableScope scope) => expression switch
    {
        IrVariableExpression variable when scope.Find(variable.Symbol) is ScalarVariableBinding binding => binding.SqlName,
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
        if (binary.Operator is IrBinaryOperator.LeftShift or IrBinaryOperator.RightShift)
        {
            var shiftLeft = EmitScalar(binary.Left, scope, substitutions);
            var shiftRight = EmitScalar(binary.Right, scope, substitutions);
            return SqlScalarExpression.Primary(binary.Operator == IrBinaryOperator.LeftShift
                ? LeftShiftSql(binary.Type, shiftLeft, shiftRight)
                : RightShiftSql(binary.Type, shiftLeft, shiftRight));
        }

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
        return SqlScalarExpression.Primary(UnsupportedExpression(
            creation.Source,
            "Only the string(char[]) scalar constructor is supported."));
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
        IrBinaryOperator.LeftShift => "<<",
        IrBinaryOperator.RightShift => ">>",
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
        IrBinaryOperator.LeftShift or IrBinaryOperator.RightShift => PrecedenceShift,
        _ => 50
    };

    private static string LeftShiftSql(IrType type, string left, string right) =>
        $"CONVERT({type.SqlType()}, ({left}) << (({right}) & {ShiftMask(type)}))";

    private static string RightShiftSql(IrType type, string left, string right)
    {
        var shift = $"(({right}) & {ShiftMask(type)})";
        if (type.Name is "uint" or "ulong")
            return $"({left}) >> {shift}";
        return $"CONVERT({type.SqlType()}, FLOOR(CONVERT(DECIMAL(38,0), ({left})) / " +
            $"POWER(CONVERT(DECIMAL(38,0), 2), {shift})))";
    }

    private static int ShiftMask(IrType type) => type.Name is "long" or "ulong" ? 63 : 31;
}
