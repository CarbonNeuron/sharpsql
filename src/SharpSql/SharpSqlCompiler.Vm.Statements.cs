using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SharpSql;

public sealed partial class SharpSqlCompiler
{
    private void EmitVmMethod(VmMethod method)
    {
        EmitLeadingComments(method.Definition.Source);
        _sql.Line($"-- stack-machine body: {method.Definition.Name}");
        EmitLabel(method.EntryLabel);
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
            case ProceduralTry @try:
                EmitVmTry(@try, method, loop);
                break;
            case ProceduralThrow @throw:
                EmitThrow(@throw, method.Scope, method);
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

    private void EmitVmTry(ProceduralTry statement, VmMethod method, LoopContext? loop)
    {
        if (statement.Catches.Count == 0)
        {
            AddDiagnostic("SS2010", "A SQL TRY block requires at least one supported catch clause.", statement.Source);
            EmitVmProceduralStatementSequence(statement.Body.Statements, method, loop);
            return;
        }

        _sql.Line("BEGIN TRY");
        using (_sql.Indent())
            EmitVmProceduralStatementSequence(statement.Body.Statements, method, loop);
        _sql.Line("END TRY");
        _sql.Line("BEGIN CATCH");
        using (_sql.Indent())
        {
            EmitCatchClauses(statement.Catches, method.Scope, (@catch, catchScope) =>
            {
                if (@catch.Exception is not null &&
                    catchScope.Find(@catch.Exception) is ExceptionVariableBinding binding)
                    method.Scope.Add(@catch.Exception, binding);
                EmitVmEmbedded(@catch.Body, method, loop);
            });
        }
        _sql.Line("END CATCH;");
    }

    private void EmitVmExpressionStatement(IrExpression expression, VmMethod method)
    {
        if (TryEmitHeapStatement(expression, method.Scope, method))
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
                EmitVmExpression(
                    invocation.Arguments[0],
                    method.Scope,
                    method,
                    value => EmitPrintSql(FormatTextValue(invocation.Arguments[0].Type, value)));
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
            if (UsesMemoryOptimizedRuntime)
            {
                _sql.Line($"UPDATE {VmSlotsTable} SET __scalar_value = CONVERT(VARBINARY(8000), CONVERT(INT, __scalar_value) + 1) WHERE __frame_id = {VmFrameId} AND __slot_id = {indexStorage.Slot}{VmExecutionPredicate()};");
            }
            else
            {
                _sql.Line($"UPDATE {VmSlotsTable} SET __value = CONVERT(SQL_VARIANT, CONVERT(INT, __value) + 1) WHERE __frame_id = {VmFrameId} AND __slot_id = {indexStorage.Slot}{VmExecutionPredicate()};");
            }
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

        _sql.Line($"SELECT {VmJump} = __return_id, {VmCallerFrameId} = __caller_id FROM {VmStackTable} WHERE __id = {VmFrameId}{VmExecutionPredicate()};");
        _sql.Line($"DELETE FROM {VmSlotsTable} WHERE __frame_id = {VmFrameId}{VmExecutionPredicate()};");
        _sql.Line($"DELETE FROM {VmStackTable} WHERE __id = {VmFrameId}{VmExecutionPredicate()};");
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
}
