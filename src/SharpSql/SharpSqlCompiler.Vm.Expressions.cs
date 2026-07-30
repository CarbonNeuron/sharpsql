using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SharpSql;

public sealed partial class SharpSqlCompiler
{
    private bool ContainsVmCall(ExpressionSyntax expression) =>
        expression.DescendantNodesAndSelf()
            .OfType<InvocationExpressionSyntax>()
            .Any(call => TryGetMethod(call, out var method) && _vmMethods.ContainsKey(method.Id));

    private void EmitVmExpression(
        ExpressionSyntax expression,
        VariableScope scope,
        VmMethod? context,
        Action<string> continuation) =>
        EmitVmExpression(BindIrExpression(expression, scope), scope, context, continuation);

    private void EmitVmExpression(
        IrExpression expression,
        VariableScope scope,
        VmMethod? context,
        Action<string> continuation)
    {
        if (TryEmitGuardedLinqExpression(expression, scope, context, continuation))
            return;
        if (TryEmitLinqMaterialization(expression, scope, context, continuation))
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
            case IrAwaitExpression awaitExpression:
                continuation(UnsupportedAwait(awaitExpression));
                return;
            case IrInvocationExpression invocation when TryGetRuntimeDispatch(invocation, out var dispatchSlot):
                EmitVmDispatchInvocation(invocation, dispatchSlot, scope, context, continuation);
                return;
            case IrInvocationExpression invocation when TryGetVmMethod(invocation, out var callee):
                EmitVmInvocation(invocation, callee, scope, context, continuation);
                return;
            case IrInvocationExpression invocation when
                TryGetMethod(invocation, out var inlineMethod) &&
                inlineMethod.PureExpression is not null:
                EmitVmInlineInvocation(invocation, inlineMethod, scope, context, continuation);
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
            case IrMemberExpression member:
                EmitVmMember(member, scope, context, continuation);
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

    private void EmitVmMember(
        IrMemberExpression member,
        VariableScope scope,
        VmMethod? context,
        Action<string> continuation)
    {
        var receiverType = member.Receiver.Type;
        EmitVmExpression(member.Receiver, scope, context, receiver =>
        {
            if (IsGroupingType(receiverType.Name) && member.MemberName == "Key")
            {
                continuation(receiver);
                return;
            }
            if (receiverType.IsString && member.MemberName == "Length")
            {
                continuation($"CONVERT(INT, DATALENGTH({receiver}) / 2)");
                return;
            }
            if ((IsListType(receiverType.Name) && member.MemberName == "Count") ||
                (IsArrayType(receiverType.Name) && member.MemberName == "Length") ||
                (IsDictionaryType(receiverType.Name) && member.MemberName == "Count"))
            {
                continuation($"(SELECT __count FROM {HeapObjects} WHERE {HeapExecutionFilter()}__id = {receiver})");
                return;
            }
            if (TryResolveHeapField(
                    receiverType,
                    member.MemberName,
                    member.MemberId,
                    out var heapType,
                    out var field))
            {
                continuation($"(SELECT {field.SqlName} FROM {heapType.TableName} WHERE {HeapExecutionFilter()}__object_id = {receiver})");
                return;
            }
            continuation(UnsupportedExpression(
                member.Source,
                $"Unknown member '{member.MemberName}' on '{receiverType.Name}'."));
        });
    }

    private void EmitVmInlineInvocation(
        IrInvocationExpression invocation,
        MethodDefinition method,
        VariableScope scope,
        VmMethod? context,
        Action<string> continuation)
    {
        var arguments = InvocationArgumentExpressions(invocation, method);
        if (!CanInline(method, arguments.Count))
        {
            continuation("NULL");
            return;
        }

        var captured = new List<VmTemporary>();
        EvaluateArgument(0);

        void EvaluateArgument(int index)
        {
            if (index == arguments.Count)
            {
                var replacements = new Dictionary<string, Substitution>(StringComparer.Ordinal);
                for (var argumentIndex = 0; argumentIndex < captured.Count; argumentIndex++)
                {
                    var parameter = method.Parameters[argumentIndex];
                    replacements[parameter.Name] = new Substitution(
                        SqlScalarExpression.Primary(ReadVmTemporary(captured[argumentIndex]), parameter.Type));
                }
                continuation(EmitScalar(method.PureExpression!, scope, replacements));
                return;
            }

            EmitVmExpression(arguments[index], scope, context, value =>
            {
                var storage = AllocateVmTemporary(method.Parameters[index].Type, context);
                StoreVmTemporary(storage, value);
                captured.Add(storage);
                EvaluateArgument(index + 1);
            });
        }
    }

    private bool TryGetVmMethod(IrInvocationExpression invocation, out VmMethod method)
    {
        if (TryGetMethod(invocation, out var definition) &&
            _vmMethods.TryGetValue(definition.Id, out method!))
            return true;
        method = null!;
        return false;
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
                        parts.Add(FormatTextValue(interpolation.Expression.Type, storedValue));
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
            var executionColumns = UsesDurableVmStorage ? "__execution_id, " : string.Empty;
            var executionValues = UsesDurableVmStorage ? $"{RuntimeExecutionId}, " : string.Empty;
            _sql.Line($"INSERT INTO {VmStackTable} ({executionColumns}__function_id, __return_id, __caller_id) VALUES ({executionValues}{callee.Id}, {returnId}, {(context is null ? "NULL" : VmFrameId)});");
            _sql.Line($"SET {VmNewFrameId} = CONVERT(INT, SCOPE_IDENTITY());");
            for (var index = 0; index < capturedArguments.Count; index++)
            {
                var parameter = callee.Variables[callee.Definition.Parameters[index].Name];
                InsertVmSlot(VmNewFrameId, parameter.Slot, parameter.Type, ReadVmTemporary(capturedArguments[index]));
            }
            _sql.Line($"SET {VmFrameId} = {VmNewFrameId};");
            _sql.Line($"GOTO {callee.EntryLabel};");
            EmitLabel(returnLabel);
            if (context is not null)
            {
                _sql.Line($"SET {VmFrameId} = {VmCallerFrameId};");
                LoadVmRegisters(context);
            }
            continuation(ReadVmResult(callee.Definition.ReturnType));
        }
    }

    private void EmitVmDispatchInvocation(
        IrInvocationExpression invocation,
        RuntimeDispatchSlot dispatchSlot,
        VariableScope scope,
        VmMethod? context,
        Action<string> continuation)
    {
        var arguments = InvocationArgumentExpressions(invocation, dispatchSlot.Method);
        if (arguments.Count != dispatchSlot.Method.Parameters.Count)
        {
            AddDiagnostic(
                "SS3001",
                $"Method '{dispatchSlot.Method.Name}' expects {dispatchSlot.Method.Parameters.Count} arguments.",
                invocation.Source);
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

            var parameterType = dispatchSlot.Method.Parameters[index].Type;
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
            var receiver = ReadVmTemporary(capturedArguments[0]);
            _sql.Line($"IF {receiver} IS NULL THROW 51011, 'Object reference was null.', 1;");
            var functionId = AllocateVmTemporary(IrType.Int, context);
            var cases = dispatchSlot.Targets.Select(target =>
                $"WHEN {target.RuntimeTypeId} THEN {_vmMethods[target.Method.Id].Id}");
            StoreVmTemporary(
                functionId,
                $"CASE (SELECT __type_id FROM {HeapObjects} WHERE {HeapExecutionFilter()}__id = {receiver}) {string.Join(" ", cases)} END");
            var selectedFunction = ReadVmTemporary(functionId);
            _sql.Line($"IF {selectedFunction} IS NULL THROW 51007, 'Virtual dispatch target was not found.', 1;");

            if (context is not null)
                SaveVmRegisters(context);
            var returnLabel = _names.AllocateLabel($"vm_return_{dispatchSlot.Method.Name}");
            var returnId = ++_nextVmContinuationId;
            _vmContinuations.Add(new VmContinuation(returnId, returnLabel));
            var executionColumns = UsesDurableVmStorage ? "__execution_id, " : string.Empty;
            var executionValues = UsesDurableVmStorage ? $"{RuntimeExecutionId}, " : string.Empty;
            _sql.Line($"INSERT INTO {VmStackTable} ({executionColumns}__function_id, __return_id, __caller_id) VALUES ({executionValues}{selectedFunction}, {returnId}, {(context is null ? "NULL" : VmFrameId)});");
            _sql.Line($"SET {VmNewFrameId} = CONVERT(INT, SCOPE_IDENTITY());");
            for (var index = 0; index < capturedArguments.Count; index++)
            {
                var parameterType = dispatchSlot.Method.Parameters[index].Type;
                InsertVmSlot(VmNewFrameId, index + 1, parameterType, ReadVmTemporary(capturedArguments[index]));
            }
            _sql.Line($"SET {VmFrameId} = {VmNewFrameId};");
            _sql.Line($"GOTO {VmFunctionDispatchLabel};");
            EmitLabel(returnLabel);
            if (context is not null)
            {
                _sql.Line($"SET {VmFrameId} = {VmCallerFrameId};");
                LoadVmRegisters(context);
            }
            continuation(ReadVmResult(dispatchSlot.Method.ReturnType));
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

}
