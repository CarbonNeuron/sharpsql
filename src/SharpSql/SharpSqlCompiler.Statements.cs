using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SharpSql;

public sealed partial class SharpSqlCompiler
{
    private void EmitStatement(
        ProceduralStatement statement,
        VariableScope scope,
        InlineReturn? inlineReturn,
        LoopContext? loop,
        string? namePrefix)
    {
        EmitLeadingComments(statement.Source);
        switch (statement)
        {
            case ProceduralBlock block:
                EmitProceduralStatementSequence(block.Statements, scope.Child(), inlineReturn, loop, namePrefix);
                break;
            case ProceduralLocalFunction:
                break;
            case ProceduralDeclarationStatement declaration:
                EmitDeclaration(declaration.Declaration, scope, inlineReturn, loop, namePrefix);
                break;
            case ProceduralExpressionStatement expression:
                EmitExpressionStatement(expression.Expression, scope, inlineReturn, namePrefix);
                break;
            case ProceduralIf @if:
                EmitIf(@if, scope, inlineReturn, loop, namePrefix);
                break;
            case ProceduralWhile @while:
                EmitWhile(@while, scope, inlineReturn, namePrefix);
                break;
            case ProceduralDo @do:
                EmitDo(@do, scope, inlineReturn, namePrefix);
                break;
            case ProceduralFor @for:
                EmitFor(@for, scope, inlineReturn, namePrefix);
                break;
            case ProceduralForEach forEach:
                EmitForEach(forEach, scope, inlineReturn, namePrefix);
                break;
            case ProceduralTry @try:
                EmitTry(@try, scope, inlineReturn, loop, namePrefix);
                break;
            case ProceduralThrow @throw:
                EmitThrow(@throw, scope, _proceduralVmContext);
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
            case ProceduralReturn @return when inlineReturn is not null:
                if (@return.Expression is not null && inlineReturn.TargetSql is not null)
                    _sql.Line($"SET {inlineReturn.TargetSql} = {EmitScalar(@return.Expression, scope)};");
                _sql.Line($"GOTO {inlineReturn.EndLabel};");
                break;
            case ProceduralReturn @return:
                if (@return.Expression is null)
                {
                    if (UsesDurableRuntime)
                        _sql.Line($"GOTO {RuntimeCleanupLabel};");
                    else
                        _sql.Line("RETURN;");
                }
                else
                    AddDiagnostic("SS2003", "A value cannot be returned from the script entry point.", @return.Source);
                break;
            case ProceduralEmpty:
                break;
            case ProceduralUnsupported unsupported:
                Unsupported(unsupported.Source, "statement");
                break;
        }
        EmitTrailingComments(statement.Source);
    }

    private void EmitProceduralStatementSequence(
        IEnumerable<ProceduralStatement> statements,
        VariableScope scope,
        InlineReturn? inlineReturn,
        LoopContext? loop,
        string? namePrefix)
    {
        foreach (var statement in statements)
            EmitStatement(statement, scope, inlineReturn, loop, namePrefix);
    }

    private void EmitTry(
        ProceduralTry statement,
        VariableScope scope,
        InlineReturn? inlineReturn,
        LoopContext? loop,
        string? namePrefix)
    {
        if (statement.Catches.Count == 0)
        {
            AddDiagnostic("SS2010", "A SQL TRY block requires at least one supported catch clause.", statement.Source);
            EmitProceduralStatementSequence(statement.Body.Statements, scope.Child(), inlineReturn, loop, namePrefix);
            return;
        }

        _sql.Line("BEGIN TRY");
        using (_sql.Indent())
            EmitProceduralStatementSequence(statement.Body.Statements, scope.Child(), inlineReturn, loop, namePrefix);
        _sql.Line("END TRY");
        _sql.Line("BEGIN CATCH");
        using (_sql.Indent())
            EmitCatchClauses(
                statement.Catches,
                scope,
                (@catch, catchScope) => EmitEmbedded(@catch.Body, catchScope, inlineReturn, loop, namePrefix));
        _sql.Line("END CATCH;");
    }

    private void EmitCatchClauses(
        IReadOnlyList<ProceduralCatch> catches,
        VariableScope parentScope,
        Action<ProceduralCatch, VariableScope> emitBody)
    {
        var number = _names.Allocate("_catch_number");
        var message = _names.Allocate("_catch_message");
        var severity = _names.Allocate("_catch_severity");
        var state = _names.Allocate("_catch_state");
        var procedure = _names.Allocate("_catch_procedure");
        var lineNumber = _names.Allocate("_catch_line_number");
        _sql.Line($"DECLARE {number} INT = ERROR_NUMBER();");
        _sql.Line($"DECLARE {message} NVARCHAR(4000) = ERROR_MESSAGE();");
        _sql.Line($"DECLARE {severity} INT = ERROR_SEVERITY();");
        _sql.Line($"DECLARE {state} INT = ERROR_STATE();");
        _sql.Line($"DECLARE {procedure} NVARCHAR(128) = ERROR_PROCEDURE();");
        _sql.Line($"DECLARE {lineNumber} INT = ERROR_LINE();");
        if (UsesServiceBrokerRuntime)
        {
            _sql.Line(
                $"IF {number} IN (1205, {ServiceBrokerWorkerDispatcherSqlEmitter.RetryableWorkerDeadlockErrorNumber}) THROW;");
        }

        var hasConditionalCatch = false;
        var hasCatchAll = false;
        foreach (var @catch in catches)
        {
            var catchScope = parentScope.Child();
            if (@catch.Exception is not null)
            {
                catchScope.Add(@catch.Exception, new ExceptionVariableBinding(
                    @catch.Exception.Type,
                    number,
                    message,
                    severity,
                    state,
                    procedure,
                    lineNumber));
            }

            var condition = EmitCatchCondition(@catch, number, catchScope);
            if (condition is null)
            {
                if (hasConditionalCatch)
                    _sql.Line("ELSE");
                emitBody(@catch, catchScope);
                hasCatchAll = true;
                break;
            }

            _sql.Line(hasConditionalCatch ? $"ELSE IF {condition}" : $"IF {condition}");
            emitBody(@catch, catchScope);
            hasConditionalCatch = true;
        }

        if (!hasCatchAll)
        {
            if (hasConditionalCatch)
                _sql.Line("ELSE");
            _sql.Line("BEGIN");
            using (_sql.Indent())
                _sql.Line("THROW;");
            _sql.Line("END;");
        }
    }

    private string? EmitCatchCondition(
        ProceduralCatch @catch,
        string errorNumber,
        VariableScope scope)
    {
        string? typeCondition;
        if (@catch.ExceptionType is null || RuntimeErrorCatalog.IsCatchAll(@catch.ExceptionType))
        {
            typeCondition = null;
        }
        else if (RuntimeErrorCatalog.IsDatabaseException(@catch.ExceptionType))
        {
            typeCondition = $"({errorNumber} < 51000 OR {errorNumber} > 51999)";
        }
        else if (RuntimeErrorCatalog.ErrorNumbersCaughtBy(@catch.ExceptionType) is { } numbers)
        {
            typeCondition = numbers.Count == 1
                ? $"{errorNumber} = {numbers[0]}"
                : $"{errorNumber} IN ({string.Join(", ", numbers)})";
        }
        else
        {
            AddDiagnostic(
                "SS2011",
                $"Catch type '{@catch.ExceptionType.MetadataName}' does not have a SQL exception mapping.",
                @catch.Source);
            typeCondition = "1 = 0";
        }

        if (@catch.Filter is null)
            return typeCondition;
        if (ContainsRuntimeExpression(@catch.Filter))
        {
            AddDiagnostic("SS2012", "Catch filters cannot invoke the SharpSql runtime.", @catch.Filter.Source);
            return typeCondition is null ? "1 = 0" : $"({typeCondition}) AND (1 = 0)";
        }

        var filterCondition = EmitPredicate(@catch.Filter, scope);
        return typeCondition is null
            ? filterCondition
            : $"({typeCondition}) AND ({filterCondition})";
    }

    private void EmitThrow(ProceduralThrow statement, VariableScope scope, VmMethod? vmContext)
    {
        if (statement.Expression is null ||
            statement.Expression is IrVariableExpression variable &&
            scope.Find(variable.Symbol) is ExceptionVariableBinding)
        {
            _sql.Line("THROW;");
            return;
        }

        if (statement.ExceptionType?.MetadataName != "System.ApplicationException" ||
            statement.Expression is not IrObjectCreationExpression creation)
        {
            AddDiagnostic(
                "SS2013",
                "Only rethrows and construction of System.ApplicationException can be lowered to SQL THROW.",
                statement.Source);
            return;
        }

        if (creation.Arguments.Count > 1 || creation.Initializers.Count > 0)
        {
            AddDiagnostic(
                "SS2013",
                "ApplicationException currently supports only the parameterless or message constructor.",
                statement.Source);
            return;
        }

        var messageSql = _names.Allocate("_application_exception_message");
        _sql.Line($"DECLARE {messageSql} NVARCHAR(2048);");
        if (creation.Arguments.Count == 0)
        {
            _sql.Line($"SET {messageSql} = N'Error in the application.';");
            _sql.Line($"THROW {RuntimeErrorCatalog.ApplicationExceptionErrorNumber}, {messageSql}, 1;");
            return;
        }

        var messageExpression = creation.Arguments[0];
        if (ContainsRuntimeExpression(messageExpression))
        {
            EmitVmExpression(messageExpression, scope, vmContext, EmitApplicationThrow);
            return;
        }
        EmitApplicationThrow(EmitScalar(messageExpression, scope));

        void EmitApplicationThrow(string value)
        {
            _sql.Line($"SET {messageSql} = LEFT(COALESCE(CONVERT(NVARCHAR(MAX), {value}), N'Error in the application.'), 2048);");
            _sql.Line($"THROW {RuntimeErrorCatalog.ApplicationExceptionErrorNumber}, {messageSql}, 1;");
        }
    }

    private void EmitDeclaration(
        ProceduralDeclaration declaration,
        VariableScope scope,
        InlineReturn? inlineReturn,
        LoopContext? loop,
        string? namePrefix)
    {
        foreach (var variable in declaration.Variables)
        {
            var sourceName = variable.Name;
            var type = variable.DeclaredType;
            var sqlName = _names.Allocate(namePrefix is null ? sourceName : $"{namePrefix}_{sourceName}");

            if (variable.Initializer is not null &&
                TryEmitLinqDelegateDeclaration(variable.Initializer, sourceName, type, scope))
                continue;

            if (variable.Initializer is not null &&
                TryEmitLinqQueryDeclaration(variable.Initializer, sourceName, sqlName, type, scope))
                continue;

            if (variable.Initializer is not null && HasCSharpSource(variable.Initializer.Source) &&
                TryEmitLinqDelegateDeclaration(CSharpExpression(variable.Initializer), sourceName, sqlName, type, scope))
                continue;

            if (variable.Initializer is not null && HasCSharpSource(variable.Initializer.Source) &&
                TryEmitLinqQueryDeclaration(CSharpExpression(variable.Initializer), sourceName, sqlName, type, scope))
                continue;

            if (variable.Initializer is not null && ContainsRuntimeExpression(variable.Initializer))
            {
                _sql.Line($"DECLARE {sqlName} {type.SqlType()};");
                EmitVmExpression(
                    variable.Initializer,
                    scope,
                    _proceduralVmContext,
                    value => _sql.Line($"SET {sqlName} = {value};"));
                scope.Add(variable.Symbol, new ScalarVariableBinding(sqlName, type));
                continue;
            }

            if (variable.Initializer is IrInvocationExpression invocation &&
                TryGetComplexMethod(invocation, out var method))
            {
                EmitComplexInline(method, InvocationArgumentExpressions(invocation, method), scope, sqlName, type, declareTarget: true);
                scope.Add(variable.Symbol, new ScalarVariableBinding(sqlName, type));
                continue;
            }

            var initializer = variable.Initializer is null
                ? string.Empty
                : $" = {EmitScalar(variable.Initializer, scope)}";
            _sql.Line($"DECLARE {sqlName} {type.SqlType()}{initializer};");
            scope.Add(variable.Symbol, new ScalarVariableBinding(sqlName, type));
        }
    }

    private void EmitIf(
        ProceduralIf statement,
        VariableScope scope,
        InlineReturn? inlineReturn,
        LoopContext? loop,
        string? namePrefix)
    {
        if (ContainsRuntimeExpression(statement.Condition))
            EmitVmExpression(
                statement.Condition,
                scope,
                _proceduralVmContext,
                condition => EmitBody(VmPredicate(condition, statement.Condition)));
        else
            EmitBody(EmitPredicate(statement.Condition, scope));

        void EmitBody(string condition)
        {
            _sql.Line($"IF {condition}");
            EmitEmbedded(statement.Then, scope.Child(), inlineReturn, loop, namePrefix);
            if (statement.Else is { } elseStatement)
            {
                _sql.Line("ELSE");
                EmitEmbedded(elseStatement, scope.Child(), inlineReturn, loop, namePrefix);
            }
        }
    }

    private void EmitWhile(
        ProceduralWhile statement,
        VariableScope scope,
        InlineReturn? inlineReturn,
        string? namePrefix)
    {
        var conditionLabel = _names.AllocateLabel("while_condition");
        var continueLabel = _names.AllocateLabel("while_continue");
        var breakLabel = _names.AllocateLabel("while_break");
        EmitLabel(conditionLabel);
        if (ContainsRuntimeExpression(statement.Condition))
            EmitVmExpression(
                statement.Condition,
                scope,
                _proceduralVmContext,
                condition => EmitBody(VmPredicate(condition, statement.Condition)));
        else
            EmitBody(EmitPredicate(statement.Condition, scope));

        void EmitBody(string condition)
        {
            _sql.Line($"IF NOT ({condition}) GOTO {breakLabel};");
            EmitEmbeddedContents(
                statement.Body,
                scope.Child(),
                inlineReturn,
                new LoopContext(breakLabel, continueLabel),
                namePrefix);
            EmitLabel(continueLabel);
            _sql.Line($"GOTO {conditionLabel};");
            EmitLabel(breakLabel);
        }
    }

    private void EmitDo(
        ProceduralDo statement,
        VariableScope scope,
        InlineReturn? inlineReturn,
        string? namePrefix)
    {
        var bodyLabel = _names.AllocateLabel("do_body");
        var continueLabel = _names.AllocateLabel("do_continue");
        var breakLabel = _names.AllocateLabel("do_break");
        EmitLabel(bodyLabel);
        EmitEmbeddedContents(
            statement.Body,
            scope.Child(),
            inlineReturn,
            new LoopContext(breakLabel, continueLabel),
            namePrefix);
        EmitLabel(continueLabel);
        if (ContainsRuntimeExpression(statement.Condition))
            EmitVmExpression(
                statement.Condition,
                scope,
                _proceduralVmContext,
                condition => EmitCondition(VmPredicate(condition, statement.Condition)));
        else
            EmitCondition(EmitPredicate(statement.Condition, scope));

        void EmitCondition(string condition)
        {
            _sql.Line($"IF {condition} GOTO {bodyLabel};");
            EmitLabel(breakLabel);
        }
    }

    private void EmitFor(
        ProceduralFor statement,
        VariableScope parentScope,
        InlineReturn? inlineReturn,
        string? namePrefix)
    {
        var scope = parentScope.Child();
        if (statement.Declaration is not null)
            EmitDeclaration(statement.Declaration, scope, inlineReturn, null, namePrefix);
        foreach (var initializer in statement.Initializers)
            EmitExpressionStatement(initializer, scope, inlineReturn, namePrefix);

        var conditionLabel = _names.AllocateLabel("for_condition");
        var continueLabel = _names.AllocateLabel("for_continue");
        var breakLabel = _names.AllocateLabel("for_break");
        EmitLabel(conditionLabel);
        if (statement.Condition is not null && ContainsRuntimeExpression(statement.Condition))
            EmitVmExpression(
                statement.Condition,
                scope,
                _proceduralVmContext,
                condition => EmitBody(VmPredicate(condition, statement.Condition)));
        else
            EmitBody(statement.Condition is null ? "1 = 1" : EmitPredicate(statement.Condition, scope));

        void EmitBody(string condition)
        {
            _sql.Line($"IF NOT ({condition}) GOTO {breakLabel};");
            EmitEmbeddedContents(
                statement.Body,
                scope,
                inlineReturn,
                new LoopContext(breakLabel, continueLabel),
                namePrefix);
            EmitLabel(continueLabel);
            foreach (var incrementor in statement.Incrementors)
                EmitExpressionStatement(incrementor, scope, inlineReturn, namePrefix);
            _sql.Line($"GOTO {conditionLabel};");
            EmitLabel(breakLabel);
        }
    }

    private void EmitEmbedded(
        ProceduralStatement statement,
        VariableScope scope,
        InlineReturn? inlineReturn,
        LoopContext? loop,
        string? namePrefix)
    {
        _sql.Line("BEGIN");
        using (_sql.Indent())
            EmitEmbeddedContents(statement, scope, inlineReturn, loop, namePrefix);
        _sql.Line("END;");
    }

    private void EmitForEach(
        ProceduralForEach statement,
        VariableScope parentScope,
        InlineReturn? inlineReturn,
        string? namePrefix)
    {
        if (IsLinqQueryExpression(CSharpExpression(statement.SourceExpression), parentScope) &&
            TryBuildLinqQuery(CSharpExpression(statement.SourceExpression), parentScope, substitutions: null, out var query))
        {
            EmitLinqForEach(statement, query, parentScope, inlineReturn, namePrefix);
            return;
        }

        var collectionType = statement.SourceExpression.Facts.Type;
        if (!IsSequenceType(collectionType.Name))
        {
            AddDiagnostic("SS6302", "foreach currently supports arrays and List<T>.", statement.SourceExpression.Source);
            return;
        }

        EmitVmExpression(statement.SourceExpression, parentScope, _proceduralVmContext, collectionValue =>
        {
            var scope = parentScope.Child();
            var collectionSql = _names.Allocate("_foreach_collection");
            var indexSql = _names.Allocate("_foreach_index");
            var itemType = statement.ElementType;
            var itemSql = _names.Allocate(statement.Element.Name);
            var conditionLabel = _names.AllocateLabel("foreach_condition");
            var continueLabel = _names.AllocateLabel("foreach_continue");
            var breakLabel = _names.AllocateLabel("foreach_break");

            _sql.Line($"DECLARE {collectionSql} INT = {collectionValue};");
            _sql.Line($"DECLARE {indexSql} INT = 0;");
            _sql.Line($"DECLARE {itemSql} {itemType.SqlType()};");
            scope.Add(statement.Element, new ScalarVariableBinding(itemSql, itemType));
            EmitLabel(conditionLabel);
            _sql.Line($"IF {indexSql} >= {SequenceCountSql(collectionSql)} GOTO {breakLabel};");
            _sql.Line($"SET {itemSql} = {SequenceElementSql(collectionSql, indexSql, itemType)};");
            EmitEmbeddedContents(
                statement.Body,
                scope,
                inlineReturn,
                new LoopContext(breakLabel, continueLabel),
                namePrefix);
            EmitLabel(continueLabel);
            _sql.Line($"SET {indexSql} = {indexSql} + 1;");
            _sql.Line($"GOTO {conditionLabel};");
            EmitLabel(breakLabel);
        });
    }

    private void EmitPrintSql(string value)
    {
        if (_emittingServiceBrokerWorker)
        {
            var output = _names.Allocate("_async_output");
            _sql.Line($"DECLARE {output} NVARCHAR(MAX);");
            _sql.Line($"SET {output} = COALESCE(CONVERT(NVARCHAR(MAX), {value}), N'');");
            _sql.Line($"EXEC [SharpSql].[AppendOutput] @ExecutionId = {RuntimeExecutionId}, @OutputText = {output};");
            return;
        }

        if (!value.Contains("(SELECT", StringComparison.Ordinal) &&
            !value.Contains("EXISTS (", StringComparison.Ordinal))
        {
            _sql.Line($"PRINT {value};");
            return;
        }

        var temporary = _names.Allocate("_print");
        _sql.Line($"DECLARE {temporary} NVARCHAR(MAX);");
        _sql.Line($"SET {temporary} = CONVERT(NVARCHAR(MAX), {value});");
        _sql.Line($"PRINT {temporary};");
    }

    private void EmitEmbeddedContents(
        ProceduralStatement statement,
        VariableScope scope,
        InlineReturn? inlineReturn,
        LoopContext? loop,
        string? namePrefix)
    {
        if (statement is ProceduralBlock block)
            EmitProceduralStatementSequence(block.Statements, scope, inlineReturn, loop, namePrefix);
        else
            EmitStatement(statement, scope, inlineReturn, loop, namePrefix);
    }

    private void EmitComplexInline(
        MethodDefinition method,
        IReadOnlyList<IrExpression> arguments,
        VariableScope callerScope,
        string? targetSql,
        IrType targetType,
        bool declareTarget)
    {
        if (TryEmitNativeKernelCall(method, arguments, callerScope, targetSql, targetType, declareTarget))
            return;

        if (!CanInline(method, arguments.Count))
            return;

        EmitLeadingComments(method.Source);

        var id = ++_inlineId;
        var prefix = $"_{method.Name.ToLowerInvariant()}_{id}";
        var methodScope = callerScope.Child();

        for (var index = 0; index < method.Parameters.Count; index++)
        {
            var parameter = method.Parameters[index];
            if ((IsSequenceType(parameter.Type.Name) || IsLinqSequenceType(parameter.Type.Name)) &&
                TryBuildLinqQuery(arguments[index], callerScope, substitutions: null, out var argumentQuery))
            {
                methodScope.Add(parameter.Symbol, new QueryVariableBinding(parameter.Type, argumentQuery));
                continue;
            }
            var parameterSql = _names.Allocate($"{prefix}_{parameter.Name}");
            var argumentSql = EmitScalar(arguments[index], callerScope);
            _sql.Line($"DECLARE {parameterSql} {parameter.Type.SqlType()} = {argumentSql};");
            methodScope.Add(parameter.Symbol, new ScalarVariableBinding(parameterSql, parameter.Type));
        }

        if (targetSql is not null && declareTarget)
            _sql.Line($"DECLARE {targetSql} {targetType.SqlType()};");

        var endLabel = _names.AllocateLabel($"{prefix}_end");
        var inlineReturn = new InlineReturn(targetSql, endLabel);

        if (method.Body is not null)
            EmitProceduralStatementSequence(method.Body.Statements, methodScope, inlineReturn, null, prefix);
        else if (method.ExpressionBody is not null)
        {
            if (targetSql is not null)
                _sql.Line($"SET {targetSql} = {EmitScalar(method.ExpressionBody, methodScope)};");
            _sql.Line($"GOTO {endLabel};");
        }
        EmitLabel(endLabel);
    }

    private bool CanInline(MethodDefinition method, int argumentCount)
    {
        if (argumentCount != method.Parameters.Count)
        {
            AddDiagnostic("SS3001", $"Method '{method.Name}' expects {method.Parameters.Count} arguments, but received {argumentCount}.", method.Source);
            return false;
        }

        if (method.ReturnType.Name != "void" &&
            method.PureExpression is null &&
            method.Flow.EndPointIsReachable)
        {
            AddDiagnostic(
                "SS3004",
                $"Method '{method.Name}' can reach its endpoint without returning a value.",
                method.Source);
            return false;
        }

        if (_methodGraph?.RecursiveMethodIds.Contains(method.Id) == true)
        {
            AddDiagnostic("SS3002", $"Recursive method '{method.Name}' needs the planned temporary-procedure fallback.", method.Source);
            return false;
        }

        if (ExceedsInlineBudget(method))
        {
            AddDiagnostic("SS3003", $"Method '{method.Name}' exceeds the configured inlining budget.", method.Source);
            return false;
        }

        return true;
    }

    private bool TryGetComplexMethod(IrInvocationExpression invocation, out MethodDefinition method)
    {
        if (!TryGetRuntimeDispatch(invocation, out _) &&
            TryGetMethod(invocation, out method!) && method.PureExpression is null)
            return true;
        method = null!;
        return false;
    }

}

