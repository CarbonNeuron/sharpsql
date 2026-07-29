using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SharpSql;

public sealed partial class SharpSqlCompiler
{
    private readonly Dictionary<string, VmMethod> _vmMethods = new(StringComparer.Ordinal);
    private readonly List<VmContinuation> _vmContinuations = [];
    private int _nextVmMethodId;
    private int _nextVmContinuationId;

    private const string VmStack = "#__sharpsql_stack";
    private const string VmSlots = "#__sharpsql_slots";
    private const string VmFrameId = "@__sharpsql_frame_id";
    private const string VmNewFrameId = "@__sharpsql_new_frame_id";
    private const string VmJump = "@__sharpsql_jump";
    private const string VmResult = "@__sharpsql_result";
    private const string VmTextResult = "@__sharpsql_text_result";
    private const string VmBinaryResult = "@__sharpsql_binary_result";

    private string VmDispatchLabel => "__sharpsql_dispatch";
    private string VmHaltLabel => "__sharpsql_halt";

    private void PrepareVmMethods()
    {
        var selected = _methods.Values
            .Where(method => _recursiveMethods.Contains(method.Name) || ExceedsInlineBudget(method) || MethodUsesRandom(method))
            .Select(method => method.Name)
            .ToHashSet(StringComparer.Ordinal);

        // A method which calls, or is called by, an outlined method must share the same
        // backend so calls never disappear into the scalar-expression emitter.
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var method in _methods.Values)
            {
                var callees = MethodCallees(method).ToArray();
                if (selected.Contains(method.Name))
                {
                    foreach (var callee in callees)
                        changed |= selected.Add(callee);
                }
                else if (callees.Any(selected.Contains))
                {
                    changed |= selected.Add(method.Name);
                }
            }
        }

        foreach (var method in _methods.Values.Where(method => selected.Contains(method.Name)))
            AddVmMethod(method);
    }

    private bool ExceedsInlineBudget(MethodDefinition method) =>
        method.StatementCount > _options.MaxInlineStatements ||
        _callCounts.GetValueOrDefault(method.Name) > _options.MaxInlineCallSites;

    private IEnumerable<string> MethodCallees(MethodDefinition method) =>
        CSharpSyntax<Microsoft.CodeAnalysis.SyntaxNode>(method.Source).DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Select(call => InvocationName(call.Expression))
            .Where(name => name is not null && _methods.ContainsKey(name))
            .Cast<string>();

    private void AddVmMethod(MethodDefinition definition)
    {
        var method = new VmMethod(
            definition,
            ++_nextVmMethodId,
            _names.AllocateLabel($"vm_{definition.Name}_entry"),
            definition.ReturnType.Name == "void" ? null : _names.Allocate($"_vm_{definition.Name}_return"));
        var slot = 1;

        foreach (var parameter in definition.Parameters)
            AddVmVariable(method, parameter.Symbol, slot++);

        var statements = definition.Body is null
            ? Array.Empty<ProceduralStatement>()
            : DescendantStatements(definition.Body).ToArray();
        var variables = statements.OfType<ProceduralDeclarationStatement>()
            .SelectMany(declaration => declaration.Declaration.Variables)
            .Concat(statements.OfType<ProceduralFor>()
                .Where(@for => @for.Declaration is not null)
                .SelectMany(@for => @for.Declaration!.Variables));
        foreach (var variable in variables)
        {
            var name = variable.Name;
            if (method.Variables.ContainsKey(name))
            {
                AddDiagnostic("SS5001", $"Shadowed local '{name}' is not supported by the stack-machine fallback yet.", variable.Source);
                continue;
            }

            AddVmVariable(method, variable.Symbol, slot++);
        }

        foreach (var forEach in statements.OfType<ProceduralForEach>())
        {
            if (method.Variables.ContainsKey(forEach.Element.Name))
                continue;
            AddVmVariable(method, forEach.Element, slot++);
        }

        method.NextTemporarySlot = slot;
        _vmMethods.Add(definition.Name, method);
    }

    private void AddVmVariable(VmMethod method, IrSymbol symbol, int slot)
    {
        var name = symbol.Name;
        var type = symbol.Type;
        var sqlName = _names.Allocate($"_vm_{method.Definition.Name}_{name}");
        var variable = new VmVariable(name, type, slot, sqlName);
        method.Variables.Add(name, variable);
        method.Scope.Add(symbol, new VariableBinding(sqlName, type));
    }

    private static IEnumerable<ProceduralStatement> DescendantStatements(ProceduralStatement statement)
    {
        yield return statement;
        switch (statement)
        {
            case ProceduralBlock block:
                foreach (var child in block.Statements)
                    foreach (var descendant in DescendantStatements(child))
                        yield return descendant;
                break;
            case ProceduralIf @if:
                foreach (var descendant in DescendantStatements(@if.Then))
                    yield return descendant;
                if (@if.Else is not null)
                    foreach (var descendant in DescendantStatements(@if.Else))
                        yield return descendant;
                break;
            case ProceduralWhile @while:
                foreach (var descendant in DescendantStatements(@while.Body))
                    yield return descendant;
                break;
            case ProceduralDo @do:
                foreach (var descendant in DescendantStatements(@do.Body))
                    yield return descendant;
                break;
            case ProceduralFor @for:
                foreach (var descendant in DescendantStatements(@for.Body))
                    yield return descendant;
                break;
            case ProceduralForEach forEach:
                foreach (var descendant in DescendantStatements(forEach.Body))
                    yield return descendant;
                break;
        }
    }

    private void EmitVmPreamble()
    {
        if (_vmMethods.Count == 0)
            return;

        _sql.Line("-- SharpSql stack-machine runtime");
        _sql.Line($"DROP TABLE IF EXISTS {VmSlots};");
        _sql.Line($"DROP TABLE IF EXISTS {VmStack};");
        _sql.Line($"CREATE TABLE {VmStack} (");
        using (_sql.Indent())
        {
            _sql.Line("__id BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,");
            _sql.Line("__function_id INT NOT NULL,");
            _sql.Line("__return_id INT NOT NULL");
        }
        _sql.Line(");");
        _sql.Line($"CREATE TABLE {VmSlots} (");
        using (_sql.Indent())
        {
            _sql.Line("__frame_id BIGINT NOT NULL,");
            _sql.Line("__slot_id INT NOT NULL,");
            _sql.Line("__value SQL_VARIANT NULL,");
            _sql.Line("__text_value NVARCHAR(MAX) NULL,");
            _sql.Line("__binary_value VARBINARY(MAX) NULL,");
            _sql.Line("PRIMARY KEY (__frame_id, __slot_id)");
        }
        _sql.Line(");");
        _sql.Line($"DECLARE {VmFrameId} BIGINT;");
        _sql.Line($"DECLARE {VmNewFrameId} BIGINT;");
        _sql.Line($"DECLARE {VmJump} INT;");
        _sql.Line($"DECLARE {VmResult} SQL_VARIANT;");
        _sql.Line($"DECLARE {VmTextResult} NVARCHAR(MAX);");
        _sql.Line($"DECLARE {VmBinaryResult} VARBINARY(MAX);");
        foreach (var variable in _vmMethods.Values.SelectMany(method => method.Variables.Values))
            _sql.Line($"DECLARE {variable.SqlName} {variable.Type.SqlType()};");
        foreach (var method in _vmMethods.Values.Where(method => method.ReturnSqlName is not null))
            _sql.Line($"DECLARE {method.ReturnSqlName} {method.Definition.ReturnType.SqlType()};");
        _sql.Line();
    }

    private void EmitVmEpilogue()
    {
        if (_vmMethods.Count == 0)
            return;

        _sql.Line($"GOTO {VmHaltLabel};");
        _sql.Line();
        foreach (var method in _vmMethods.Values)
            EmitVmMethod(method);

        EmitLabel(VmDispatchLabel);
        foreach (var continuation in _vmContinuations)
            _sql.Line($"IF {VmJump} = {continuation.Id} GOTO {continuation.Label};");
        _sql.Line($"GOTO {VmHaltLabel};");
        _sql.Line();
        EmitLabel(VmHaltLabel);
        _sql.Line($"DROP TABLE IF EXISTS {VmSlots};");
        _sql.Line($"DROP TABLE IF EXISTS {VmStack};");
    }

    private bool ContainsVmCall(ExpressionSyntax expression) =>
        expression.DescendantNodesAndSelf()
            .OfType<InvocationExpressionSyntax>()
            .Any(call => _vmMethods.ContainsKey(InvocationName(call.Expression) ?? string.Empty));

    private void EmitVmExpression(
        ExpressionSyntax expression,
        VariableScope scope,
        VmMethod? context,
        Action<string> continuation)
    {
        expression = StripParentheses(expression);
        if (TryEmitGuardedLinqExpression(expression, scope, context, continuation))
            return;
        if (TryEmitHeapExpression(expression, scope, context, continuation))
            return;
        if (!ContainsRuntimeExpression(expression))
        {
            continuation(EmitScalar(expression, scope));
            return;
        }

        switch (expression)
        {
            case InvocationExpressionSyntax invocation
                when _vmMethods.TryGetValue(InvocationName(invocation.Expression) ?? string.Empty, out var callee):
                EmitVmInvocation(invocation, callee, scope, context, continuation);
                return;
            case BinaryExpressionSyntax binary:
                EmitVmBinary(binary, scope, context, continuation);
                return;
            case PrefixUnaryExpressionSyntax prefix:
                EmitVmExpression(prefix.Operand, scope, context, operand =>
                {
                    var sql = prefix.Kind() switch
                    {
                        SyntaxKind.UnaryMinusExpression => $"-({operand})",
                        SyntaxKind.UnaryPlusExpression => $"+({operand})",
                        SyntaxKind.BitwiseNotExpression => $"~({operand})",
                        SyntaxKind.LogicalNotExpression =>
                            $"CASE WHEN {operand} = 1 THEN CAST(0 AS BIT) ELSE CAST(1 AS BIT) END",
                        _ => UnsupportedExpression(prefix)
                    };
                    continuation(sql);
                });
                return;
            case ConditionalExpressionSyntax conditional:
                EmitVmConditional(conditional, scope, context, continuation);
                return;
            case ObjectCreationExpressionSyntax creation
                when IsStringCharacterArrayCreation(creation, scope):
                EmitVmExpression(
                    creation.ArgumentList!.Arguments[0].Expression,
                    scope,
                    context,
                    characters => continuation(StringFromCharacterArraySql(characters)));
                return;
            case CheckedExpressionSyntax checkedExpression:
                EmitVmExpression(checkedExpression.Expression, scope, context, continuation);
                return;
            default:
                continuation(UnsupportedExpression(
                    expression,
                    "This call-containing expression is not supported by the stack-machine fallback yet."));
                return;
        }
    }

    private void EmitVmExpression(
        IrExpression expression,
        VariableScope scope,
        VmMethod? context,
        Action<string> continuation)
    {
        if (HasCSharpSource(expression.Source))
        {
            var syntax = CSharpExpression(expression);
            if (TryEmitGuardedLinqExpression(syntax, scope, context, continuation))
                return;
            if (TryEmitHeapExpression(syntax, scope, context, continuation))
                return;
            if (!ContainsRuntimeExpression(syntax))
            {
                continuation(EmitScalar(expression, scope));
                return;
            }
        }
        else
        {
            continuation(EmitScalar(expression, scope));
            return;
        }

        switch (expression)
        {
            case IrInvocationExpression invocation
                when invocation.MethodName is { } name && _vmMethods.TryGetValue(name, out var callee):
                EmitVmInvocation(invocation, callee, scope, context, continuation);
                return;
            case IrBinaryExpression binary:
                EmitVmBinary(binary, scope, context, continuation);
                return;
            case IrUnaryExpression unary:
                EmitVmExpression(unary.Operand, scope, context, operand =>
                {
                    var sql = unary.Operator switch
                    {
                        IrUnaryOperator.Negate => $"-({operand})",
                        IrUnaryOperator.Identity => $"+({operand})",
                        IrUnaryOperator.BitwiseNot => $"~({operand})",
                        IrUnaryOperator.LogicalNot =>
                            $"CASE WHEN {operand} = 1 THEN CAST(0 AS BIT) ELSE CAST(1 AS BIT) END",
                        _ => UnsupportedExpression(unary.Source, $"Unsupported VM unary operator {unary.Operator}.")
                    };
                    continuation(sql);
                });
                return;
            case IrConditionalExpression conditional:
                EmitVmConditional(conditional, scope, context, continuation);
                return;
            case IrInterpolatedStringExpression interpolated:
                EmitVmInterpolatedString(interpolated, scope, context, continuation);
                return;
            case IrMemberExpression member when HasCSharpSource(member.Source):
                EmitVmExpression(member.Receiver, scope, context, receiver =>
                    continuation(EmitHeapMemberScalar(
                        CSharpSyntax<MemberAccessExpressionSyntax>(member.Source),
                        scope,
                        receiverOverride: receiver).Sql));
                return;
            case IrObjectCreationExpression
                {
                    CreatedType.IsString: true,
                    Arguments: [var characters]
                } when characters.Type.Name == "char[]":
                EmitVmExpression(characters, scope, context, value =>
                    continuation(StringFromCharacterArraySql(value)));
                return;
            default:
                continuation(UnsupportedExpression(
                    expression.Source,
                    "This effectful IR expression is not supported by the stack-machine backend yet."));
                return;
        }
    }

    private void EmitVmInterpolatedString(
        IrInterpolatedStringExpression interpolated,
        VariableScope scope,
        VmMethod? context,
        Action<string> continuation)
    {
        var parts = new List<string>();
        EmitPart(0);

        void EmitPart(int index)
        {
            if (index == interpolated.Parts.Count)
            {
                continuation(parts.Count switch
                {
                    0 => "N''",
                    1 when interpolated.Parts[0] is IrInterpolatedText => parts[0],
                    1 => $"CONCAT(N'', {parts[0]})",
                    _ => $"CONCAT({string.Join(", ", parts)})"
                });
                return;
            }

            switch (interpolated.Parts[index])
            {
                case IrInterpolatedText text:
                    parts.Add("N'" + EscapeSqlString(text.Text) + "'");
                    EmitPart(index + 1);
                    break;
                case IrInterpolation interpolation:
                    EmitVmExpression(interpolation.Expression, scope, context, value =>
                    {
                        var storage = AllocateVmTemporary(interpolation.Expression.Type, context);
                        StoreVmTemporary(storage, value);
                        var storedValue = ReadVmTemporary(storage);
                        parts.Add(interpolation.Expression.Type.IsBoolean
                            ? $"CASE {storedValue} WHEN CAST(1 AS BIT) THEN N'True' WHEN CAST(0 AS BIT) THEN N'False' ELSE N'' END"
                            : storedValue);
                        EmitPart(index + 1);
                    });
                    break;
            }
        }
    }

    private void EmitVmBinary(
        IrBinaryExpression binary,
        VariableScope scope,
        VmMethod? context,
        Action<string> continuation)
    {
        if (binary.Operator is IrBinaryOperator.LogicalAnd or IrBinaryOperator.LogicalOr)
        {
            EmitVmShortCircuit(binary, scope, context, continuation);
            return;
        }

        EmitVmExpression(binary.Left, scope, context, left =>
        {
            var storage = AllocateVmTemporary(binary.Left.Type, context);
            StoreVmTemporary(storage, left);
            EmitVmExpression(binary.Right, scope, context, right =>
                continuation(CombineVmBinary(binary, ReadVmTemporary(storage), right)));
        });
    }

    private void EmitVmShortCircuit(
        IrBinaryExpression binary,
        VariableScope scope,
        VmMethod? context,
        Action<string> continuation)
    {
        var storage = AllocateVmTemporary(IrType.Bool, context);
        var shortCircuitLabel = _names.AllocateLabel("vm_short_circuit");
        var endLabel = _names.AllocateLabel("vm_short_circuit_end");
        var isAnd = binary.Operator == IrBinaryOperator.LogicalAnd;

        EmitVmExpression(binary.Left, scope, context, left =>
        {
            _sql.Line(isAnd
                ? $"IF NOT ({left} = 1) GOTO {shortCircuitLabel};"
                : $"IF {left} = 1 GOTO {shortCircuitLabel};");
            EmitVmExpression(binary.Right, scope, context, right =>
            {
                StoreVmTemporary(storage, right);
                _sql.Line($"GOTO {endLabel};");
            });
            EmitLabel(shortCircuitLabel);
            StoreVmTemporary(storage, isAnd ? "CAST(0 AS BIT)" : "CAST(1 AS BIT)");
            EmitLabel(endLabel);
            continuation(ReadVmTemporary(storage));
        });
    }

    private string CombineVmBinary(IrBinaryExpression binary, string left, string right)
    {
        if (binary.Operator == IrBinaryOperator.Coalesce)
            return $"COALESCE({left}, {right})";
        if (binary.Operator == IrBinaryOperator.Add && (binary.Left.Type.IsString || binary.Right.Type.IsString))
            return $"CONCAT({left}, {right})";
        if (binary.Operator == IrBinaryOperator.LogicalAnd)
            return $"CASE WHEN {left} = 1 AND {right} = 1 THEN CAST(1 AS BIT) ELSE CAST(0 AS BIT) END";
        if (binary.Operator == IrBinaryOperator.LogicalOr)
            return $"CASE WHEN {left} = 1 OR {right} = 1 THEN CAST(1 AS BIT) ELSE CAST(0 AS BIT) END";
        if (binary.Operator is IrBinaryOperator.Equal or IrBinaryOperator.NotEqual &&
            (IsNull(binary.Left) || IsNull(binary.Right)))
        {
            var operand = IsNull(binary.Left) ? right : left;
            return $"CASE WHEN {operand} {(binary.Operator == IrBinaryOperator.Equal ? "IS NULL" : "IS NOT NULL")} " +
                "THEN CAST(1 AS BIT) ELSE CAST(0 AS BIT) END";
        }
        var op = SqlOperator(binary.Operator);
        if (IsComparison(binary.Operator))
            return $"CASE WHEN {left} {op} {right} THEN CAST(1 AS BIT) ELSE CAST(0 AS BIT) END";
        return $"({left}) {op} ({right})";
    }

    private void EmitVmConditional(
        IrConditionalExpression conditional,
        VariableScope scope,
        VmMethod? context,
        Action<string> continuation)
    {
        var storage = AllocateVmTemporary(conditional.Type, context);
        var falseLabel = _names.AllocateLabel("vm_conditional_false");
        var endLabel = _names.AllocateLabel("vm_conditional_end");
        EmitVmExpression(conditional.Condition, scope, context, condition =>
        {
            _sql.Line($"IF NOT ({condition} = 1) GOTO {falseLabel};");
            EmitVmExpression(conditional.WhenTrue, scope, context, value =>
            {
                StoreVmTemporary(storage, value);
                _sql.Line($"GOTO {endLabel};");
            });
            EmitLabel(falseLabel);
            EmitVmExpression(conditional.WhenFalse, scope, context, value => StoreVmTemporary(storage, value));
            EmitLabel(endLabel);
            continuation(ReadVmTemporary(storage));
        });
    }

    private void EmitVmInvocation(
        IrInvocationExpression invocation,
        VmMethod callee,
        VariableScope scope,
        VmMethod? context,
        Action<string> continuation)
    {
        var arguments = new List<IrExpression>();
        if (callee.Definition.IsInstance && invocation.Target is IrMemberExpression member)
            arguments.Add(member.Receiver);
        arguments.AddRange(invocation.Arguments);
        if (arguments.Count != callee.Definition.Parameters.Count)
        {
            AddDiagnostic("SS3001", $"Method '{callee.Definition.Name}' expects {callee.Definition.Parameters.Count} arguments.", invocation.Source);
            continuation("NULL");
            return;
        }

        var capturedArguments = new List<VmTemporary>();
        EvaluateArgument(0);
        void EvaluateArgument(int index)
        {
            if (index == arguments.Count)
            {
                EmitCall();
                return;
            }
            var parameterType = callee.Definition.Parameters[index].Type;
            EmitVmExpression(arguments[index], scope, context, value =>
            {
                var storage = AllocateVmTemporary(parameterType, context);
                StoreVmTemporary(storage, value);
                capturedArguments.Add(storage);
                EvaluateArgument(index + 1);
            });
        }
        void EmitCall()
        {
            if (context is not null)
                SaveVmRegisters(context);
            var returnLabel = _names.AllocateLabel($"vm_return_{callee.Definition.Name}");
            var returnId = ++_nextVmContinuationId;
            _vmContinuations.Add(new VmContinuation(returnId, returnLabel));
            _sql.Line($"INSERT INTO {VmStack} (__function_id, __return_id) VALUES ({callee.Id}, {returnId});");
            _sql.Line($"SET {VmNewFrameId} = CONVERT(BIGINT, SCOPE_IDENTITY());");
            for (var index = 0; index < capturedArguments.Count; index++)
            {
                var parameter = callee.Variables[callee.Definition.Parameters[index].Name];
                InsertVmSlot(VmNewFrameId, parameter.Slot, parameter.Type, ReadVmTemporary(capturedArguments[index]));
            }
            _sql.Line($"GOTO {callee.EntryLabel};");
            EmitLabel(returnLabel);
            if (context is not null)
            {
                _sql.Line($"SET {VmFrameId} = (SELECT MAX(__id) FROM {VmStack});");
                LoadVmRegisters(context);
            }
            continuation(ReadVmResult(callee.Definition.ReturnType));
        }
    }

    private void EmitVmBinary(
        BinaryExpressionSyntax binary,
        VariableScope scope,
        VmMethod? context,
        Action<string> continuation)
    {
        if (binary.Kind() is SyntaxKind.LogicalAndExpression or SyntaxKind.LogicalOrExpression)
        {
            EmitVmShortCircuit(binary, scope, context, continuation);
            return;
        }

        EmitVmExpression(binary.Left, scope, context, left =>
        {
            var type = InferType(binary.Left, scope);
            var storage = AllocateVmTemporary(type, context);
            StoreVmTemporary(storage, left);
            EmitVmExpression(binary.Right, scope, context, right =>
            {
                var leftValue = ReadVmTemporary(storage);
                continuation(CombineVmBinary(binary, leftValue, right, scope));
            });
        });
    }

    private void EmitVmShortCircuit(
        BinaryExpressionSyntax binary,
        VariableScope scope,
        VmMethod? context,
        Action<string> continuation)
    {
        var storage = AllocateVmTemporary(IrType.Bool, context);
        var shortCircuitLabel = _names.AllocateLabel("vm_short_circuit");
        var endLabel = _names.AllocateLabel("vm_short_circuit_end");
        var isAnd = binary.IsKind(SyntaxKind.LogicalAndExpression);

        EmitVmExpression(binary.Left, scope, context, left =>
        {
            _sql.Line(isAnd
                ? $"IF NOT ({left} = 1) GOTO {shortCircuitLabel};"
                : $"IF {left} = 1 GOTO {shortCircuitLabel};");
            EmitVmExpression(binary.Right, scope, context, right =>
            {
                StoreVmTemporary(storage, right);
                _sql.Line($"GOTO {endLabel};");
            });
            EmitLabel(shortCircuitLabel);
            StoreVmTemporary(storage, isAnd ? "CAST(0 AS BIT)" : "CAST(1 AS BIT)");
            EmitLabel(endLabel);
            continuation(ReadVmTemporary(storage));
        });
    }

    private string CombineVmBinary(
        BinaryExpressionSyntax binary,
        string left,
        string right,
        VariableScope scope)
    {
        if (binary.IsKind(SyntaxKind.CoalesceExpression))
            return $"COALESCE({left}, {right})";
        if (binary.IsKind(SyntaxKind.AddExpression) &&
            (InferType(binary.Left, scope).IsString || InferType(binary.Right, scope).IsString))
            return $"CONCAT({left}, {right})";
        if (binary.IsKind(SyntaxKind.LogicalAndExpression))
            return $"CASE WHEN {left} = 1 AND {right} = 1 THEN CAST(1 AS BIT) ELSE CAST(0 AS BIT) END";
        if (binary.IsKind(SyntaxKind.LogicalOrExpression))
            return $"CASE WHEN {left} = 1 OR {right} = 1 THEN CAST(1 AS BIT) ELSE CAST(0 AS BIT) END";

        var op = SqlOperator(binary.Kind());
        if (op.Length == 0)
            return UnsupportedExpression(binary);
        if (binary.Kind() is SyntaxKind.EqualsExpression or SyntaxKind.NotEqualsExpression &&
            (binary.Left.IsKind(SyntaxKind.NullLiteralExpression) || binary.Right.IsKind(SyntaxKind.NullLiteralExpression)))
        {
            var operand = binary.Left.IsKind(SyntaxKind.NullLiteralExpression) ? right : left;
            var nullOperator = binary.IsKind(SyntaxKind.EqualsExpression) ? "IS NULL" : "IS NOT NULL";
            return $"CASE WHEN {operand} {nullOperator} THEN CAST(1 AS BIT) ELSE CAST(0 AS BIT) END";
        }
        if (IsComparison(binary.Kind()))
            return $"CASE WHEN {left} {op} {right} THEN CAST(1 AS BIT) ELSE CAST(0 AS BIT) END";
        return $"({left}) {op} ({right})";
    }

    private void EmitVmConditional(
        ConditionalExpressionSyntax conditional,
        VariableScope scope,
        VmMethod? context,
        Action<string> continuation)
    {
        var type = InferType(conditional, scope);
        var storage = AllocateVmTemporary(type, context);
        var falseLabel = _names.AllocateLabel("vm_conditional_false");
        var endLabel = _names.AllocateLabel("vm_conditional_end");

        EmitVmExpression(conditional.Condition, scope, context, condition =>
        {
            _sql.Line($"IF NOT ({condition} = 1) GOTO {falseLabel};");
            EmitVmExpression(conditional.WhenTrue, scope, context, value =>
            {
                StoreVmTemporary(storage, value);
                _sql.Line($"GOTO {endLabel};");
            });
            EmitLabel(falseLabel);
            EmitVmExpression(conditional.WhenFalse, scope, context, value => StoreVmTemporary(storage, value));
            EmitLabel(endLabel);
            continuation(ReadVmTemporary(storage));
        });
    }

    private void EmitVmInvocation(
        InvocationExpressionSyntax invocation,
        VmMethod callee,
        VariableScope scope,
        VmMethod? context,
        Action<string> continuation)
    {
        var arguments = InvocationArgumentExpressions(invocation, callee.Definition);
        if (arguments.Count != callee.Definition.Parameters.Count)
        {
            AddDiagnostic("SS3001", $"Method '{callee.Definition.Name}' expects {callee.Definition.Parameters.Count} arguments.", invocation);
            continuation("NULL");
            return;
        }

        var capturedArguments = new List<VmTemporary>();
        EvaluateArgument(0);

        void EvaluateArgument(int index)
        {
            if (index == arguments.Count)
            {
                EmitCall();
                return;
            }

            var parameterType = callee.Definition.Parameters[index].Type;
            EmitVmExpression(arguments[index], scope, context, value =>
            {
                var storage = AllocateVmTemporary(parameterType, context);
                StoreVmTemporary(storage, value);
                capturedArguments.Add(storage);
                EvaluateArgument(index + 1);
            });
        }

        void EmitCall()
        {
            if (context is not null)
                SaveVmRegisters(context);

            var returnLabel = _names.AllocateLabel($"vm_return_{callee.Definition.Name}");
            var returnId = ++_nextVmContinuationId;
            _vmContinuations.Add(new VmContinuation(returnId, returnLabel));

            _sql.Line($"INSERT INTO {VmStack} (__function_id, __return_id) VALUES ({callee.Id}, {returnId});");
            _sql.Line($"SET {VmNewFrameId} = CONVERT(BIGINT, SCOPE_IDENTITY());");
            for (var index = 0; index < capturedArguments.Count; index++)
            {
                var parameter = callee.Variables[callee.Definition.Parameters[index].Name];
                InsertVmSlot(VmNewFrameId, parameter.Slot, parameter.Type, ReadVmTemporary(capturedArguments[index]));
            }
            _sql.Line($"GOTO {callee.EntryLabel};");
            EmitLabel(returnLabel);

            if (context is not null)
            {
                _sql.Line($"SET {VmFrameId} = (SELECT MAX(__id) FROM {VmStack});");
                LoadVmRegisters(context);
            }

            continuation(ReadVmResult(callee.Definition.ReturnType));
        }
    }

    private VmTemporary AllocateVmTemporary(IrType type, VmMethod? context)
    {
        if (context is not null)
            return new VmTemporary(type, context.NextTemporarySlot++, null, context);

        var sqlName = _names.Allocate("_vm_temp");
        _sql.Line($"DECLARE {sqlName} {type.SqlType()};");
        return new VmTemporary(type, null, sqlName, null);
    }

    private void StoreVmTemporary(VmTemporary temporary, string value)
    {
        if (temporary.SqlName is not null)
            _sql.Line($"SET {temporary.SqlName} = {value};");
        else
            StoreVmSlot(VmFrameId, temporary.Slot!.Value, temporary.Type, value);
    }

    private string ReadVmTemporary(VmTemporary temporary) => temporary.SqlName ??
        ReadVmSlot(VmFrameId, temporary.Slot!.Value, temporary.Type);

    private void EmitVmMethod(VmMethod method)
    {
        EmitLeadingComments(method.Definition.Source);
        _sql.Line($"-- stack-machine body: {method.Definition.Name}");
        EmitLabel(method.EntryLabel);
        _sql.Line($"SET {VmFrameId} = (SELECT MAX(__id) FROM {VmStack});");
        LoadVmRegisters(method);

        if (method.Definition.Body is not null)
            EmitVmProceduralStatementSequence(method.Definition.Body.Statements, method, null);
        else if (method.Definition.ExpressionBody is not null)
            EmitVmExpression(
                method.Definition.ExpressionBody,
                method.Scope,
                method,
                value => EmitVmReturn(method, value));

        EmitVmReturn(method, "NULL");
        _sql.Line();
    }

    private void EmitVmStatementSequence(
        IEnumerable<StatementSyntax> statements,
        VmMethod method,
        LoopContext? loop)
    {
        foreach (var statement in statements)
            EmitVmStatement(BindProceduralStatement(statement, method.Scope), method, loop);
    }

    private void EmitVmStatement(ProceduralStatement statement, VmMethod method, LoopContext? loop)
    {
        EmitLeadingComments(statement.Source);
        switch (statement)
        {
            case ProceduralBlock block:
                EmitVmProceduralStatementSequence(block.Statements, method, loop);
                break;
            case ProceduralDeclarationStatement declaration:
                EmitVmDeclaration(declaration.Declaration, method);
                break;
            case ProceduralExpressionStatement expression:
                EmitVmExpressionStatement(expression.Expression, method);
                break;
            case ProceduralIf @if:
                EmitVmExpression(@if.Condition, method.Scope, method, condition =>
                {
                    _sql.Line($"IF {VmPredicate(condition, @if.Condition)}");
                    EmitVmEmbedded(@if.Then, method, loop);
                    if (@if.Else is { } elseStatement)
                    {
                        _sql.Line("ELSE");
                        EmitVmEmbedded(elseStatement, method, loop);
                    }
                });
                break;
            case ProceduralWhile @while:
                EmitVmWhile(@while, method);
                break;
            case ProceduralDo @do:
                EmitVmDo(@do, method);
                break;
            case ProceduralFor @for:
                EmitVmFor(@for, method);
                break;
            case ProceduralForEach forEach:
                EmitVmForEach(forEach, method);
                break;
            case ProceduralBreak:
                if (loop is null)
                    AddDiagnostic("SS2005", "break must be inside a loop.", statement.Source);
                else
                    _sql.Line($"GOTO {loop.BreakLabel};");
                break;
            case ProceduralContinue:
                if (loop is null)
                    AddDiagnostic("SS2001", "continue must be inside a loop.", statement.Source);
                else
                    _sql.Line($"GOTO {loop.ContinueLabel};");
                break;
            case ProceduralReturn @return:
                if (@return.Expression is null)
                    EmitVmReturn(method, "NULL");
                else
                    EmitVmExpression(
                        @return.Expression,
                        method.Scope,
                        method,
                        value => EmitVmReturn(method, value));
                break;
            case ProceduralLocalFunction:
            case ProceduralEmpty:
                break;
            case ProceduralUnsupported unsupported:
                Unsupported(unsupported.Source, "stack-machine statement");
                break;
        }
        EmitTrailingComments(statement.Source);
    }

    private void EmitVmProceduralStatementSequence(
        IEnumerable<ProceduralStatement> statements,
        VmMethod method,
        LoopContext? loop)
    {
        foreach (var statement in statements)
            EmitVmStatement(statement, method, loop);
    }

    private void EmitVmExpressionStatement(ExpressionSyntax expression, VmMethod method)
    {
        if (TryEmitHeapStatement(expression, method.Scope, method))
            return;

        if (expression is AssignmentExpressionSyntax assignment &&
            assignment.Left is IdentifierNameSyntax identifier &&
            method.Variables.TryGetValue(identifier.Identifier.ValueText, out var target))
        {
            EmitVmExpression(
                assignment.Right,
                method.Scope,
                method,
                value => _sql.Line(VmAssignmentLine(assignment, target.SqlName, target.Type, value)));
            return;
        }

        if (expression is InvocationExpressionSyntax invocation && IsConsoleWrite(invocation))
        {
            var argument = invocation.ArgumentList.Arguments.FirstOrDefault()?.Expression;
            if (argument is null)
                EmitPrintSql("N''");
            else
                EmitVmExpression(argument, method.Scope, method, EmitPrintSql);
            return;
        }

        if (ContainsVmCall(expression))
        {
            EmitVmExpression(expression, method.Scope, method, _ => { });
            return;
        }

        foreach (var line in MutationLines(expression, method.Scope))
            _sql.Line(line);
    }

    private void EmitVmExpressionStatement(IrExpression expression, VmMethod method)
    {
        if (HasCSharpSource(expression.Source) && TryEmitHeapStatement(CSharpExpression(expression), method.Scope, method))
            return;
        if (expression is IrAssignmentExpression assignment &&
            assignment.Target is IrVariableExpression identifier &&
            method.Variables.TryGetValue(identifier.Symbol.Name, out var target))
        {
            EmitVmExpression(
                assignment.Value,
                method.Scope,
                method,
                value => _sql.Line(IrAssignmentLine(
                    assignment,
                    target.SqlName,
                    target.Type,
                    value,
                    parenthesizeValue: assignment.Operator != IrAssignmentOperator.Assign)));
            return;
        }
        if (expression is IrInvocationExpression invocation && IsConsoleWrite(invocation))
        {
            if (invocation.Arguments.Count == 0)
                EmitPrintSql("N''");
            else
                EmitVmExpression(invocation.Arguments[0], method.Scope, method, EmitPrintSql);
            return;
        }
        if (expression is IrUnaryExpression
            {
                Operator: IrUnaryOperator.PreIncrement or IrUnaryOperator.PostIncrement or
                    IrUnaryOperator.PreDecrement or IrUnaryOperator.PostDecrement,
                Operand: IrVariableExpression variable
            } && method.Variables.TryGetValue(variable.Symbol.Name, out var mutationTarget))
        {
            var op = expression is IrUnaryExpression
                {
                    Operator: IrUnaryOperator.PreIncrement or IrUnaryOperator.PostIncrement
                } ? "+" : "-";
            _sql.Line($"SET {mutationTarget.SqlName} = {mutationTarget.SqlName} {op} 1;");
            return;
        }
        EmitVmExpression(expression, method.Scope, method, _ => { });
    }

    private string VmAssignmentLine(
        AssignmentExpressionSyntax assignment,
        string target,
        IrType targetType,
        string value)
    {
        if (assignment.IsKind(SyntaxKind.SimpleAssignmentExpression))
            return $"SET {target} = {value};";

        var op = assignment.Kind() switch
        {
            SyntaxKind.AddAssignmentExpression => "+",
            SyntaxKind.SubtractAssignmentExpression => "-",
            SyntaxKind.MultiplyAssignmentExpression => "*",
            SyntaxKind.DivideAssignmentExpression => "/",
            SyntaxKind.ModuloAssignmentExpression => "%",
            SyntaxKind.AndAssignmentExpression => "&",
            SyntaxKind.OrAssignmentExpression => "|",
            SyntaxKind.ExclusiveOrAssignmentExpression => "^",
            _ => null
        };
        if (op is null)
        {
            Unsupported(assignment, "assignment operator");
            return $"SET {target} = {value};";
        }
        if (targetType.IsString && op == "+")
            return $"SET {target} = CONCAT({target}, {value});";
        return $"SET {target} = {target} {op} ({value});";
    }

    private void EmitVmWhile(ProceduralWhile statement, VmMethod method)
    {
        var conditionLabel = _names.AllocateLabel("vm_while_condition");
        var continueLabel = _names.AllocateLabel("vm_while_continue");
        var breakLabel = _names.AllocateLabel("vm_while_break");
        EmitLabel(conditionLabel);
        EmitVmExpression(statement.Condition, method.Scope, method, condition =>
        {
            _sql.Line($"IF NOT ({VmPredicate(condition, statement.Condition)}) GOTO {breakLabel};");
            EmitVmEmbeddedContents(statement.Body, method, new LoopContext(breakLabel, continueLabel));
            EmitLabel(continueLabel);
            _sql.Line($"GOTO {conditionLabel};");
            EmitLabel(breakLabel);
        });
    }

    private void EmitVmDo(ProceduralDo statement, VmMethod method)
    {
        var bodyLabel = _names.AllocateLabel("vm_do_body");
        var continueLabel = _names.AllocateLabel("vm_do_continue");
        var breakLabel = _names.AllocateLabel("vm_do_break");
        EmitLabel(bodyLabel);
        EmitVmEmbeddedContents(statement.Body, method, new LoopContext(breakLabel, continueLabel));
        EmitLabel(continueLabel);
        EmitVmExpression(statement.Condition, method.Scope, method, condition =>
        {
            _sql.Line($"IF {VmPredicate(condition, statement.Condition)} GOTO {bodyLabel};");
            EmitLabel(breakLabel);
        });
    }

    private void EmitVmFor(ProceduralFor statement, VmMethod method)
    {
        if (statement.Declaration is not null)
            EmitVmDeclaration(statement.Declaration, method);
        foreach (var initializer in statement.Initializers)
            EmitVmExpressionStatement(initializer, method);

        var conditionLabel = _names.AllocateLabel("vm_for_condition");
        var continueLabel = _names.AllocateLabel("vm_for_continue");
        var breakLabel = _names.AllocateLabel("vm_for_break");
        EmitLabel(conditionLabel);
        if (statement.Condition is null)
            EmitBody();
        else
            EmitVmExpression(statement.Condition, method.Scope, method, condition =>
            {
                _sql.Line($"IF NOT ({VmPredicate(condition, statement.Condition)}) GOTO {breakLabel};");
                EmitBody();
            });

        void EmitBody()
        {
            EmitVmEmbeddedContents(statement.Body, method, new LoopContext(breakLabel, continueLabel));
            EmitLabel(continueLabel);
            foreach (var incrementor in statement.Incrementors)
                EmitVmExpressionStatement(incrementor, method);
            _sql.Line($"GOTO {conditionLabel};");
            EmitLabel(breakLabel);
        }
    }

    private void EmitVmDeclaration(ProceduralDeclaration declaration, VmMethod method)
    {
        foreach (var variable in declaration.Variables)
        {
            var target = method.Variables[variable.Name];
            if (variable.Initializer is null)
                _sql.Line($"SET {target.SqlName} = NULL;");
            else
                EmitVmExpression(
                    variable.Initializer,
                    method.Scope,
                    method,
                    value => _sql.Line($"SET {target.SqlName} = {value};"));
        }
    }

    private void EmitVmEmbedded(ProceduralStatement statement, VmMethod method, LoopContext? loop)
    {
        _sql.Line("BEGIN");
        using (_sql.Indent())
            EmitVmEmbeddedContents(statement, method, loop);
        _sql.Line("END;");
    }

    private void EmitVmForEach(ProceduralForEach statement, VmMethod method)
    {
        var collectionType = statement.SourceExpression.Facts.Type;
        if (!IsSequenceType(collectionType.Name))
        {
            AddDiagnostic("SS6302", "foreach currently supports arrays and List<T>.", statement.SourceExpression.Source);
            return;
        }

        EmitVmExpression(statement.SourceExpression, method.Scope, method, collection =>
        {
            var collectionStorage = AllocateVmTemporary(collectionType, method);
            StoreVmTemporary(collectionStorage, collection);
            var indexStorage = AllocateVmTemporary(IrType.Int, method);
            StoreVmTemporary(indexStorage, "0");
            var item = method.Variables[statement.Element.Name];
            var conditionLabel = _names.AllocateLabel("vm_foreach_condition");
            var continueLabel = _names.AllocateLabel("vm_foreach_continue");
            var breakLabel = _names.AllocateLabel("vm_foreach_break");

            EmitLabel(conditionLabel);
            var collectionValue = ReadVmTemporary(collectionStorage);
            var indexValue = ReadVmTemporary(indexStorage);
            _sql.Line($"IF {indexValue} >= {SequenceCountSql(collectionValue)} GOTO {breakLabel};");
            _sql.Line($"SET {item.SqlName} = {SequenceElementSql(collectionValue, indexValue, item.Type)};");
            EmitVmEmbeddedContents(statement.Body, method, new LoopContext(breakLabel, continueLabel));
            EmitLabel(continueLabel);
            _sql.Line($"UPDATE {VmSlots} SET __value = CONVERT(SQL_VARIANT, CONVERT(INT, __value) + 1) WHERE __frame_id = {VmFrameId} AND __slot_id = {indexStorage.Slot};");
            _sql.Line($"GOTO {conditionLabel};");
            EmitLabel(breakLabel);
        });
    }

    private void EmitVmEmbeddedContents(ProceduralStatement statement, VmMethod method, LoopContext? loop)
    {
        if (statement is ProceduralBlock block)
            EmitVmProceduralStatementSequence(block.Statements, method, loop);
        else
            EmitVmStatement(statement, method, loop);
    }

    private string VmPredicate(string value, ExpressionSyntax original, VariableScope scope) =>
        InferType(original, scope).IsBoolean && IsPredicateShape(original)
            ? $"{value} = 1"
            : InferType(original, scope).IsBoolean
                ? $"{value} = 1"
                : value;

    private static string VmPredicate(string value, IrExpression original) =>
        original.Type.IsBoolean ? $"{value} = 1" : value;

    private void EmitVmReturn(VmMethod method, string value)
    {
        if (method.ReturnSqlName is not null)
            _sql.Line($"SET {method.ReturnSqlName} = {value};");
        _sql.Line($"SET {VmResult} = NULL;");
        _sql.Line($"SET {VmTextResult} = NULL;");
        _sql.Line($"SET {VmBinaryResult} = NULL;");
        if (method.Definition.ReturnType.IsString)
            _sql.Line($"SET {VmTextResult} = {method.ReturnSqlName};");
        else if (method.Definition.ReturnType.Name == "byte[]")
            _sql.Line($"SET {VmBinaryResult} = {method.ReturnSqlName};");
        else if (method.Definition.ReturnType.Name != "void")
            _sql.Line($"SET {VmResult} = CONVERT(SQL_VARIANT, {method.ReturnSqlName});");

        _sql.Line($"SET {VmJump} = (SELECT __return_id FROM {VmStack} WHERE __id = {VmFrameId});");
        _sql.Line($"DELETE FROM {VmSlots} WHERE __frame_id = {VmFrameId};");
        _sql.Line($"DELETE FROM {VmStack} WHERE __id = {VmFrameId};");
        _sql.Line($"GOTO {VmDispatchLabel};");
    }

    private string ReadVmResult(IrType type)
    {
        if (type.IsString)
            return VmTextResult;
        if (type.Name == "byte[]")
            return VmBinaryResult;
        if (type.Name == "void")
            return "NULL";
        return $"CONVERT({type.SqlType()}, {VmResult})";
    }

    private void SaveVmRegisters(VmMethod method)
    {
        foreach (var variable in method.Variables.Values)
            StoreVmSlot(VmFrameId, variable.Slot, variable.Type, variable.SqlName);
    }

    private void LoadVmRegisters(VmMethod method)
    {
        foreach (var variable in method.Variables.Values)
            _sql.Line($"SET {variable.SqlName} = {ReadVmSlot(VmFrameId, variable.Slot, variable.Type)};");
    }

    private void StoreVmSlot(string frameId, int slot, IrType type, string value)
    {
        _sql.Line($"DELETE FROM {VmSlots} WHERE __frame_id = {frameId} AND __slot_id = {slot};");
        InsertVmSlot(frameId, slot, type, value);
    }

    private void InsertVmSlot(string frameId, int slot, IrType type, string value)
    {
        if (type.IsString)
            _sql.Line($"INSERT INTO {VmSlots} (__frame_id, __slot_id, __text_value) VALUES ({frameId}, {slot}, {value});");
        else if (type.Name == "byte[]")
            _sql.Line($"INSERT INTO {VmSlots} (__frame_id, __slot_id, __binary_value) VALUES ({frameId}, {slot}, {value});");
        else
            _sql.Line($"INSERT INTO {VmSlots} (__frame_id, __slot_id, __value) VALUES ({frameId}, {slot}, CONVERT(SQL_VARIANT, {value}));");
    }

    private static string ReadVmSlot(string frameId, int slot, IrType type)
    {
        if (type.IsString)
            return $"(SELECT __text_value FROM {VmSlots} WHERE __frame_id = {frameId} AND __slot_id = {slot})";
        if (type.Name == "byte[]")
            return $"(SELECT __binary_value FROM {VmSlots} WHERE __frame_id = {frameId} AND __slot_id = {slot})";
        return $"CONVERT({type.SqlType()}, (SELECT __value FROM {VmSlots} WHERE __frame_id = {frameId} AND __slot_id = {slot}))";
    }

    private sealed class VmMethod(
        MethodDefinition definition,
        int id,
        string entryLabel,
        string? returnSqlName)
    {
        public MethodDefinition Definition { get; } = definition;
        public int Id { get; } = id;
        public string EntryLabel { get; } = entryLabel;
        public string? ReturnSqlName { get; } = returnSqlName;
        public Dictionary<string, VmVariable> Variables { get; } = new(StringComparer.Ordinal);
        public VariableScope Scope { get; } = new();
        public int NextTemporarySlot { get; set; }
    }

    private sealed record VmVariable(string Name, IrType Type, int Slot, string SqlName);
    private sealed record VmContinuation(int Id, string Label);
    private sealed record VmTemporary(IrType Type, int? Slot, string? SqlName, VmMethod? Context);
}
