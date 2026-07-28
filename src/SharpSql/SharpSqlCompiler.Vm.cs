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
            .Where(method => _recursiveMethods.Contains(method.Name) || ExceedsInlineBudget(method))
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
        method.Syntax.DescendantNodes()
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
            AddVmVariable(method, parameter.Name, parameter.Type, slot++);

        var declarations = definition.Body is null
            ? []
            : definition.Body
                .DescendantNodes(descendIntoChildren: node => node is not LocalFunctionStatementSyntax)
                .OfType<VariableDeclarationSyntax>();
        foreach (var declaration in declarations)
        {
            foreach (var variable in declaration.Variables)
            {
                var name = variable.Identifier.ValueText;
                if (method.Variables.ContainsKey(name))
                {
                    AddDiagnostic("SS5001", $"Shadowed local '{name}' is not supported by the stack-machine fallback yet.", variable);
                    continue;
                }

                var declared = CSharpType.From(declaration.Type);
                var type = declared == CSharpType.Unknown && variable.Initializer is not null
                    ? InferType(variable.Initializer.Value, method.Scope)
                    : declared;
                AddVmVariable(method, name, type, slot++);
            }
        }

        if (definition.Body is not null)
        {
            foreach (var forEach in definition.Body.DescendantNodes().OfType<ForEachStatementSyntax>())
            {
                var name = forEach.Identifier.ValueText;
                if (method.Variables.ContainsKey(name))
                    continue;
                var collectionType = InferType(forEach.Expression, method.Scope);
                var type = forEach.Type.IsVar && IsSequenceType(collectionType.Name)
                    ? SequenceElementType(collectionType.Name)
                    : CSharpType.From(forEach.Type);
                AddVmVariable(method, name, type, slot++);
            }
        }

        method.NextTemporarySlot = slot;
        _vmMethods.Add(definition.Name, method);
    }

    private void AddVmVariable(VmMethod method, string name, CSharpType type, int slot)
    {
        var sqlName = _names.Allocate($"_vm_{method.Definition.Name}_{name}");
        var variable = new VmVariable(name, type, slot, sqlName);
        method.Variables.Add(name, variable);
        method.Scope.Add(name, new VariableBinding(sqlName, type));
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
            _sql.Line($"DECLARE {variable.SqlName} {variable.Type.Sql};");
        foreach (var method in _vmMethods.Values.Where(method => method.ReturnSqlName is not null))
            _sql.Line($"DECLARE {method.ReturnSqlName} {method.Definition.ReturnType.Sql};");
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
        var storage = AllocateVmTemporary(CSharpType.Bool, context);
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

    private VmTemporary AllocateVmTemporary(CSharpType type, VmMethod? context)
    {
        if (context is not null)
            return new VmTemporary(type, context.NextTemporarySlot++, null, context);

        var sqlName = _names.Allocate("_vm_temp");
        _sql.Line($"DECLARE {sqlName} {type.Sql};");
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
        EmitLeadingComments(method.Definition.Syntax);
        _sql.Line($"-- stack-machine body: {method.Definition.Name}");
        EmitLabel(method.EntryLabel);
        _sql.Line($"SET {VmFrameId} = (SELECT MAX(__id) FROM {VmStack});");
        LoadVmRegisters(method);

        if (method.Definition.Body is not null)
            EmitVmStatementSequence(method.Definition.Body.Statements, method, null);
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
            EmitVmStatement(statement, method, loop);
    }

    private void EmitVmStatement(StatementSyntax statement, VmMethod method, LoopContext? loop)
    {
        EmitLeadingComments(statement);
        switch (statement)
        {
            case BlockSyntax block:
                EmitVmStatementSequence(block.Statements, method, loop);
                break;
            case LocalDeclarationStatementSyntax declaration:
                foreach (var variable in declaration.Declaration.Variables)
                {
                    var target = method.Variables[variable.Identifier.ValueText];
                    if (variable.Initializer is null)
                        _sql.Line($"SET {target.SqlName} = NULL;");
                    else
                        EmitVmExpression(
                            variable.Initializer.Value,
                            method.Scope,
                            method,
                            value => _sql.Line($"SET {target.SqlName} = {value};"));
                }
                break;
            case ExpressionStatementSyntax expression:
                EmitVmExpressionStatement(expression.Expression, method);
                break;
            case IfStatementSyntax @if:
                EmitVmExpression(@if.Condition, method.Scope, method, condition =>
                {
                    _sql.Line($"IF {VmPredicate(condition, @if.Condition, method.Scope)}");
                    EmitVmEmbedded(@if.Statement, method, loop);
                    if (@if.Else is not null)
                    {
                        _sql.Line("ELSE");
                        EmitVmEmbedded(@if.Else.Statement, method, loop);
                    }
                });
                break;
            case WhileStatementSyntax @while:
                EmitVmWhile(@while, method);
                break;
            case DoStatementSyntax @do:
                EmitVmDo(@do, method);
                break;
            case ForStatementSyntax @for:
                EmitVmFor(@for, method);
                break;
            case ForEachStatementSyntax forEach:
                EmitVmForEach(forEach, method);
                break;
            case BreakStatementSyntax:
                if (loop is null)
                    AddDiagnostic("SS2005", "break must be inside a loop.", statement);
                else
                    _sql.Line($"GOTO {loop.BreakLabel};");
                break;
            case ContinueStatementSyntax:
                if (loop is null)
                    AddDiagnostic("SS2001", "continue must be inside a loop.", statement);
                else
                    _sql.Line($"GOTO {loop.ContinueLabel};");
                break;
            case ReturnStatementSyntax @return:
                if (@return.Expression is null)
                    EmitVmReturn(method, "NULL");
                else
                    EmitVmExpression(
                        @return.Expression,
                        method.Scope,
                        method,
                        value => EmitVmReturn(method, value));
                break;
            case LocalFunctionStatementSyntax:
            case EmptyStatementSyntax:
                break;
            default:
                Unsupported(statement, "stack-machine statement");
                break;
        }
        EmitTrailingComments(statement);
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

    private string VmAssignmentLine(
        AssignmentExpressionSyntax assignment,
        string target,
        CSharpType targetType,
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

    private void EmitVmWhile(WhileStatementSyntax statement, VmMethod method)
    {
        var conditionLabel = _names.AllocateLabel("vm_while_condition");
        var continueLabel = _names.AllocateLabel("vm_while_continue");
        var breakLabel = _names.AllocateLabel("vm_while_break");
        EmitLabel(conditionLabel);
        EmitVmExpression(statement.Condition, method.Scope, method, condition =>
        {
            _sql.Line($"IF NOT ({VmPredicate(condition, statement.Condition, method.Scope)}) GOTO {breakLabel};");
            EmitVmEmbeddedContents(statement.Statement, method, new LoopContext(breakLabel, continueLabel));
            EmitLabel(continueLabel);
            _sql.Line($"GOTO {conditionLabel};");
            EmitLabel(breakLabel);
        });
    }

    private void EmitVmDo(DoStatementSyntax statement, VmMethod method)
    {
        var bodyLabel = _names.AllocateLabel("vm_do_body");
        var continueLabel = _names.AllocateLabel("vm_do_continue");
        var breakLabel = _names.AllocateLabel("vm_do_break");
        EmitLabel(bodyLabel);
        EmitVmEmbeddedContents(statement.Statement, method, new LoopContext(breakLabel, continueLabel));
        EmitLabel(continueLabel);
        EmitVmExpression(statement.Condition, method.Scope, method, condition =>
        {
            _sql.Line($"IF {VmPredicate(condition, statement.Condition, method.Scope)} GOTO {bodyLabel};");
            EmitLabel(breakLabel);
        });
    }

    private void EmitVmFor(ForStatementSyntax statement, VmMethod method)
    {
        if (statement.Declaration is not null)
        {
            var declaration = SyntaxFactory.LocalDeclarationStatement(statement.Declaration);
            EmitVmStatement(declaration, method, null);
        }
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
                _sql.Line($"IF NOT ({VmPredicate(condition, statement.Condition, method.Scope)}) GOTO {breakLabel};");
                EmitBody();
            });

        void EmitBody()
        {
            EmitVmEmbeddedContents(statement.Statement, method, new LoopContext(breakLabel, continueLabel));
            EmitLabel(continueLabel);
            foreach (var incrementor in statement.Incrementors)
                EmitVmExpressionStatement(incrementor, method);
            _sql.Line($"GOTO {conditionLabel};");
            EmitLabel(breakLabel);
        }
    }

    private void EmitVmEmbedded(StatementSyntax statement, VmMethod method, LoopContext? loop)
    {
        _sql.Line("BEGIN");
        using (_sql.Indent())
            EmitVmEmbeddedContents(statement, method, loop);
        _sql.Line("END;");
    }

    private void EmitVmForEach(ForEachStatementSyntax statement, VmMethod method)
    {
        var collectionType = InferType(statement.Expression, method.Scope);
        if (!IsSequenceType(collectionType.Name))
        {
            AddDiagnostic("SS6302", "foreach currently supports arrays and List<T>.", statement.Expression);
            return;
        }

        EmitVmExpression(statement.Expression, method.Scope, method, collection =>
        {
            var collectionStorage = AllocateVmTemporary(collectionType, method);
            StoreVmTemporary(collectionStorage, collection);
            var indexStorage = AllocateVmTemporary(CSharpType.Int, method);
            StoreVmTemporary(indexStorage, "0");
            var item = method.Variables[statement.Identifier.ValueText];
            var conditionLabel = _names.AllocateLabel("vm_foreach_condition");
            var continueLabel = _names.AllocateLabel("vm_foreach_continue");
            var breakLabel = _names.AllocateLabel("vm_foreach_break");

            EmitLabel(conditionLabel);
            var collectionValue = ReadVmTemporary(collectionStorage);
            var indexValue = ReadVmTemporary(indexStorage);
            _sql.Line($"IF {indexValue} >= {SequenceCountSql(collectionValue)} GOTO {breakLabel};");
            _sql.Line($"SET {item.SqlName} = {SequenceElementSql(collectionValue, indexValue, item.Type)};");
            EmitVmEmbeddedContents(statement.Statement, method, new LoopContext(breakLabel, continueLabel));
            EmitLabel(continueLabel);
            _sql.Line($"UPDATE {VmSlots} SET __value = CONVERT(SQL_VARIANT, CONVERT(INT, __value) + 1) WHERE __frame_id = {VmFrameId} AND __slot_id = {indexStorage.Slot};");
            _sql.Line($"GOTO {conditionLabel};");
            EmitLabel(breakLabel);
        });
    }

    private void EmitVmEmbeddedContents(StatementSyntax statement, VmMethod method, LoopContext? loop)
    {
        if (statement is BlockSyntax block)
            EmitVmStatementSequence(block.Statements, method, loop);
        else
            EmitVmStatement(statement, method, loop);
    }

    private string VmPredicate(string value, ExpressionSyntax original, VariableScope scope) =>
        InferType(original, scope).IsBoolean && IsPredicateShape(original)
            ? $"{value} = 1"
            : InferType(original, scope).IsBoolean
                ? $"{value} = 1"
                : value;

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

    private string ReadVmResult(CSharpType type)
    {
        if (type.IsString)
            return VmTextResult;
        if (type.Name == "byte[]")
            return VmBinaryResult;
        if (type.Name == "void")
            return "NULL";
        return $"CONVERT({type.Sql}, {VmResult})";
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

    private void StoreVmSlot(string frameId, int slot, CSharpType type, string value)
    {
        _sql.Line($"DELETE FROM {VmSlots} WHERE __frame_id = {frameId} AND __slot_id = {slot};");
        InsertVmSlot(frameId, slot, type, value);
    }

    private void InsertVmSlot(string frameId, int slot, CSharpType type, string value)
    {
        if (type.IsString)
            _sql.Line($"INSERT INTO {VmSlots} (__frame_id, __slot_id, __text_value) VALUES ({frameId}, {slot}, {value});");
        else if (type.Name == "byte[]")
            _sql.Line($"INSERT INTO {VmSlots} (__frame_id, __slot_id, __binary_value) VALUES ({frameId}, {slot}, {value});");
        else
            _sql.Line($"INSERT INTO {VmSlots} (__frame_id, __slot_id, __value) VALUES ({frameId}, {slot}, CONVERT(SQL_VARIANT, {value}));");
    }

    private static string ReadVmSlot(string frameId, int slot, CSharpType type)
    {
        if (type.IsString)
            return $"(SELECT __text_value FROM {VmSlots} WHERE __frame_id = {frameId} AND __slot_id = {slot})";
        if (type.Name == "byte[]")
            return $"(SELECT __binary_value FROM {VmSlots} WHERE __frame_id = {frameId} AND __slot_id = {slot})";
        return $"CONVERT({type.Sql}, (SELECT __value FROM {VmSlots} WHERE __frame_id = {frameId} AND __slot_id = {slot}))";
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

    private sealed record VmVariable(string Name, CSharpType Type, int Slot, string SqlName);
    private sealed record VmContinuation(int Id, string Label);
    private sealed record VmTemporary(CSharpType Type, int? Slot, string? SqlName, VmMethod? Context);
}
