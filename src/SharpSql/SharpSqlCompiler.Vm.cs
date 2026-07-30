using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SharpSql;

public sealed partial class SharpSqlCompiler
{
    private readonly Dictionary<IrMethodId, VmMethod> _vmMethods = [];
    private readonly List<VmContinuation> _vmContinuations = [];
    private int _nextVmMethodId;
    private int _nextVmContinuationId;

    private const string VmStack = "#__sharpsql_stack";
    private const string VmSlots = "#__sharpsql_slots";
    private string MemoryOptimizedVmStack =>
        $"{SqlIdentifier.Quote(_options.ApplicationSchema, nameof(TranspileOptions.ApplicationSchema))}." +
        $"[{MemoryOptimizedRuntimeSqlEmitter.VmStackTableName(_effectiveRuntime.Durability)}]";
    private string MemoryOptimizedVmSlots =>
        $"{SqlIdentifier.Quote(_options.ApplicationSchema, nameof(TranspileOptions.ApplicationSchema))}." +
        $"[{MemoryOptimizedRuntimeSqlEmitter.VmSlotsTableName(_effectiveRuntime.Durability)}]";
    private const string DurableVmStack = "[SharpSql].[__sharpsql_stack]";
    private const string DurableVmSlots = "[SharpSql].[__sharpsql_slots]";
    private const string VmFrameId = "@__sharpsql_frame_id";
    private const string VmNewFrameId = "@__sharpsql_new_frame_id";
    private const string VmCallerFrameId = "@__sharpsql_caller_frame_id";
    private const string VmJump = "@__sharpsql_jump";
    private const string VmFunctionId = "@__sharpsql_function_id";
    private const string VmResult = "@__sharpsql_result";
    private const string VmTextResult = "@__sharpsql_text_result";
    private const string VmBinaryResult = "@__sharpsql_binary_result";

    private string VmDispatchLabel => "__sharpsql_dispatch";
    private string VmFunctionDispatchLabel => "__sharpsql_function_dispatch";
    private string VmHaltLabel => "__sharpsql_halt";
    private bool UsesDurableVmStorage => UsesDurableRuntime && !UsesMemoryOptimizedRuntime;
    private bool UsesSharedVmStorage => UsesDurableVmStorage || UsesMemoryOptimizedRuntime;
    private string VmStackTable => UsesMemoryOptimizedRuntime
        ? MemoryOptimizedVmStack
        : UsesDurableVmStorage ? DurableVmStack : VmStack;
    private string VmSlotsTable => UsesMemoryOptimizedRuntime
        ? MemoryOptimizedVmSlots
        : UsesDurableVmStorage ? DurableVmSlots : VmSlots;
    private string VmExecutionPredicate(string? alias = null) => UsesSharedVmStorage
        ? $" AND {(alias is null ? string.Empty : alias + ".")}__execution_id = {RuntimeExecutionId}"
        : string.Empty;

    private void PrepareVmMethods()
    {
        PrepareRuntimeDispatch();
        var graph = _methodGraph ?? throw new InvalidOperationException("Method graph has not been prepared.");
        var roots = _methods.Values
            .Where(method => (!UsesServiceBrokerRuntime || !method.IsAsync) &&
                (graph.RecursiveMethodIds.Contains(method.Id) || ExceedsInlineBudget(method) || MethodUsesRandom(method)))
            .Select(method => method.Id)
            .ToHashSet();
        var dispatchMethods = _runtimeDispatchSlots.Values
            .SelectMany(slot => slot.Targets.Select(target => target.Method.Id).Append(slot.Method.Id))
            .ToHashSet();
        roots.UnionWith(graph.ConnectedClosure(dispatchMethods)
            .Where(id => _methods.TryGetValue(id, out var method) &&
                (!UsesServiceBrokerRuntime || !method.IsAsync) &&
                !method.IsAbstract && (method.Body is not null || method.ExpressionBody is not null)));

        foreach (var method in _methods.Values.Where(method => roots.Contains(method.Id)))
            AddVmMethod(method);
    }

    private bool ExceedsInlineBudget(MethodDefinition method) =>
        method.StatementCount > _options.MaxInlineStatements ||
        (long)Math.Max(1, method.StatementCount) * (_methodGraph?.CallSiteCount(method.Id) ?? 0) >
        (long)_options.MaxInlineStatements * _options.MaxInlineCallSites;

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
        _vmMethods.Add(definition.Id, method);
    }

    private void AddVmVariable(VmMethod method, IrSymbol symbol, int slot)
    {
        var name = symbol.Name;
        var type = symbol.Type;
        var sqlName = _names.Allocate($"_vm_{method.Definition.Name}_{name}");
        var variable = new VmVariable(name, type, slot, sqlName);
        method.Variables.Add(name, variable);
        method.Scope.Add(symbol, new ScalarVariableBinding(sqlName, type));
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
            case ProceduralTry @try:
                foreach (var descendant in DescendantStatements(@try.Body))
                    yield return descendant;
                foreach (var @catch in @try.Catches)
                    foreach (var descendant in DescendantStatements(@catch.Body))
                        yield return descendant;
                break;
        }
    }

    private void EmitVmPreamble()
    {
        if (_vmMethods.Count == 0)
            return;

        _sql.Line("-- SharpSql stack-machine runtime");
        if (UsesMemoryOptimizedRuntime)
        {
            _sql.Line("-- SharpSql database-global memory-optimized stack-machine runtime");
            _sql.Line($"IF OBJECT_ID({SqlIdentifier.UnicodeLiteral(MemoryOptimizedVmStack)}, N'U') IS NULL");
            using (_sql.Indent())
                _sql.Line($"THROW {MemoryOptimizedRuntimeSqlEmitter.MissingPhysicalTableErrorNumber}, 'Provision the SharpSql memory-optimized runtime before executing this program.', 1;");
            _sql.Line($"IF OBJECT_ID({SqlIdentifier.UnicodeLiteral(MemoryOptimizedVmSlots)}, N'U') IS NULL");
            using (_sql.Indent())
                _sql.Line($"THROW {MemoryOptimizedRuntimeSqlEmitter.MissingPhysicalTableErrorNumber}, 'Provision the SharpSql memory-optimized runtime before executing this program.', 1;");
        }
        else if (UsesDurableVmStorage)
        {
            _sql.Line($"IF OBJECT_ID(N'{DurableVmStack}', N'U') IS NULL");
            _sql.Line("BEGIN");
            using (_sql.Indent())
            {
                _sql.Line($"CREATE TABLE {DurableVmStack} (");
                using (_sql.Indent())
                {
                    _sql.Line("__execution_id UNIQUEIDENTIFIER NOT NULL,");
                    _sql.Line("__id INT IDENTITY(1,1) NOT NULL PRIMARY KEY,");
                    _sql.Line("__function_id INT NOT NULL,");
                    _sql.Line("__return_id INT NOT NULL,");
                    _sql.Line("__caller_id INT NULL");
                }
                _sql.Line(");");
            }
            _sql.Line("END;");
            _sql.Line($"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'{DurableVmStack}') AND name = N'IX___sharpsql_stack_execution')");
            using (_sql.Indent())
                _sql.Line($"CREATE INDEX [IX___sharpsql_stack_execution] ON {DurableVmStack} (__execution_id);");
            _sql.Line($"IF OBJECT_ID(N'{DurableVmSlots}', N'U') IS NULL");
            _sql.Line("BEGIN");
            using (_sql.Indent())
            {
                _sql.Line($"CREATE TABLE {DurableVmSlots} (");
                using (_sql.Indent())
                {
                    _sql.Line("__execution_id UNIQUEIDENTIFIER NOT NULL,");
                    _sql.Line("__frame_id INT NOT NULL,");
                    _sql.Line("__slot_id INT NOT NULL,");
                    _sql.Line("__value SQL_VARIANT NULL,");
                    _sql.Line("__text_value NVARCHAR(MAX) NULL,");
                    _sql.Line("__binary_value VARBINARY(MAX) NULL,");
                    _sql.Line("PRIMARY KEY (__execution_id, __frame_id, __slot_id)");
                }
                _sql.Line(");");
            }
            _sql.Line("END;");
        }
        else
        {
            _sql.Line($"DROP TABLE IF EXISTS {VmSlots};");
            _sql.Line($"DROP TABLE IF EXISTS {VmStack};");
            _sql.Line($"CREATE TABLE {VmStack} (");
            using (_sql.Indent())
            {
                _sql.Line("__id INT IDENTITY(1,1) NOT NULL PRIMARY KEY,");
                _sql.Line("__function_id INT NOT NULL,");
                _sql.Line("__return_id INT NOT NULL,");
                _sql.Line("__caller_id INT NULL");
            }
            _sql.Line(");");
            _sql.Line($"CREATE TABLE {VmSlots} (");
            using (_sql.Indent())
            {
                _sql.Line("__frame_id INT NOT NULL,");
                _sql.Line("__slot_id INT NOT NULL,");
                _sql.Line("__value SQL_VARIANT NULL,");
                _sql.Line("__text_value NVARCHAR(MAX) NULL,");
                _sql.Line("__binary_value VARBINARY(MAX) NULL,");
                _sql.Line("PRIMARY KEY (__frame_id, __slot_id)");
            }
            _sql.Line(");");
        }
        _sql.Line($"DECLARE {VmFrameId} INT;");
        _sql.Line($"DECLARE {VmNewFrameId} INT;");
        _sql.Line($"DECLARE {VmCallerFrameId} INT;");
        _sql.Line($"DECLARE {VmJump} INT;");
        _sql.Line($"DECLARE {VmFunctionId} INT;");
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

        EmitLabel(VmFunctionDispatchLabel);
        _sql.Line($"SELECT {VmFunctionId} = __function_id FROM {VmStackTable} WHERE __id = {VmFrameId}{VmExecutionPredicate()};");
        foreach (var method in _vmMethods.Values)
            _sql.Line($"IF {VmFunctionId} = {method.Id} GOTO {method.EntryLabel};");
        _sql.Line("THROW 51007, 'Virtual dispatch target was not found.', 1;");
        _sql.Line();

        EmitLabel(VmDispatchLabel);
        foreach (var continuation in _vmContinuations)
            _sql.Line($"IF {VmJump} = {continuation.Id} GOTO {continuation.Label};");
        _sql.Line($"GOTO {VmHaltLabel};");
        _sql.Line();
        EmitLabel(VmHaltLabel);
        if (!UsesDurableVmStorage && !UsesMemoryOptimizedRuntime)
        {
            _sql.Line($"DROP TABLE IF EXISTS {VmSlots};");
            _sql.Line($"DROP TABLE IF EXISTS {VmStack};");
        }
    }

    private void EmitDurableVmCleanup()
    {
        if (!UsesSharedVmStorage || _vmMethods.Count == 0)
            return;

        _sql.Line($"DELETE FROM {VmSlotsTable} WHERE __execution_id = {RuntimeExecutionId};");
        _sql.Line($"DELETE FROM {VmStackTable} WHERE __execution_id = {RuntimeExecutionId};");
    }

}
