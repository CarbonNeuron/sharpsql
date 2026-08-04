namespace SharpSql;

internal sealed record CoreLoweringResult(CoreMethod? Method, string? UnsupportedReason)
{
    public bool Success => Method is not null;
}

internal static class CoreIrLowerer
{
    public static CoreLoweringResult Lower(
        MethodDefinition method,
        IReadOnlyCollection<IrMethodId>? callableMethods = null)
    {
        if (method.Body is null && method.ExpressionBody is null)
            return Unsupported("Only methods with a body can be lowered to Core IR.");

        var builder = new Builder(method, callableMethods);
        return builder.Lower();
    }

    private static CoreLoweringResult Unsupported(string reason) => new(null, reason);

    private sealed class Builder
    {
        private readonly MethodDefinition _method;
        private readonly IReadOnlyCollection<IrMethodId>? _callableMethods;
        private readonly List<MutableBlock> _blocks = [];
        private readonly Dictionary<IrSymbolId, CoreValueId> _symbols = [];
        private readonly List<CoreParameter> _parameters = [];
        private readonly List<CoreLocal> _locals = [];
        private int _nextValue;
        private MutableBlock _current;
        private string? _unsupportedReason;

        public Builder(MethodDefinition method, IReadOnlyCollection<IrMethodId>? callableMethods)
        {
            _method = method;
            _callableMethods = callableMethods;
            _current = CreateBlock();
            foreach (var parameter in method.Parameters)
            {
                var value = AllocateValue();
                _symbols[parameter.Symbol.Id] = value;
                _parameters.Add(new CoreParameter(value, parameter.Type));
            }
        }

        public CoreLoweringResult Lower()
        {
            if (_method.Body is not null)
            {
                if (!LowerStatement(_method.Body))
                    return Unsupported(_unsupportedReason ?? "The method contains an unsupported Core IR operation.");
            }
            else if (_method.ExpressionBody is not null)
            {
                if (!LowerExpression(_method.ExpressionBody, out var expressionResult))
                    return Unsupported(_unsupportedReason ?? "The method contains an unsupported Core IR operation.");
                _current.Terminator = _method.ReturnType == IrType.Void
                    ? new CoreReturn(null)
                    : new CoreReturn(expressionResult);
            }

            if (_current.Terminator is null)
            {
                if (_method.ReturnType != IrType.Void)
                    return Unsupported("A non-void Core IR method must terminate with a return value.");
                _current.Terminator = new CoreReturn(null);
            }

            var blocks = _blocks
                .Where(block => block.Terminator is not null)
                .Select(block => new CoreBlock(block.Id, block.Instructions, block.Terminator!))
                .ToArray();
            return new CoreLoweringResult(
                new CoreMethod(
                    _method.Id,
                    _method.ReturnType,
                    _parameters,
                    _locals,
                    new CoreBlockId(0),
                    blocks),
                null);
        }

        private bool LowerStatement(ProceduralStatement statement)
        {
            if (_current.Terminator is not null)
                return true;

            switch (statement)
            {
                case ProceduralBlock block:
                    foreach (var child in block.Statements)
                    {
                        if (!LowerStatement(child))
                            return false;
                        if (_current.Terminator is not null)
                            break;
                    }
                    return true;

                case ProceduralDeclarationStatement declaration:
                    foreach (var variable in declaration.Declaration.Variables)
                    {
                        var destination = AllocateValue();
                        _symbols[variable.Symbol.Id] = destination;
                        _locals.Add(new CoreLocal(destination, variable.DeclaredType));
                        if (variable.Initializer is not null)
                        {
                            if (!LowerExpression(variable.Initializer, out var initializer))
                                return false;
                            _current.Instructions.Add(
                                new CoreMoveInstruction(destination, variable.DeclaredType, initializer));
                        }
                    }
                    return true;

                case ProceduralExpressionStatement { Expression: IrAssignmentExpression assignment }:
                    return LowerAssignment(assignment, out _);

                case ProceduralExpressionStatement { Expression: IrUnaryExpression unary }
                    when unary.Operator is IrUnaryOperator.PreIncrement or IrUnaryOperator.PostIncrement or
                        IrUnaryOperator.PreDecrement or IrUnaryOperator.PostDecrement:
                    return LowerIncrement(unary, out _);

                case ProceduralExpressionStatement expression:
                    return LowerExpression(expression.Expression, out _);

                case ProceduralIf @if:
                    return LowerIf(@if);

                case ProceduralWhile @while:
                    return LowerWhile(@while);

                case ProceduralReturn @return:
                    if (@return.Expression is null)
                    {
                        _current.Terminator = new CoreReturn(null);
                        return true;
                    }
                    if (!LowerExpression(@return.Expression, out var returnValue))
                        return false;
                    _current.Terminator = new CoreReturn(returnValue);
                    return true;

                case ProceduralEmpty:
                    return true;

                default:
                    return Fail($"Statement '{statement.GetType().Name}' is not supported by Core IR lowering.");
            }
        }

        private bool LowerIf(ProceduralIf statement)
        {
            if (!LowerExpression(statement.Condition, out var condition))
                return false;

            var predecessor = _current;
            var thenBlock = CreateBlock();
            var elseBlock = CreateBlock();
            predecessor.Terminator = new CoreBranch(condition, thenBlock.Id, elseBlock.Id);

            _current = thenBlock;
            if (!LowerStatement(statement.Then))
                return false;
            var thenEnd = _current;
            var thenFallsThrough = thenEnd.Terminator is null;

            _current = elseBlock;
            if (statement.Else is not null && !LowerStatement(statement.Else))
                return false;
            var elseEnd = _current;
            var elseFallsThrough = elseEnd.Terminator is null;

            if (!thenFallsThrough && !elseFallsThrough)
            {
                _current = elseEnd;
                return true;
            }

            var continuation = CreateBlock();
            if (thenFallsThrough)
                thenEnd.Terminator = new CoreJump(continuation.Id);
            if (elseFallsThrough)
                elseEnd.Terminator = new CoreJump(continuation.Id);
            _current = continuation;
            return true;
        }

        private bool LowerWhile(ProceduralWhile statement)
        {
            var predecessor = _current;
            var conditionBlock = CreateBlock();
            var bodyBlock = CreateBlock();
            var continuation = CreateBlock();
            predecessor.Terminator = new CoreJump(conditionBlock.Id);

            _current = conditionBlock;
            if (!LowerExpression(statement.Condition, out var condition))
                return false;
            conditionBlock.Terminator = new CoreBranch(condition, bodyBlock.Id, continuation.Id);

            _current = bodyBlock;
            if (!LowerStatement(statement.Body))
                return false;
            if (_current.Terminator is null)
                _current.Terminator = new CoreJump(conditionBlock.Id);

            _current = continuation;
            return true;
        }

        private bool LowerExpression(IrExpression expression, out CoreValueId value)
        {
            switch (expression)
            {
                case IrConstantExpression constant:
                    value = AllocateValue();
                    _current.Instructions.Add(new CoreConstantInstruction(value, constant.Type, constant.Value));
                    return true;

                case IrDefaultValueExpression defaultValue:
                    value = AllocateValue();
                    _current.Instructions.Add(new CoreConstantInstruction(
                        value,
                        defaultValue.Type,
                        DefaultCoreValue(defaultValue.Type)));
                    return true;

                case IrVariableExpression variable when _symbols.TryGetValue(variable.Symbol.Id, out value):
                    return true;

                case IrConversionExpression conversion:
                    if (!LowerExpression(conversion.Operand, out var converted))
                        break;
                    value = AllocateValue();
                    _current.Instructions.Add(new CoreConvertInstruction(value, conversion.TargetType, converted));
                    return true;

                case IrBinaryExpression binary:
                    if (binary.Operator is IrBinaryOperator.LogicalAnd or IrBinaryOperator.LogicalOr or
                        IrBinaryOperator.Coalesce)
                    {
                        value = default;
                        return Fail($"Short-circuit operator '{binary.Operator}' requires control-flow lowering.");
                    }
                    var contextualStringType = binary.Operator is IrBinaryOperator.Equal or IrBinaryOperator.NotEqual &&
                        (binary.Left.Type.IsString || binary.Right.Type.IsString)
                            ? IrType.String
                            : null;
                    if (!LowerBinaryOperand(binary.Left, contextualStringType, out var left) ||
                        !LowerBinaryOperand(binary.Right, contextualStringType, out var right))
                        break;
                    value = AllocateValue();
                    _current.Instructions.Add(
                        new CoreBinaryInstruction(value, binary.Type, binary.Operator, left, right));
                    return true;

                case IrUnaryExpression unary when unary.Operator is
                    IrUnaryOperator.PreIncrement or IrUnaryOperator.PostIncrement or
                    IrUnaryOperator.PreDecrement or IrUnaryOperator.PostDecrement:
                    return LowerIncrement(unary, out value);

                case IrUnaryExpression unary:
                    if (!LowerExpression(unary.Operand, out var operand))
                        break;
                    value = AllocateValue();
                    _current.Instructions.Add(
                        new CoreUnaryInstruction(value, unary.Type, unary.Operator, operand));
                    return true;

                case IrAssignmentExpression assignment:
                    return LowerAssignment(assignment, out value);

                case IrConditionalExpression conditional:
                    return LowerConditional(conditional, out value);

                case IrInvocationExpression invocation when
                    !invocation.TargetMethodId.IsNone &&
                    _callableMethods?.Contains(invocation.TargetMethodId) == true:
                    value = default;
                    var arguments = new List<CoreValueId>(invocation.Arguments.Count);
                    foreach (var argument in invocation.Arguments)
                    {
                        if (!LowerExpression(argument, out var loweredArgument))
                            return false;
                        arguments.Add(loweredArgument);
                    }
                    if (invocation.Type != IrType.Void)
                        value = AllocateValue();
                    _current.Instructions.Add(new CoreCallInstruction(
                        value,
                        invocation.Type,
                        invocation.TargetMethodId,
                        arguments));
                    return true;

                case IrInvocationExpression invocation when IsConsoleWriteLine(invocation):
                    value = default;
                    var hostArguments = new List<CoreValueId>(invocation.Arguments.Count);
                    foreach (var argument in invocation.Arguments)
                    {
                        if (!LowerExpression(argument, out var loweredArgument))
                            return false;
                        hostArguments.Add(loweredArgument);
                    }
                    _current.Instructions.Add(new CoreHostCallInstruction(
                        CoreHostOperation.WriteLine,
                        hostArguments));
                    return true;
            }

            value = default;
            return Fail($"Expression '{expression.GetType().Name}' is not supported by Core IR lowering.");
        }

        private bool LowerBinaryOperand(IrExpression expression, IrType? contextualType, out CoreValueId value)
        {
            if (contextualType is not null && expression is IrConstantExpression { Value: null })
            {
                value = AllocateValue();
                _current.Instructions.Add(new CoreConstantInstruction(value, contextualType, null));
                return true;
            }
            return LowerExpression(expression, out value);
        }

        private bool LowerConditional(IrConditionalExpression expression, out CoreValueId value)
        {
            value = default;
            if (!LowerExpression(expression.Condition, out var condition))
                return false;

            value = AllocateValue();
            _locals.Add(new CoreLocal(value, expression.Type));
            var predecessor = _current;
            var whenTrue = CreateBlock();
            var whenFalse = CreateBlock();
            var continuation = CreateBlock();
            predecessor.Terminator = new CoreBranch(condition, whenTrue.Id, whenFalse.Id);

            _current = whenTrue;
            if (!LowerExpression(expression.WhenTrue, out var trueValue))
                return false;
            _current.Instructions.Add(new CoreMoveInstruction(value, expression.Type, trueValue));
            _current.Terminator = new CoreJump(continuation.Id);

            _current = whenFalse;
            if (!LowerExpression(expression.WhenFalse, out var falseValue))
                return false;
            _current.Instructions.Add(new CoreMoveInstruction(value, expression.Type, falseValue));
            _current.Terminator = new CoreJump(continuation.Id);

            _current = continuation;
            return true;
        }

        private static object? DefaultCoreValue(IrType type)
        {
            if (type.IsReference || type.IsString)
                return null;
            if (type.IsBoolean)
                return false;
            return type.Name switch
            {
                "char" => '\0',
                "float" => 0f,
                "double" => 0d,
                "decimal" => 0m,
                "long" or "uint" => 0L,
                "ulong" => 0UL,
                _ => 0
            };
        }

        private static bool IsConsoleWriteLine(IrInvocationExpression invocation) =>
            invocation.Target is IrMemberExpression
            {
                Receiver: IrVariableExpression { Symbol.Name: "Console" },
                MemberName: "WriteLine"
            };

        private bool LowerAssignment(IrAssignmentExpression assignment, out CoreValueId value)
        {
            value = default;
            if (assignment.Target is not IrVariableExpression target ||
                !_symbols.TryGetValue(target.Symbol.Id, out var destination))
                return Fail("Core IR assignments require a bound local or parameter target.");
            if (!LowerExpression(assignment.Value, out var source))
                return false;

            if (assignment.Operator == IrAssignmentOperator.Assign)
            {
                _current.Instructions.Add(new CoreMoveInstruction(destination, target.Type, source));
            }
            else if (AssignmentOperator(assignment.Operator) is { } operation)
            {
                _current.Instructions.Add(
                    new CoreBinaryInstruction(destination, target.Type, operation, destination, source));
            }
            else
            {
                return Fail($"Assignment operator '{assignment.Operator}' is not supported by Core IR lowering.");
            }

            value = destination;
            return true;
        }

        private bool LowerIncrement(IrUnaryExpression unary, out CoreValueId value)
        {
            value = default;
            if (unary.Operand is not IrVariableExpression variable ||
                !_symbols.TryGetValue(variable.Symbol.Id, out var destination))
                return Fail("Core IR increment and decrement require a bound local or parameter.");

            var isPostfix = unary.Operator is IrUnaryOperator.PostIncrement or IrUnaryOperator.PostDecrement;
            var previous = default(CoreValueId);
            if (isPostfix)
            {
                previous = AllocateValue();
                _current.Instructions.Add(new CoreMoveInstruction(previous, variable.Type, destination));
            }

            var one = AllocateValue();
            _current.Instructions.Add(new CoreConstantInstruction(one, variable.Type, 1));
            var operation = unary.Operator is IrUnaryOperator.PreIncrement or IrUnaryOperator.PostIncrement
                ? IrBinaryOperator.Add
                : IrBinaryOperator.Subtract;
            _current.Instructions.Add(
                new CoreBinaryInstruction(destination, variable.Type, operation, destination, one));
            value = isPostfix ? previous : destination;
            return true;
        }

        private static IrBinaryOperator? AssignmentOperator(IrAssignmentOperator operation) => operation switch
        {
            IrAssignmentOperator.Add => IrBinaryOperator.Add,
            IrAssignmentOperator.Subtract => IrBinaryOperator.Subtract,
            IrAssignmentOperator.Multiply => IrBinaryOperator.Multiply,
            IrAssignmentOperator.Divide => IrBinaryOperator.Divide,
            IrAssignmentOperator.Remainder => IrBinaryOperator.Remainder,
            IrAssignmentOperator.BitwiseAnd => IrBinaryOperator.BitwiseAnd,
            IrAssignmentOperator.BitwiseOr => IrBinaryOperator.BitwiseOr,
            IrAssignmentOperator.ExclusiveOr => IrBinaryOperator.ExclusiveOr,
            _ => null
        };

        private CoreValueId AllocateValue() => new(++_nextValue);

        private MutableBlock CreateBlock()
        {
            var block = new MutableBlock(new CoreBlockId(_blocks.Count));
            _blocks.Add(block);
            return block;
        }

        private bool Fail(string reason)
        {
            _unsupportedReason ??= reason;
            return false;
        }

        private sealed class MutableBlock(CoreBlockId id)
        {
            public CoreBlockId Id { get; } = id;
            public List<CoreInstruction> Instructions { get; } = [];
            public CoreTerminator? Terminator { get; set; }
        }
    }
}
