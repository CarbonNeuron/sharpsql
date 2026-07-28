using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SharpSql;

public sealed partial class SharpSqlCompiler
{
    private const int PrecedenceAdditive = 60;
    private const int PrecedenceMultiplicative = 70;
    private const int PrecedenceUnary = 80;
    private const int PrecedencePrimary = 100;

    private readonly List<CompilerDiagnostic> _diagnostics = [];
    private readonly Dictionary<string, MethodDefinition> _methods = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _callCounts = new(StringComparer.Ordinal);
    private readonly HashSet<string> _recursiveMethods = new(StringComparer.Ordinal);
    private readonly NameAllocator _names = new();
    private readonly SqlWriter _sql = new();
    private SemanticModel? _semanticModel;
    private TranspileOptions _options = new();
    private int _inlineId;
    private bool _used;

    public TranspileResult Transpile(string source, TranspileOptions? options = null)
    {
        if (_used)
            return new SharpSqlCompiler().Transpile(source, options);
        _used = true;

        _options = options ?? new TranspileOptions();
        var tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview));
        var root = tree.GetCompilationUnitRoot();
        _semanticModel = CreateSemanticModel(tree);

        AddParseDiagnostics(tree);
        CollectMethods(root);
        CountCalls(root);
        FindRecursion();
        PrepareVmMethods();
        PrepareHeapRuntime(root);

        EmitFileHeaderComments(root);

        if (_options.EmitNoCount)
        {
            _sql.Line("SET NOCOUNT ON;");
            _sql.Line();
        }

        EmitVmPreamble();
        EmitHeapPreamble();

        var scope = new VariableScope();
        var topLevelStatements = root.Members
            .OfType<GlobalStatementSyntax>()
            .Select(member => member.Statement)
            .Where(statement => statement is not LocalFunctionStatementSyntax)
            .ToArray();

        if (topLevelStatements.Length > 0)
        {
            EmitStatementSequence(topLevelStatements, scope, null, null, null);
        }
        else
        {
            var main = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
                .FirstOrDefault(method => method.Identifier.ValueText == "Main");
            if (main?.Body is not null)
                EmitStatementSequence(main.Body.Statements, scope, null, null, null);
            else if (main?.ExpressionBody is not null)
                EmitExpressionStatement(main.ExpressionBody.Expression, scope, null, null);
            else if (_diagnostics.Count == 0)
                AddDiagnostic("SS0001", "No top-level statements or Main method were found.", root);
        }

        EmitVmEpilogue();
        EmitAllRemainingComments(root);
        EmitHeapEpilogue();

        return new TranspileResult(_sql.ToString(), _diagnostics.AsReadOnly());
    }

    private void AddParseDiagnostics(SyntaxTree tree)
    {
        foreach (var diagnostic in tree.GetDiagnostics().Where(item => item.Severity == DiagnosticSeverity.Error))
        {
            var position = diagnostic.Location.GetLineSpan().StartLinePosition;
            _diagnostics.Add(new CompilerDiagnostic(
                "CS-PARSE",
                diagnostic.GetMessage(CultureInfo.InvariantCulture),
                position.Line + 1,
                position.Character + 1));
        }
    }

    private static SemanticModel CreateSemanticModel(SyntaxTree sourceTree)
    {
        var trustedAssemblies = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))?
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries) ?? [];
        var references = trustedAssemblies.Select(path => MetadataReference.CreateFromFile(path));
        var globalUsings = CSharpSyntaxTree.ParseText(
            "global using System; global using System.Collections.Generic; global using System.Linq;",
            new CSharpParseOptions(LanguageVersion.Preview));
        var compilation = CSharpCompilation.Create(
            "SharpSqlInput",
            [sourceTree, globalUsings],
            references,
            new CSharpCompilationOptions(OutputKind.ConsoleApplication));
        return compilation.GetSemanticModel(sourceTree, ignoreAccessibility: true);
    }

    private void CollectMethods(CompilationUnitSyntax root)
    {
        foreach (var local in root.DescendantNodes().OfType<LocalFunctionStatementSyntax>())
        {
            AddMethod(new MethodDefinition(
                local.Identifier.ValueText,
                CSharpType.From(local.ReturnType),
                local.ParameterList.Parameters.Select(ToParameter).ToArray(),
                local.Body,
                local.ExpressionBody?.Expression,
                local));
        }

        foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            if (method.Identifier.ValueText == "Main")
                continue;

            var containingType = method.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
            var isInstance = containingType is not null && !method.Modifiers.Any(SyntaxKind.StaticKeyword);
            var parameters = method.ParameterList.Parameters.Select(ToParameter).ToList();
            if (isInstance)
                parameters.Insert(0, new ParameterDefinition("this", new CSharpType(containingType!.Identifier.ValueText, "BIGINT", IsReference: true)));

            AddMethod(new MethodDefinition(
                method.Identifier.ValueText,
                CSharpType.From(method.ReturnType),
                parameters,
                method.Body,
                method.ExpressionBody?.Expression,
                method,
                containingType?.Identifier.ValueText,
                isInstance));
        }
    }

    private void AddMethod(MethodDefinition method)
    {
        if (!_methods.TryAdd(method.Name, method))
            AddDiagnostic("SS1001", $"Method overloads are not supported yet: '{method.Name}'.", method.Syntax);
    }

    private static ParameterDefinition ToParameter(ParameterSyntax parameter) =>
        new(parameter.Identifier.ValueText,
            parameter.Type is null ? CSharpType.Unknown : CSharpType.From(parameter.Type));

    private void CountCalls(CompilationUnitSyntax root)
    {
        foreach (var call in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var name = InvocationName(call.Expression);
            if (name is not null)
                _callCounts[name] = _callCounts.GetValueOrDefault(name) + 1;
        }
    }

    private void FindRecursion()
    {
        var graph = _methods.Values.ToDictionary(
            method => method.Name,
            method => method.Syntax.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Select(call => InvocationName(call.Expression))
                .Where(name => name is not null && _methods.ContainsKey(name))
                .Cast<string>()
                .ToHashSet(StringComparer.Ordinal),
            StringComparer.Ordinal);

        foreach (var method in _methods.Keys)
            Visit(method, method, []);

        void Visit(string origin, string current, HashSet<string> path)
        {
            if (!path.Add(current))
            {
                if (current == origin)
                    foreach (var item in path)
                        _recursiveMethods.Add(item);
                return;
            }

            foreach (var next in graph[current])
                Visit(origin, next, new HashSet<string>(path, StringComparer.Ordinal));
        }
    }

    private void EmitStatementSequence(
        IEnumerable<StatementSyntax> statements,
        VariableScope scope,
        InlineReturn? inlineReturn,
        LoopContext? loop,
        string? namePrefix)
    {
        foreach (var statement in statements)
        {
            if (statement is LocalFunctionStatementSyntax)
            {
                EmitLeadingComments(statement);
                EmitTrailingComments(statement);
                continue;
            }
            EmitStatement(statement, scope, inlineReturn, loop, namePrefix);
        }
    }

    private void EmitStatement(
        StatementSyntax statement,
        VariableScope scope,
        InlineReturn? inlineReturn,
        LoopContext? loop,
        string? namePrefix)
    {
        EmitLeadingComments(statement);
        switch (statement)
        {
            case BlockSyntax block:
                EmitStatementSequence(block.Statements, scope.Child(), inlineReturn, loop, namePrefix);
                break;
            case LocalDeclarationStatementSyntax declaration:
                EmitDeclaration(declaration.Declaration, scope, inlineReturn, loop, namePrefix);
                break;
            case ExpressionStatementSyntax expression:
                EmitExpressionStatement(expression.Expression, scope, inlineReturn, namePrefix);
                break;
            case IfStatementSyntax @if:
                EmitIf(@if, scope, inlineReturn, loop, namePrefix);
                break;
            case WhileStatementSyntax @while:
                EmitWhile(@while, scope, inlineReturn, namePrefix);
                break;
            case DoStatementSyntax @do:
                EmitDo(@do, scope, inlineReturn, namePrefix);
                break;
            case ForStatementSyntax @for:
                EmitFor(@for, scope, inlineReturn, namePrefix);
                break;
            case ForEachStatementSyntax forEach:
                EmitForEach(forEach, scope, inlineReturn, namePrefix);
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
            case ReturnStatementSyntax @return when inlineReturn is not null:
                if (@return.Expression is not null && inlineReturn.TargetSql is not null)
                    _sql.Line($"SET {inlineReturn.TargetSql} = {EmitScalar(@return.Expression, scope)};");
                _sql.Line($"GOTO {inlineReturn.EndLabel};");
                break;
            case ReturnStatementSyntax @return:
                if (@return.Expression is null)
                    _sql.Line("RETURN;");
                else
                    AddDiagnostic("SS2003", "A value cannot be returned from the script entry point.", @return);
                break;
            case EmptyStatementSyntax:
                break;
            default:
                Unsupported(statement, "statement");
                break;
        }
        EmitTrailingComments(statement);
    }

    private void EmitDeclaration(
        VariableDeclarationSyntax declaration,
        VariableScope scope,
        InlineReturn? inlineReturn,
        LoopContext? loop,
        string? namePrefix)
    {
        foreach (var variable in declaration.Variables)
        {
            var sourceName = variable.Identifier.ValueText;
            var declaredType = CSharpType.From(declaration.Type);
            var type = declaredType == CSharpType.Unknown && variable.Initializer is not null
                ? InferType(variable.Initializer.Value, scope)
                : declaredType;
            var sqlName = _names.Allocate(namePrefix is null ? sourceName : $"{namePrefix}_{sourceName}");

            if (variable.Initializer is not null && ContainsRuntimeExpression(variable.Initializer.Value))
            {
                _sql.Line($"DECLARE {sqlName} {type.Sql};");
                EmitVmExpression(
                    variable.Initializer.Value,
                    scope,
                    null,
                    value => _sql.Line($"SET {sqlName} = {value};"));
                scope.Add(sourceName, new VariableBinding(sqlName, type));
                continue;
            }

            if (variable.Initializer?.Value is InvocationExpressionSyntax invocation &&
                TryGetComplexMethod(invocation, out var method))
            {
                EmitComplexInline(method, InvocationArgumentExpressions(invocation, method), scope, sqlName, type, declareTarget: true);
                scope.Add(sourceName, new VariableBinding(sqlName, type));
                continue;
            }

            var initializer = variable.Initializer is null
                ? string.Empty
                : $" = {EmitScalar(variable.Initializer.Value, scope)}";
            _sql.Line($"DECLARE {sqlName} {type.Sql}{initializer};");
            scope.Add(sourceName, new VariableBinding(sqlName, type));
        }
    }

    private void EmitExpressionStatement(
        ExpressionSyntax expression,
        VariableScope scope,
        InlineReturn? inlineReturn,
        string? namePrefix)
    {
        if (TryEmitHeapStatement(expression, scope))
            return;

        if (expression is AssignmentExpressionSyntax vmAssignment && ContainsRuntimeExpression(vmAssignment.Right))
        {
            var target = EmitAssignable(vmAssignment.Left, scope);
            EmitVmExpression(
                vmAssignment.Right,
                scope,
                null,
                value => _sql.Line(VmAssignmentLine(
                    vmAssignment,
                    target,
                    InferType(vmAssignment.Left, scope),
                    value)));
            return;
        }

        if (expression is AssignmentExpressionSyntax
            {
                RawKind: (int)SyntaxKind.SimpleAssignmentExpression,
                Right: InvocationExpressionSyntax assignedCall
            } assignment && TryGetComplexMethod(assignedCall, out var assignedMethod))
        {
            EmitComplexInline(
                assignedMethod,
                InvocationArgumentExpressions(assignedCall, assignedMethod),
                scope,
                EmitAssignable(assignment.Left, scope),
                InferType(assignment.Left, scope),
                declareTarget: false);
            return;
        }

        if (expression is InvocationExpressionSyntax invocation)
        {
            if (IsConsoleWrite(invocation))
            {
                var value = invocation.ArgumentList.Arguments.FirstOrDefault()?.Expression;
                if (value is not null && ContainsRuntimeExpression(value))
                    EmitVmExpression(value, scope, null, EmitPrintSql);
                else
                    EmitPrintSql(value is null ? "N''" : EmitScalar(value, scope));
                return;
            }

            if (ContainsRuntimeExpression(invocation))
            {
                EmitVmExpression(invocation, scope, null, _ => { });
                return;
            }

            if (TryGetComplexMethod(invocation, out var method))
            {
                EmitComplexInline(method, InvocationArgumentExpressions(invocation, method), scope, null, method.ReturnType, declareTarget: false);
                return;
            }

            if (_methods.ContainsKey(InvocationName(invocation.Expression) ?? string.Empty))
                return; // A pure, side-effect-free result can be discarded.
        }

        foreach (var line in MutationLines(expression, scope))
            _sql.Line(line);
    }

    private void EmitIf(
        IfStatementSyntax statement,
        VariableScope scope,
        InlineReturn? inlineReturn,
        LoopContext? loop,
        string? namePrefix)
    {
        if (ContainsRuntimeExpression(statement.Condition))
            EmitVmExpression(
                statement.Condition,
                scope,
                null,
                condition => EmitBody(VmPredicate(condition, statement.Condition, scope)));
        else
            EmitBody(EmitPredicate(statement.Condition, scope));

        void EmitBody(string condition)
        {
            _sql.Line($"IF {condition}");
            EmitEmbedded(statement.Statement, scope.Child(), inlineReturn, loop, namePrefix);
            if (statement.Else is not null)
            {
                _sql.Line("ELSE");
                EmitEmbedded(statement.Else.Statement, scope.Child(), inlineReturn, loop, namePrefix);
            }
        }
    }

    private void EmitWhile(
        WhileStatementSyntax statement,
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
                null,
                condition => EmitBody(VmPredicate(condition, statement.Condition, scope)));
        else
            EmitBody(EmitPredicate(statement.Condition, scope));

        void EmitBody(string condition)
        {
            _sql.Line($"IF NOT ({condition}) GOTO {breakLabel};");
            EmitEmbeddedContents(
                statement.Statement,
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
        DoStatementSyntax statement,
        VariableScope scope,
        InlineReturn? inlineReturn,
        string? namePrefix)
    {
        var bodyLabel = _names.AllocateLabel("do_body");
        var continueLabel = _names.AllocateLabel("do_continue");
        var breakLabel = _names.AllocateLabel("do_break");
        EmitLabel(bodyLabel);
        EmitEmbeddedContents(
            statement.Statement,
            scope.Child(),
            inlineReturn,
            new LoopContext(breakLabel, continueLabel),
            namePrefix);
        EmitLabel(continueLabel);
        if (ContainsRuntimeExpression(statement.Condition))
            EmitVmExpression(
                statement.Condition,
                scope,
                null,
                condition => EmitCondition(VmPredicate(condition, statement.Condition, scope)));
        else
            EmitCondition(EmitPredicate(statement.Condition, scope));

        void EmitCondition(string condition)
        {
            _sql.Line($"IF {condition} GOTO {bodyLabel};");
            EmitLabel(breakLabel);
        }
    }

    private void EmitFor(
        ForStatementSyntax statement,
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
                null,
                condition => EmitBody(VmPredicate(condition, statement.Condition, scope)));
        else
            EmitBody(statement.Condition is null ? "1 = 1" : EmitPredicate(statement.Condition, scope));

        void EmitBody(string condition)
        {
            _sql.Line($"IF NOT ({condition}) GOTO {breakLabel};");
            EmitEmbeddedContents(
                statement.Statement,
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
        StatementSyntax statement,
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
        ForEachStatementSyntax statement,
        VariableScope parentScope,
        InlineReturn? inlineReturn,
        string? namePrefix)
    {
        var collectionType = InferType(statement.Expression, parentScope);
        if (!IsSequenceType(collectionType.Name))
        {
            AddDiagnostic("SS6302", "foreach currently supports arrays and List<T>.", statement.Expression);
            return;
        }

        EmitVmExpression(statement.Expression, parentScope, null, collectionValue =>
        {
            var scope = parentScope.Child();
            var collectionSql = _names.Allocate("_foreach_collection");
            var indexSql = _names.Allocate("_foreach_index");
            var itemType = statement.Type.IsVar
                ? SequenceElementType(collectionType.Name)
                : CSharpType.From(statement.Type);
            var itemSql = _names.Allocate(statement.Identifier.ValueText);
            var conditionLabel = _names.AllocateLabel("foreach_condition");
            var continueLabel = _names.AllocateLabel("foreach_continue");
            var breakLabel = _names.AllocateLabel("foreach_break");

            _sql.Line($"DECLARE {collectionSql} BIGINT = {collectionValue};");
            _sql.Line($"DECLARE {indexSql} INT = 0;");
            _sql.Line($"DECLARE {itemSql} {itemType.Sql};");
            scope.Add(statement.Identifier.ValueText, new VariableBinding(itemSql, itemType));
            EmitLabel(conditionLabel);
            _sql.Line($"IF {indexSql} >= {SequenceCountSql(collectionSql)} GOTO {breakLabel};");
            _sql.Line($"SET {itemSql} = {SequenceElementSql(collectionSql, indexSql, itemType)};");
            EmitEmbeddedContents(
                statement.Statement,
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
        StatementSyntax statement,
        VariableScope scope,
        InlineReturn? inlineReturn,
        LoopContext? loop,
        string? namePrefix)
    {
        if (statement is BlockSyntax block)
            EmitStatementSequence(block.Statements, scope, inlineReturn, loop, namePrefix);
        else
            EmitStatement(statement, scope, inlineReturn, loop, namePrefix);
    }

    private IEnumerable<string> MutationLines(ExpressionSyntax expression, VariableScope scope)
    {
        switch (expression)
        {
            case AssignmentExpressionSyntax assignment:
                {
                    var target = EmitAssignable(assignment.Left, scope);
                    if (assignment.Right is InvocationExpressionSyntax invocation &&
                        TryGetComplexMethod(invocation, out _))
                    {
                        AddDiagnostic("SS2004", "Complex inline calls on assignment are not supported yet; initialize a new variable instead.", assignment);
                        return [];
                    }

                    var value = EmitScalar(assignment.Right, scope);
                    if (assignment.Kind() == SyntaxKind.SimpleAssignmentExpression)
                        return [$"SET {target} = {value};"];

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
                        return [];
                    }

                    var targetType = InferType(assignment.Left, scope);
                    return targetType.IsString && op == "+"
                        ? [$"SET {target} = CONCAT({target}, {value});"]
                        : [$"SET {target} = {target} {op} {value};"];
                }
            case PostfixUnaryExpressionSyntax postfix when
                postfix.Kind() is SyntaxKind.PostIncrementExpression or SyntaxKind.PostDecrementExpression:
                {
                    var target = EmitAssignable(postfix.Operand, scope);
                    var op = postfix.Kind() == SyntaxKind.PostIncrementExpression ? "+" : "-";
                    return [$"SET {target} = {target} {op} 1;"];
                }
            case PrefixUnaryExpressionSyntax prefix when
                prefix.Kind() is SyntaxKind.PreIncrementExpression or SyntaxKind.PreDecrementExpression:
                {
                    var target = EmitAssignable(prefix.Operand, scope);
                    var op = prefix.Kind() == SyntaxKind.PreIncrementExpression ? "+" : "-";
                    return [$"SET {target} = {target} {op} 1;"];
                }
            default:
                Unsupported(expression, "expression statement");
                return [];
        }
    }

    private void EmitComplexInline(
        MethodDefinition method,
        IReadOnlyList<ExpressionSyntax> arguments,
        VariableScope callerScope,
        string? targetSql,
        CSharpType targetType,
        bool declareTarget)
    {
        if (!CanInline(method, arguments.Count))
            return;

        EmitLeadingComments(method.Syntax);

        var id = ++_inlineId;
        var prefix = $"_{method.Name.ToLowerInvariant()}_{id}";
        var methodScope = callerScope.Child();

        for (var index = 0; index < method.Parameters.Count; index++)
        {
            var parameter = method.Parameters[index];
            var parameterSql = _names.Allocate($"{prefix}_{parameter.Name}");
            var argumentSql = EmitScalar(arguments[index], callerScope);
            _sql.Line($"DECLARE {parameterSql} {parameter.Type.Sql} = {argumentSql};");
            methodScope.Add(parameter.Name, new VariableBinding(parameterSql, parameter.Type));
        }

        if (targetSql is not null && declareTarget)
            _sql.Line($"DECLARE {targetSql} {targetType.Sql};");

        var endLabel = _names.AllocateLabel($"{prefix}_end");
        var inlineReturn = new InlineReturn(targetSql, endLabel);

        if (method.Body is not null)
            EmitStatementSequence(method.Body.Statements, methodScope, inlineReturn, null, prefix);
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
            AddDiagnostic("SS3001", $"Method '{method.Name}' expects {method.Parameters.Count} arguments, but received {argumentCount}.", method.Syntax);
            return false;
        }

        if (_recursiveMethods.Contains(method.Name))
        {
            AddDiagnostic("SS3002", $"Recursive method '{method.Name}' needs the planned temporary-procedure fallback.", method.Syntax);
            return false;
        }

        if (method.StatementCount > _options.MaxInlineStatements ||
            _callCounts.GetValueOrDefault(method.Name) > _options.MaxInlineCallSites)
        {
            AddDiagnostic("SS3003", $"Method '{method.Name}' exceeds the configured inlining budget.", method.Syntax);
            return false;
        }

        return true;
    }

    private bool TryGetComplexMethod(InvocationExpressionSyntax invocation, out MethodDefinition method)
    {
        var name = InvocationName(invocation.Expression);
        if (name is not null && _methods.TryGetValue(name, out method!) && method.PureExpression is null)
            return true;
        method = null!;
        return false;
    }

    private string EmitScalar(
        ExpressionSyntax expression,
        VariableScope scope,
        IReadOnlyDictionary<string, Substitution>? substitutions = null) =>
        EmitScalarExpression(expression, scope, substitutions).Text;

    private EmittedExpression EmitScalarExpression(
        ExpressionSyntax expression,
        VariableScope scope,
        IReadOnlyDictionary<string, Substitution>? substitutions = null)
    {
        EmitExpressionComments(expression);
        expression = StripParentheses(expression);
        var type = InferType(expression, scope, substitutions);
        if (type.IsBoolean && IsPredicateShape(expression))
            return EmittedExpression.Primary(
                $"CASE WHEN {EmitPredicate(expression, scope, substitutions)} THEN CAST(1 AS BIT) ELSE CAST(0 AS BIT) END");

        return expression switch
        {
            LiteralExpressionSyntax literal => EmittedExpression.Primary(EmitLiteral(literal)),
            IdentifierNameSyntax identifier => EmitIdentifierExpression(identifier, scope, substitutions),
            ThisExpressionSyntax => EmitThisExpression(scope, substitutions),
            BinaryExpressionSyntax binary => EmitBinaryScalar(binary, scope, substitutions),
            PrefixUnaryExpressionSyntax prefix => EmitPrefixScalar(prefix, scope, substitutions),
            CastExpressionSyntax cast => EmittedExpression.Primary(
                $"CAST({EmitScalar(cast.Expression, scope, substitutions)} AS {CSharpType.From(cast.Type).Sql})"),
            ConditionalExpressionSyntax conditional => EmittedExpression.Primary(
                $"CASE WHEN {EmitPredicate(conditional.Condition, scope, substitutions)} THEN {EmitScalar(conditional.WhenTrue, scope, substitutions)} ELSE {EmitScalar(conditional.WhenFalse, scope, substitutions)} END"),
            InvocationExpressionSyntax invocation => EmitInvocation(invocation, scope, substitutions),
            MemberAccessExpressionSyntax member => EmitHeapMemberScalar(member, scope),
            ElementAccessExpressionSyntax element => EmitHeapElementScalar(element, scope),
            InterpolatedStringExpressionSyntax interpolated => EmitInterpolatedString(interpolated, scope, substitutions),
            CheckedExpressionSyntax checkedExpression => EmitScalarExpression(checkedExpression.Expression, scope, substitutions),
            _ => EmittedExpression.Primary(UnsupportedExpression(expression))
        };
    }

    private string EmitPredicate(
        ExpressionSyntax expression,
        VariableScope scope,
        IReadOnlyDictionary<string, Substitution>? substitutions = null)
    {
        expression = StripParentheses(expression);
        switch (expression)
        {
            case LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.TrueLiteralExpression):
                return "1 = 1";
            case LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.FalseLiteralExpression):
                return "1 = 0";
            case IdentifierNameSyntax identifier:
                return $"{EmitIdentifier(identifier, scope, substitutions)} = 1";
            case PrefixUnaryExpressionSyntax prefix when prefix.IsKind(SyntaxKind.LogicalNotExpression):
                return $"NOT ({EmitPredicate(prefix.Operand, scope, substitutions)})";
            case BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.LogicalAndExpression):
                return $"({EmitPredicate(binary.Left, scope, substitutions)}) AND ({EmitPredicate(binary.Right, scope, substitutions)})";
            case BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.LogicalOrExpression):
                return $"({EmitPredicate(binary.Left, scope, substitutions)}) OR ({EmitPredicate(binary.Right, scope, substitutions)})";
            case BinaryExpressionSyntax binary when
                binary.Kind() is SyntaxKind.EqualsExpression or SyntaxKind.NotEqualsExpression &&
                (binary.Left.IsKind(SyntaxKind.NullLiteralExpression) || binary.Right.IsKind(SyntaxKind.NullLiteralExpression)):
                {
                    var operand = binary.Left.IsKind(SyntaxKind.NullLiteralExpression) ? binary.Right : binary.Left;
                    var op = binary.IsKind(SyntaxKind.EqualsExpression) ? "IS NULL" : "IS NOT NULL";
                    return $"{EmitScalar(operand, scope, substitutions)} {op}";
                }
            case BinaryExpressionSyntax binary when IsComparison(binary.Kind()):
                return $"{EmitScalar(binary.Left, scope, substitutions)} {SqlOperator(binary.Kind())} {EmitScalar(binary.Right, scope, substitutions)}";
            default:
                return $"{EmitScalar(expression, scope, substitutions)} = 1";
        }
    }

    private EmittedExpression EmitBinaryScalar(
        BinaryExpressionSyntax binary,
        VariableScope scope,
        IReadOnlyDictionary<string, Substitution>? substitutions)
    {
        if (binary.IsKind(SyntaxKind.CoalesceExpression))
            return EmittedExpression.Primary(
                $"COALESCE({EmitScalar(binary.Left, scope, substitutions)}, {EmitScalar(binary.Right, scope, substitutions)})");

        if (binary.IsKind(SyntaxKind.AddExpression) &&
            (InferType(binary.Left, scope, substitutions).IsString || InferType(binary.Right, scope, substitutions).IsString))
            return EmittedExpression.Primary(
                $"CONCAT({EmitScalar(binary.Left, scope, substitutions)}, {EmitScalar(binary.Right, scope, substitutions)})");

        var op = SqlOperator(binary.Kind());
        if (op.Length == 0)
            return EmittedExpression.Primary(UnsupportedExpression(binary));

        var precedence = BinaryPrecedence(binary.Kind());
        var left = EmitScalarExpression(binary.Left, scope, substitutions).Render(precedence);
        var right = EmitScalarExpression(binary.Right, scope, substitutions).Render(precedence + 1);
        return new EmittedExpression($"{left} {op} {right}", precedence);
    }

    private EmittedExpression EmitPrefixScalar(
        PrefixUnaryExpressionSyntax prefix,
        VariableScope scope,
        IReadOnlyDictionary<string, Substitution>? substitutions) => prefix.Kind() switch
        {
            SyntaxKind.UnaryMinusExpression => new EmittedExpression(
                $"-{EmitScalarExpression(prefix.Operand, scope, substitutions).Render(PrecedenceUnary + 1)}",
                PrecedenceUnary),
            SyntaxKind.UnaryPlusExpression => new EmittedExpression(
                $"+{EmitScalarExpression(prefix.Operand, scope, substitutions).Render(PrecedenceUnary + 1)}",
                PrecedenceUnary),
            SyntaxKind.BitwiseNotExpression => new EmittedExpression(
                $"~{EmitScalarExpression(prefix.Operand, scope, substitutions).Render(PrecedenceUnary + 1)}",
                PrecedenceUnary),
            SyntaxKind.LogicalNotExpression => EmittedExpression.Primary(
                $"CASE WHEN {EmitPredicate(prefix.Operand, scope, substitutions)} THEN CAST(0 AS BIT) ELSE CAST(1 AS BIT) END"),
            _ => EmittedExpression.Primary(UnsupportedExpression(prefix))
        };

    private EmittedExpression EmitInvocation(
        InvocationExpressionSyntax invocation,
        VariableScope scope,
        IReadOnlyDictionary<string, Substitution>? substitutions)
    {
        if (substitutions is null && TryEmitHeapInvocationScalar(invocation, scope, out var heapExpression))
            return heapExpression;

        var name = InvocationName(invocation.Expression);
        if (name is null || !_methods.TryGetValue(name, out var method))
            return EmittedExpression.Primary(
                UnsupportedExpression(invocation, "Only user-defined methods and Console.WriteLine are supported."));
        if (method.PureExpression is null)
            return EmittedExpression.Primary(
                UnsupportedExpression(invocation, "A branching method call must be the complete variable initializer."));
        EmitLeadingComments(method.Syntax);
        if (method.Body?.Statements.OfType<ReturnStatementSyntax>().FirstOrDefault() is { } returnStatement)
            EmitLeadingComments(returnStatement);
        var arguments = InvocationArgumentExpressions(invocation, method);
        if (!CanInline(method, arguments.Count))
            return EmittedExpression.Primary("NULL");

        var replacements = new Dictionary<string, Substitution>(StringComparer.Ordinal);
        for (var index = 0; index < method.Parameters.Count; index++)
        {
            var parameter = method.Parameters[index];
            var argument = arguments[index];
            replacements[parameter.Name] = new Substitution(
                EmitScalarExpression(argument, scope, substitutions),
                InferType(argument, scope, substitutions));
        }

        return EmitScalarExpression(method.PureExpression, scope, replacements);
    }

    private static IReadOnlyList<ExpressionSyntax> InvocationArgumentExpressions(
        InvocationExpressionSyntax invocation,
        MethodDefinition method)
    {
        var arguments = new List<ExpressionSyntax>();
        if (method.IsInstance)
        {
            arguments.Add(invocation.Expression is MemberAccessExpressionSyntax member
                ? member.Expression
                : SyntaxFactory.ThisExpression());
        }
        arguments.AddRange(invocation.ArgumentList.Arguments.Select(argument => argument.Expression));
        return arguments;
    }

    private EmittedExpression EmitInterpolatedString(
        InterpolatedStringExpressionSyntax interpolated,
        VariableScope scope,
        IReadOnlyDictionary<string, Substitution>? substitutions)
    {
        var parts = interpolated.Contents.Select(content => content switch
        {
            InterpolatedStringTextSyntax text => "N'" + EscapeSqlString(text.TextToken.ValueText) + "'",
            InterpolationSyntax interpolation => EmitInterpolation(interpolation, scope, substitutions),
            _ => "N''"
        }).ToArray();
        return parts.Length switch
        {
            0 => EmittedExpression.Primary("N''"),
            1 when interpolated.Contents[0] is InterpolatedStringTextSyntax => EmittedExpression.Primary(parts[0]),
            1 => EmittedExpression.Primary($"CONCAT(N'', {parts[0]})"),
            _ => EmittedExpression.Primary($"CONCAT({string.Join(", ", parts)})")
        };
    }

    private string EmitInterpolation(
        InterpolationSyntax interpolation,
        VariableScope scope,
        IReadOnlyDictionary<string, Substitution>? substitutions)
    {
        var value = EmitScalar(interpolation.Expression, scope, substitutions);
        if (!InferType(interpolation.Expression, scope, substitutions).IsBoolean)
            return value;

        return $"CASE {value} " +
            "WHEN CAST(1 AS BIT) THEN N'True' " +
            "WHEN CAST(0 AS BIT) THEN N'False' ELSE N'' END";
    }

    private string EmitAssignable(ExpressionSyntax expression, VariableScope scope) => expression switch
    {
        IdentifierNameSyntax identifier when scope.Find(identifier.Identifier.ValueText) is { } binding => binding.SqlName,
        _ => UnsupportedExpression(expression, "Only local variables can be assigned.")
    };

    private string EmitIdentifier(
        IdentifierNameSyntax identifier,
        VariableScope scope,
        IReadOnlyDictionary<string, Substitution>? substitutions) =>
        EmitIdentifierExpression(identifier, scope, substitutions).Text;

    private EmittedExpression EmitIdentifierExpression(
        IdentifierNameSyntax identifier,
        VariableScope scope,
        IReadOnlyDictionary<string, Substitution>? substitutions)
    {
        var name = identifier.Identifier.ValueText;
        if (substitutions is not null && substitutions.TryGetValue(name, out var replacement))
            return replacement.Expression;
        if (scope.Find(name) is { } binding)
            return EmittedExpression.Primary(binding.SqlName);
        if (TryEmitImplicitHeapField(identifier, scope, substitutions, out var heapField))
            return heapField;
        AddDiagnostic("SS4001", $"Unknown identifier '{name}'.", identifier);
        return EmittedExpression.Primary("NULL");
    }

    private static EmittedExpression EmitThisExpression(
        VariableScope scope,
        IReadOnlyDictionary<string, Substitution>? substitutions)
    {
        if (substitutions is not null && substitutions.TryGetValue("this", out var replacement))
            return replacement.Expression;
        return scope.Find("this") is { } binding
            ? EmittedExpression.Primary(binding.SqlName)
            : EmittedExpression.Primary("NULL");
    }

    private CSharpType InferType(
        ExpressionSyntax expression,
        VariableScope scope,
        IReadOnlyDictionary<string, Substitution>? substitutions = null)
    {
        expression = StripParentheses(expression);
        var semanticType = _semanticModel is not null && expression.SyntaxTree == _semanticModel.SyntaxTree
            ? _semanticModel.GetTypeInfo(expression).Type
            : null;
        if (semanticType is not null && semanticType.TypeKind != TypeKind.Error)
            return CSharpType.From(semanticType);

        return expression switch
        {
            LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.StringLiteralExpression) => CSharpType.String,
            LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.CharacterLiteralExpression) => new("char", "NCHAR(1)"),
            LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.TrueLiteralExpression) || literal.IsKind(SyntaxKind.FalseLiteralExpression) => CSharpType.Bool,
            LiteralExpressionSyntax literal when literal.Token.Value is decimal => new("decimal", "DECIMAL(38,18)"),
            LiteralExpressionSyntax literal when literal.Token.Value is double => new("double", "FLOAT"),
            LiteralExpressionSyntax literal when literal.Token.Value is float => new("float", "REAL"),
            LiteralExpressionSyntax literal when literal.Token.Value is long => new("long", "BIGINT"),
            LiteralExpressionSyntax => CSharpType.Int,
            IdentifierNameSyntax identifier when substitutions is not null && substitutions.TryGetValue(identifier.Identifier.ValueText, out var value) => value.Type,
            IdentifierNameSyntax identifier => scope.Find(identifier.Identifier.ValueText)?.Type ?? CSharpType.Unknown,
            ThisExpressionSyntax => scope.Find("this")?.Type ?? CSharpType.Unknown,
            BinaryExpressionSyntax binary when IsPredicateShape(binary) => CSharpType.Bool,
            BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.AddExpression) &&
                (InferType(binary.Left, scope, substitutions).IsString || InferType(binary.Right, scope, substitutions).IsString) => CSharpType.String,
            BinaryExpressionSyntax binary => InferType(binary.Left, scope, substitutions),
            PrefixUnaryExpressionSyntax prefix when prefix.IsKind(SyntaxKind.LogicalNotExpression) => CSharpType.Bool,
            PrefixUnaryExpressionSyntax prefix => InferType(prefix.Operand, scope, substitutions),
            CastExpressionSyntax cast => CSharpType.From(cast.Type),
            ConditionalExpressionSyntax conditional => InferType(conditional.WhenTrue, scope, substitutions),
            InterpolatedStringExpressionSyntax => CSharpType.String,
            ObjectCreationExpressionSyntax creation => CSharpType.From(creation.Type),
            ArrayCreationExpressionSyntax creation => CSharpType.From(creation.Type),
            MemberAccessExpressionSyntax member => InferHeapMemberType(member, scope),
            ElementAccessExpressionSyntax element => InferHeapElementType(element, scope),
            InvocationExpressionSyntax invocation when invocation.Expression is MemberAccessExpressionSyntax member &&
                ((IsDictionaryType(InferType(member.Expression, scope, substitutions).Name) &&
                  member.Name.Identifier.ValueText is "ContainsKey" or "ContainsValue") ||
                 (IsListType(InferType(member.Expression, scope, substitutions).Name) &&
                  member.Name.Identifier.ValueText == "Contains")) => CSharpType.Bool,
            InvocationExpressionSyntax invocation when _methods.TryGetValue(InvocationName(invocation.Expression) ?? string.Empty, out var method) => method.ReturnType,
            _ => CSharpType.Unknown
        };
    }

    private string UnsupportedExpression(SyntaxNode node, string? detail = null)
    {
        AddDiagnostic("SS4002", detail ?? $"Unsupported expression: {node.Kind()}.", node);
        return "NULL";
    }

    private void Unsupported(SyntaxNode node, string category) =>
        AddDiagnostic("SS4003", $"Unsupported {category}: {node.Kind()}.", node);

    private void AddDiagnostic(string code, string message, SyntaxNode node)
    {
        var location = node.GetLocation().GetLineSpan().StartLinePosition;
        var diagnostic = new CompilerDiagnostic(code, message, location.Line + 1, location.Character + 1);
        if (!_diagnostics.Contains(diagnostic))
            _diagnostics.Add(diagnostic);
    }

    private static bool IsConsoleWrite(InvocationExpressionSyntax invocation) =>
        invocation.Expression is MemberAccessExpressionSyntax
        {
            Expression: IdentifierNameSyntax { Identifier.ValueText: "Console" },
            Name.Identifier.ValueText: "WriteLine" or "Write"
        };

    private static string? InvocationName(ExpressionSyntax expression) => expression switch
    {
        IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
        GenericNameSyntax generic => generic.Identifier.ValueText,
        MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
        _ => null
    };

    private static ExpressionSyntax StripParentheses(ExpressionSyntax expression)
    {
        while (expression is ParenthesizedExpressionSyntax parenthesized)
            expression = parenthesized.Expression;
        return expression;
    }

    private void EmitLabel(string label) => _sql.Line($"{label}:;");

    private static bool IsPredicateShape(ExpressionSyntax expression) => expression switch
    {
        BinaryExpressionSyntax binary => IsComparison(binary.Kind()) ||
            binary.IsKind(SyntaxKind.LogicalAndExpression) ||
            binary.IsKind(SyntaxKind.LogicalOrExpression),
        PrefixUnaryExpressionSyntax prefix => prefix.IsKind(SyntaxKind.LogicalNotExpression),
        _ => false
    };

    private static bool IsComparison(SyntaxKind kind) => kind is
        SyntaxKind.EqualsExpression or SyntaxKind.NotEqualsExpression or
        SyntaxKind.LessThanExpression or SyntaxKind.LessThanOrEqualExpression or
        SyntaxKind.GreaterThanExpression or SyntaxKind.GreaterThanOrEqualExpression;

    private static string SqlOperator(SyntaxKind kind) => kind switch
    {
        SyntaxKind.AddExpression => "+",
        SyntaxKind.SubtractExpression => "-",
        SyntaxKind.MultiplyExpression => "*",
        SyntaxKind.DivideExpression => "/",
        SyntaxKind.ModuloExpression => "%",
        SyntaxKind.BitwiseAndExpression => "&",
        SyntaxKind.BitwiseOrExpression => "|",
        SyntaxKind.ExclusiveOrExpression => "^",
        SyntaxKind.EqualsExpression => "=",
        SyntaxKind.NotEqualsExpression => "<>",
        SyntaxKind.LessThanExpression => "<",
        SyntaxKind.LessThanOrEqualExpression => "<=",
        SyntaxKind.GreaterThanExpression => ">",
        SyntaxKind.GreaterThanOrEqualExpression => ">=",
        _ => ""
    };

    private static int BinaryPrecedence(SyntaxKind kind) => kind switch
    {
        SyntaxKind.MultiplyExpression or SyntaxKind.DivideExpression or SyntaxKind.ModuloExpression =>
            PrecedenceMultiplicative,
        _ => PrecedenceAdditive
    };

    private static string EmitLiteral(LiteralExpressionSyntax literal)
    {
        if (literal.IsKind(SyntaxKind.StringLiteralExpression) || literal.IsKind(SyntaxKind.CharacterLiteralExpression))
            return "N'" + EscapeSqlString(literal.Token.ValueText) + "'";
        if (literal.IsKind(SyntaxKind.TrueLiteralExpression))
            return "CAST(1 AS BIT)";
        if (literal.IsKind(SyntaxKind.FalseLiteralExpression))
            return "CAST(0 AS BIT)";
        if (literal.IsKind(SyntaxKind.NullLiteralExpression))
            return "NULL";

        return literal.Token.Value switch
        {
            float value => $"CAST({value.ToString("R", CultureInfo.InvariantCulture)} AS REAL)",
            double value => $"CAST({value.ToString("R", CultureInfo.InvariantCulture)} AS FLOAT)",
            decimal value => $"CAST({value.ToString(CultureInfo.InvariantCulture)} AS DECIMAL(38,18))",
            IFormattable value => value.ToString(null, CultureInfo.InvariantCulture),
            _ => literal.Token.ValueText
        };
    }

    private static string EscapeSqlString(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    private sealed record InlineReturn(string? TargetSql, string EndLabel);
    private sealed record LoopContext(string BreakLabel, string ContinueLabel);
    private sealed record Substitution(EmittedExpression Expression, CSharpType Type);

    private sealed record EmittedExpression(string Text, int Precedence)
    {
        public static EmittedExpression Primary(string text) => new(text, PrecedencePrimary);

        public string Render(int requiredPrecedence) =>
            Precedence < requiredPrecedence ? $"({Text})" : Text;
    }
}
