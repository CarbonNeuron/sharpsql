using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SharpSql;

public sealed partial class SharpSqlCompiler
{
    private static readonly Lazy<MetadataReference[]> DefaultReferences = new(CreateDefaultReferences);

    private static readonly HashSet<string> DefaultReferenceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft.CSharp.dll",
        "netstandard.dll",
        "System.Collections.dll",
        "System.Console.dll",
        "System.Linq.dll",
        "System.Linq.Expressions.dll",
        "System.Linq.Queryable.dll",
        "System.ObjectModel.dll",
        "System.Private.CoreLib.dll",
        "System.Runtime.dll",
        "System.Runtime.Extensions.dll"
    };

    private static readonly HashSet<string> DataFlowDiagnosticIds =
        ["CS0161", "CS0165", "CS0177", "CS0841"];

    private const int PrecedenceAdditive = 60;
    private const int PrecedenceMultiplicative = 70;
    private const int PrecedenceUnary = 80;

    private readonly List<CompilerDiagnostic> _diagnostics = [];
    private readonly MethodCatalog _methods = new();
    private MethodGraph? _methodGraph;
    private readonly NameAllocator _names = new();
    private SqlWriter _sql = new();
    private readonly Dictionary<SyntaxTree, SemanticModel> _semanticModels = [];
    private CSharpCompilation? _compilation;
    private Diagnostic[]? _semanticDiagnostics;
    private string? _selectedEntryPointIdentity;
    private TranspileOptions _options = new();
    private IrProgram? _boundProgram;
    private VmMethod? _proceduralVmContext;
    private int _inlineId;
    private bool _used;

    internal IrProgram? BoundProgram => _boundProgram;

    public TranspileResult Transpile(string source, TranspileOptions? options = null)
    {
        if (_used)
            return new SharpSqlCompiler().Transpile(source, options);
        _used = true;

        var tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview));
        return TranspileCompilation(CreateCompilation(tree), entryPoint: null, options, compileReachableOnly: false);
    }

    public TranspileResult Transpile(
        CSharpCompilation compilation,
        string? entryPoint = null,
        TranspileOptions? options = null)
    {
        if (compilation is null)
            throw new ArgumentNullException(nameof(compilation));
        if (_used)
            return new SharpSqlCompiler().Transpile(compilation, entryPoint, options);
        _used = true;
        return TranspileCompilation(compilation, entryPoint, options, compileReachableOnly: true);
    }

    private TranspileResult TranspileCompilation(
        CSharpCompilation compilation,
        string? entryPoint,
        TranspileOptions? options,
        bool compileReachableOnly)
    {
        _options = options ?? new TranspileOptions();
        _compilation = compilation;
        var roots = compilation.SyntaxTrees
            .Select(tree => tree.GetCompilationUnitRoot())
            .ToArray();

        if (roots.Length == 0)
        {
            _diagnostics.Add(new CompilerDiagnostic("SS0001", "The compilation contains no C# source files.", 0, 0));
            return new TranspileResult(string.Empty, _diagnostics.AsReadOnly());
        }

        foreach (var root in roots)
        {
            AddParseDiagnostics(root.SyntaxTree);
            AddSemanticDiagnostics(root.SyntaxTree);
        }

        var selectedEntryPoint = SelectEntryPoint(compilation, roots, entryPoint);
        _selectedEntryPointIdentity = selectedEntryPoint is null
            ? "<top-level>"
            : $"{selectedEntryPoint.SyntaxTree.FilePath}|{MethodIdentity(SemanticModelFor(selectedEntryPoint)?.GetDeclaredSymbol(selectedEntryPoint) as IMethodSymbol).Value}";
        var topLevelStatements = roots
            .SelectMany(root => root.Members)
            .OfType<GlobalStatementSyntax>()
            .Select(member => member.Statement)
            .Where(statement => statement is not LocalFunctionStatementSyntax)
            .ToArray();
        var reachableMethods = compileReachableOnly
            ? FindReachableMethods(selectedEntryPoint, topLevelStatements)
            : roots.SelectMany(root => root.DescendantNodes().Where(node =>
                    node is MethodDeclarationSyntax or LocalFunctionStatementSyntax))
                .ToHashSet(ReferenceComparer<SyntaxNode>.Instance);
        var compilationSources = topLevelStatements.Cast<SyntaxNode>().Concat(reachableMethods).ToArray();
        foreach (var root in roots)
            CollectMethods(root, selectedEntryPoint, reachableMethods);
        AnalyzeMethodBehaviors();
        var heapTypeDefinitions = BindHeapTypeDefinitions(
            roots,
            compileReachableOnly ? compilationSources : null);

        var scope = new VariableScope();
        var relevantTrees = topLevelStatements.Select(statement => statement.SyntaxTree)
            .Concat(reachableMethods.Select(method => method.SyntaxTree))
            .ToHashSet();
        var commentRoots = roots
            .Where(root => relevantTrees.Contains(root.SyntaxTree) && !IsGeneratedSource(root))
            .ToArray();
        ProceduralBlock entryBlock;
        if (selectedEntryPoint is not null)
        {
            entryBlock = selectedEntryPoint.Body is not null
                ? (ProceduralBlock)BindProceduralStatement(selectedEntryPoint.Body, scope)
                : selectedEntryPoint.ExpressionBody is not null
                    ? new ProceduralBlock(
                        ToIrSource(selectedEntryPoint),
                        [new ProceduralExpressionStatement(
                            ToIrSource(selectedEntryPoint.ExpressionBody.Expression),
                            BindIrExpression(selectedEntryPoint.ExpressionBody.Expression, scope))])
                    : new ProceduralBlock(ToIrSource(selectedEntryPoint), []);
        }
        else if (topLevelStatements.Length > 0)
        {
            entryBlock = new ProceduralBlock(
                ToIrSource(topLevelStatements[0].SyntaxTree.GetCompilationUnitRoot()),
                topLevelStatements.Select(statement => BindProceduralStatement(statement, scope)).ToArray());
        }
        else
        {
            entryBlock = new ProceduralBlock(ToIrSource(roots[0]), []);
            if (entryBlock.Statements.Count == 0 && _diagnostics.Count == 0)
                AddDiagnostic("SS0001", "No top-level statements or selected entry method were found.", roots[0]);
        }
        _boundProgram = new IrProgram(
            _methods.Values.ToArray(),
            entryBlock,
            (compileReachableOnly ? compilationSources : commentRoots.Cast<SyntaxNode>())
                .Where(source => !IsGeneratedSource(source.SyntaxTree.GetCompilationUnitRoot()))
                .SelectMany(source => ToIrSource(source).DescendantComments)
                .ToArray())
        {
            HeapTypes = heapTypeDefinitions
        };
        PrepareHeapRuntime(_boundProgram);
        _methodGraph = MethodGraph.Create(_boundProgram.Methods, _boundProgram.EntryPoint);
        PrepareVmMethods();

        foreach (var root in commentRoots)
            EmitFileHeaderComments(root);

        if (_options.EmitNoCount)
        {
            _sql.Line("SET NOCOUNT ON;");
            _sql.Line();
        }

        EmitDurableRuntimePreamble();
        EmitVmPreamble();
        EmitHeapPreamble();
        EmitDurableRuntimeProvisioningEpilogue();
        EmitDurableExecutionBodyPreamble();

        foreach (var vmMethod in _vmMethods.Values)
            vmMethod.Scope.SetParent(scope);
        if (!TryEmitServiceBrokerProgram(_boundProgram))
            EmitProceduralStatementSequence(_boundProgram.EntryPoint.Statements, scope, null, null, null);

        EmitVmEpilogue();
        if (compileReachableOnly)
        {
            foreach (var source in compilationSources.Where(source => !IsGeneratedSource(source.SyntaxTree.GetCompilationUnitRoot())))
                EmitAllRemainingComments(source);
        }
        else
        {
            foreach (var root in commentRoots)
                EmitAllRemainingComments(root);
        }
        EmitDurableExecutionCleanupLabel();
        EmitHeapEpilogue();
        EmitDurableExecutionBodyEpilogue();

        return new TranspileResult(_sql.ToString(), _diagnostics.AsReadOnly());
    }

    internal TranspileResult Transpile(IrProgram program, TranspileOptions? options = null)
    {
        if (_used)
            return new SharpSqlCompiler().Transpile(program, options);
        _used = true;
        _options = options ?? new TranspileOptions();
        foreach (var method in program.Methods)
            _methods.TryAdd(method, out _);
        AnalyzeMethodBehaviors();
        _boundProgram = program with { Methods = _methods.Values.ToArray() };
        PrepareHeapRuntime(_boundProgram);
        _methodGraph = MethodGraph.Create(_boundProgram.Methods, _boundProgram.EntryPoint);
        PrepareVmMethods();

        EmitIrComments(program.FileComments);
        if (_options.EmitNoCount)
        {
            _sql.Line("SET NOCOUNT ON;");
            _sql.Line();
        }

        var scope = new VariableScope();
        EmitDurableRuntimePreamble();
        EmitVmPreamble();
        EmitHeapPreamble();
        EmitDurableRuntimeProvisioningEpilogue();
        EmitDurableExecutionBodyPreamble();
        if (!TryEmitServiceBrokerProgram(_boundProgram))
            EmitProceduralStatementSequence(program.EntryPoint.Statements, scope, null, null, null);
        EmitVmEpilogue();
        EmitDurableExecutionCleanupLabel();
        EmitHeapEpilogue();
        EmitDurableExecutionBodyEpilogue();
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
                position.Character + 1,
                tree.FilePath));
        }
    }

    private void AddSemanticDiagnostics(SyntaxTree tree)
    {
        if (_compilation is null)
            return;

        var parseErrors = tree.GetDiagnostics()
            .Where(item => item.Severity == DiagnosticSeverity.Error)
            .Select(item => (item.Id, item.Location.SourceSpan))
            .ToHashSet();
        _semanticDiagnostics ??= _compilation.GetMethodBodyDiagnostics()
            .Where(item => item.Severity == DiagnosticSeverity.Error && DataFlowDiagnosticIds.Contains(item.Id))
            .ToArray();
        foreach (var diagnostic in _semanticDiagnostics
                     .Where(item =>
                         item.Location.SourceTree == tree &&
                         !parseErrors.Contains((item.Id, item.Location.SourceSpan))))
        {
            var position = diagnostic.Location.GetLineSpan().StartLinePosition;
            _diagnostics.Add(new CompilerDiagnostic(
                diagnostic.Id,
                diagnostic.GetMessage(CultureInfo.InvariantCulture),
                position.Line + 1,
                position.Character + 1,
                tree.FilePath));
        }
    }

    private static CSharpCompilation CreateCompilation(SyntaxTree sourceTree)
    {
        var globalUsings = CSharpSyntaxTree.ParseText(
            "global using System; global using System.Collections.Generic; global using System.Linq;",
            new CSharpParseOptions(LanguageVersion.Preview));
        var compilation = CSharpCompilation.Create(
            "SharpSqlInput",
            [sourceTree, globalUsings],
            DefaultReferences.Value,
            new CSharpCompilationOptions(OutputKind.ConsoleApplication));
        return compilation;
    }

    private static MetadataReference[] CreateDefaultReferences()
    {
        var trustedAssemblies = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))?
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries) ?? [];
        return trustedAssemblies
            .Where(path => DefaultReferenceNames.Contains(Path.GetFileName(path)))
            .Select(path => MetadataReference.CreateFromFile(path))
            .ToArray();
    }

    private SemanticModel? SemanticModelFor(SyntaxNode node)
    {
        if (_compilation is null || !_compilation.SyntaxTrees.Contains(node.SyntaxTree))
            return null;
        if (_semanticModels.TryGetValue(node.SyntaxTree, out var semanticModel))
            return semanticModel;
        semanticModel = _compilation.GetSemanticModel(node.SyntaxTree, ignoreAccessibility: true);
        _semanticModels.Add(node.SyntaxTree, semanticModel);
        return semanticModel;
    }

    private static bool IsGeneratedSource(CompilationUnitSyntax root)
    {
        var source = root.ToFullString();
        var prefix = source.Substring(0, Math.Min(source.Length, 512));
        return prefix.Contains("<auto-generated", StringComparison.OrdinalIgnoreCase) ||
               prefix.Contains("<autogenerated", StringComparison.OrdinalIgnoreCase);
    }

    private MethodDeclarationSyntax? SelectEntryPoint(
        CSharpCompilation compilation,
        IReadOnlyList<CompilationUnitSyntax> roots,
        string? requestedEntryPoint)
    {
        var hasTopLevelStatements = roots.Any(root => root.Members.OfType<GlobalStatementSyntax>().Any());
        if (string.IsNullOrWhiteSpace(requestedEntryPoint))
        {
            if (hasTopLevelStatements)
                return null;
            var compilerEntryPoint = compilation.GetEntryPoint(default);
            return compilerEntryPoint?.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() as MethodDeclarationSyntax;
        }

        var requested = requestedEntryPoint!.Trim();
        var candidates = roots
            .SelectMany(root => root.DescendantNodes().OfType<MethodDeclarationSyntax>())
            .Select(method => (Method: method, Symbol: SemanticModelFor(method)?.GetDeclaredSymbol(method)))
            .Where(candidate => candidate.Symbol is not null && EntryPointMatches(candidate.Symbol!, requested))
            .ToArray();

        if (candidates.Length == 0)
        {
            AddDiagnostic("SS0002", $"Entry method '{requested}' was not found.", roots[0]);
            return null;
        }
        if (candidates.Length > 1)
        {
            AddDiagnostic("SS0002", $"Entry method '{requested}' is ambiguous; use 'Namespace.Type::Method'.", roots[0]);
            return null;
        }

        var candidate = candidates[0];
        if (!candidate.Symbol!.IsStatic)
        {
            AddDiagnostic("SS0002", $"Entry method '{requested}' must be static.", candidate.Method);
            return null;
        }
        if (candidate.Symbol.Parameters.Length != 0)
        {
            AddDiagnostic("SS0002", $"Entry method '{requested}' must be parameterless.", candidate.Method);
            return null;
        }
        return candidate.Method;

        static bool EntryPointMatches(IMethodSymbol method, string requested)
        {
            var typeName = method.ContainingType.ToDisplayString();
            return string.Equals(method.Name, requested, StringComparison.Ordinal) ||
                   string.Equals($"{typeName}.{method.Name}", requested, StringComparison.Ordinal) ||
                   string.Equals($"{typeName}::{method.Name}", requested, StringComparison.Ordinal);
        }
    }

    private IReadOnlyCollection<SyntaxNode> FindReachableMethods(
        MethodDeclarationSyntax? selectedEntryPoint,
        IReadOnlyList<StatementSyntax> topLevelStatements)
    {
        var reachable = new HashSet<SyntaxNode>(ReferenceComparer<SyntaxNode>.Instance);
        var pending = new Queue<SyntaxNode>();
        var dispatchCandidates = _compilation!.SyntaxTrees
            .SelectMany(tree => tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>())
            .Select(declaration => (
                Declaration: declaration,
                Symbol: SemanticModelFor(declaration)?.GetDeclaredSymbol(declaration) as IMethodSymbol))
            .Where(candidate => candidate.Symbol is not null)
            .ToArray();
        if (selectedEntryPoint is not null)
        {
            reachable.Add(selectedEntryPoint);
            pending.Enqueue(selectedEntryPoint);
        }
        else
        {
            foreach (var statement in topLevelStatements)
                pending.Enqueue(statement);
        }

        while (pending.TryDequeue(out var source))
        {
            foreach (var invocation in InvocationsIn(source))
            {
                var symbolInfo = SemanticModelFor(invocation)?.GetSymbolInfo(invocation);
                var method = symbolInfo?.Symbol as IMethodSymbol ??
                             symbolInfo?.CandidateSymbols.OfType<IMethodSymbol>().SingleOrDefault();
                EnqueueMethod(method);
            }

            foreach (var argument in ArgumentsIn(source))
            {
                var symbolInfo = SemanticModelFor(argument)?.GetSymbolInfo(argument.Expression);
                var method = symbolInfo?.Symbol as IMethodSymbol ??
                             symbolInfo?.CandidateSymbols.OfType<IMethodSymbol>().SingleOrDefault();
                EnqueueMethod(method);
            }

            foreach (var creation in ObjectCreationsIn(source))
            {
                var constructor = SemanticModelFor(creation)?.GetSymbolInfo(creation).Symbol as IMethodSymbol;
                EnqueueConstructor(constructor);
                foreach (var reference in constructor?.ContainingType.DeclaringSyntaxReferences ?? [])
                {
                    var declaration = reference.GetSyntax();
                    if (declaration is not TypeDeclarationSyntax ||
                        _compilation is null ||
                        !_compilation.SyntaxTrees.Contains(declaration.SyntaxTree) ||
                        !reachable.Add(declaration))
                        continue;
                    pending.Enqueue(declaration);
                }
            }

            if (source is ConstructorDeclarationSyntax { Initializer: not null } constructorDeclaration)
            {
                var constructor = SemanticModelFor(constructorDeclaration.Initializer)?
                    .GetSymbolInfo(constructorDeclaration.Initializer).Symbol as IMethodSymbol;
                EnqueueConstructor(constructor);
            }

            void EnqueueConstructor(IMethodSymbol? constructor)
            {
                foreach (var reference in constructor?.OriginalDefinition.DeclaringSyntaxReferences ?? [])
                {
                    var declaration = reference.GetSyntax();
                    if (declaration is not ConstructorDeclarationSyntax ||
                        _compilation is null ||
                        !_compilation.SyntaxTrees.Contains(declaration.SyntaxTree) ||
                        !reachable.Add(declaration))
                        continue;
                    pending.Enqueue(declaration);
                }
            }

            void EnqueueMethod(IMethodSymbol? method)
            {
                if (method is null)
                    return;
                method = method.ReducedFrom ?? method;
                foreach (var reference in method.OriginalDefinition.DeclaringSyntaxReferences)
                {
                    var declaration = reference.GetSyntax();
                    if (declaration is not (MethodDeclarationSyntax or LocalFunctionStatementSyntax) ||
                        _compilation is null || !_compilation.SyntaxTrees.Contains(declaration.SyntaxTree) ||
                        !reachable.Add(declaration))
                        continue;
                    pending.Enqueue(declaration);
                }
                EnqueueDispatchImplementations(method);
            }

            void EnqueueDispatchImplementations(IMethodSymbol target)
            {
                if (target.IsStatic || target.MethodKind == MethodKind.DelegateInvoke ||
                    target.ContainingType.TypeKind != TypeKind.Interface &&
                    !target.IsVirtual && !target.IsOverride && !target.IsAbstract)
                    return;

                foreach (var candidate in dispatchCandidates)
                {
                    var candidateSymbol = candidate.Symbol!;
                    var matches = target.ContainingType.TypeKind == TypeKind.Interface
                        ? SymbolEqualityComparer.Default.Equals(
                            candidateSymbol.ContainingType.FindImplementationForInterfaceMember(target),
                            candidateSymbol)
                        : OverridesOrMatches(candidateSymbol, target);
                    if (!matches || !reachable.Add(candidate.Declaration))
                        continue;
                    pending.Enqueue(candidate.Declaration);
                }
            }

            static bool OverridesOrMatches(IMethodSymbol candidate, IMethodSymbol target)
            {
                for (IMethodSymbol? current = candidate; current is not null; current = current.OverriddenMethod)
                    if (SymbolEqualityComparer.Default.Equals(current.OriginalDefinition, target.OriginalDefinition))
                        return true;
                return false;
            }
        }
        return reachable;

        static IEnumerable<InvocationExpressionSyntax> InvocationsIn(SyntaxNode source) =>
            source.DescendantNodesAndSelf(node =>
                    ReferenceEquals(node, source) || node is not (LocalFunctionStatementSyntax or MethodDeclarationSyntax))
                .OfType<InvocationExpressionSyntax>();

        static IEnumerable<ArgumentSyntax> ArgumentsIn(SyntaxNode source) =>
            source.DescendantNodesAndSelf(node =>
                    ReferenceEquals(node, source) || node is not (LocalFunctionStatementSyntax or MethodDeclarationSyntax))
                .OfType<ArgumentSyntax>();

        static IEnumerable<BaseObjectCreationExpressionSyntax> ObjectCreationsIn(SyntaxNode source) =>
            source.DescendantNodesAndSelf(node =>
                    ReferenceEquals(node, source) || node is not (LocalFunctionStatementSyntax or MethodDeclarationSyntax))
                .OfType<BaseObjectCreationExpressionSyntax>();
    }

    private void CollectMethods(
        CompilationUnitSyntax root,
        MethodDeclarationSyntax? selectedEntryPoint,
        IReadOnlyCollection<SyntaxNode> reachableMethods)
    {
        foreach (var local in root.DescendantNodes().OfType<LocalFunctionStatementSyntax>()
                     .Where(reachableMethods.Contains))
        {
            var scope = new VariableScope();
            var localSymbol = SemanticModelFor(local)?.GetDeclaredSymbol(local) as IMethodSymbol;
            AddMethod(new MethodDefinition(
                local.Identifier.ValueText,
                CSharpTypeFactory.From(local.ReturnType),
                local.ParameterList.Parameters.Select(ToParameter).ToArray(),
                local.Body is null ? null : (ProceduralBlock)BindProceduralStatement(local.Body, scope),
                local.ExpressionBody is null ? null : BindIrExpression(local.ExpressionBody.Expression, scope),
                ToIrSource(local))
            {
                Id = MethodIdentity(localSymbol),
                IsAsync = localSymbol?.IsAsync == true || local.Modifiers.Any(SyntaxKind.AsyncKeyword)
            });
        }

        foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>()
                     .Where(reachableMethods.Contains))
        {
            if (method == selectedEntryPoint)
                continue;

            var containingType = method.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
            var isInstance = containingType is not null && !method.Modifiers.Any(SyntaxKind.StaticKeyword);
            var methodSymbol = SemanticModelFor(method)?.GetDeclaredSymbol(method) as IMethodSymbol;
            var scope = new VariableScope();
            var parameters = method.ParameterList.Parameters.Select(ToParameter).ToList();
            if (isInstance)
                parameters.Insert(0, new ParameterDefinition(GetOrCreateIrSymbol(
                    null,
                    "this",
                    new IrType(containingType!.Identifier.ValueText, IsReference: true))));

            AddMethod(new MethodDefinition(
                method.Identifier.ValueText,
                CSharpTypeFactory.From(method.ReturnType),
                parameters,
                method.Body is null ? null : (ProceduralBlock)BindProceduralStatement(method.Body, scope),
                method.ExpressionBody is null ? null : BindIrExpression(method.ExpressionBody.Expression, scope),
                ToIrSource(method),
                containingType?.Identifier.ValueText,
                isInstance)
            {
                Id = MethodIdentity(methodSymbol),
                IsAsync = methodSymbol?.IsAsync == true || method.Modifiers.Any(SyntaxKind.AsyncKeyword),
                IsAbstract = methodSymbol?.IsAbstract == true,
                IsVirtual = methodSymbol?.IsVirtual == true,
                IsOverride = methodSymbol?.IsOverride == true,
                IsSealed = methodSymbol?.IsSealed == true,
                OverriddenMethodId = MethodIdentity(methodSymbol?.OverriddenMethod),
                ImplementedInterfaceMethodIds = InterfaceMethodIdentities(methodSymbol)
            });
        }
    }

    private void AddMethod(MethodDefinition method)
    {
        method = method with { Flow = AnalyzeMethodFlow(method) };
        if (!_methods.TryAdd(method, out _))
            AddDiagnostic("SS1001", $"Duplicate method identity is not supported: '{method.Name}'.", method.Source);
    }

    private MethodFlowSummary AnalyzeMethodFlow(MethodDefinition method)
    {
        if (method.Body is null)
        {
            return new MethodFlowSummary(
                EndPointIsReachable: method.ReturnType.Name == "void",
                StatementCount: 1);
        }

        var body = CSharpSyntax<BlockSyntax>(method.Body.Source);
        var semanticModel = SemanticModelFor(body);
        var controlFlow = semanticModel?.AnalyzeControlFlow(body);
        return new MethodFlowSummary(
            controlFlow is { Succeeded: true, EndPointIsReachable: true },
            body.DescendantNodes().OfType<StatementSyntax>().Count());
    }

    private ParameterDefinition ToParameter(ParameterSyntax parameter) =>
        new(GetOrCreateIrSymbol(
            SemanticModelFor(parameter)?.GetDeclaredSymbol(parameter),
            parameter.Identifier.ValueText,
            parameter.Type is null ? IrType.Unknown : CSharpTypeFactory.From(parameter.Type)));

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

    private string EmitScalar(
        ExpressionSyntax expression,
        VariableScope scope,
        IReadOnlyDictionary<string, Substitution>? substitutions = null) =>
        EmitScalarExpression(expression, scope, substitutions).Sql;

    private SqlScalarExpression EmitScalarExpression(
        ExpressionSyntax expression,
        VariableScope scope,
        IReadOnlyDictionary<string, Substitution>? substitutions = null)
    {
        EmitExpressionComments(expression);
        expression = StripParentheses(expression);
        var analysis = AnalyzeExpression(expression, scope, substitutions);
        if (analysis.Type.IsBoolean && IsPredicateShape(expression))
            return SqlScalarExpression.Primary(
                $"CASE WHEN {EmitPredicate(expression, scope, substitutions)} THEN CAST(1 AS BIT) ELSE CAST(0 AS BIT) END",
                IrType.Bool,
                ScalarNullability.NonNull);

        var result = expression switch
        {
            LiteralExpressionSyntax literal => SqlScalarExpression.Primary(EmitLiteral(literal)),
            IdentifierNameSyntax identifier => EmitIdentifierExpression(identifier, scope, substitutions),
            ThisExpressionSyntax or BaseExpressionSyntax => EmitThisExpression(scope, substitutions),
            BinaryExpressionSyntax binary => EmitBinaryScalar(binary, scope, substitutions),
            PrefixUnaryExpressionSyntax prefix => EmitPrefixScalar(prefix, scope, substitutions),
            CastExpressionSyntax cast => EmitScalarExpression(
                cast.Expression,
                scope,
                substitutions).CastTo(CSharpTypeFactory.From(cast.Type)),
            ConditionalExpressionSyntax conditional => SqlScalarExpression.Primary(
                $"CASE WHEN {EmitPredicate(conditional.Condition, scope, substitutions)} THEN {EmitScalar(conditional.WhenTrue, scope, substitutions)} ELSE {EmitScalar(conditional.WhenFalse, scope, substitutions)} END"),
            InvocationExpressionSyntax invocation => EmitInvocation(invocation, scope, substitutions),
            MemberAccessExpressionSyntax member => EmitHeapMemberScalar(member, scope, substitutions),
            ElementAccessExpressionSyntax element => EmitHeapElementScalar(element, scope),
            ObjectCreationExpressionSyntax creation => EmitIntrinsicObjectCreation(creation, scope, substitutions),
            InterpolatedStringExpressionSyntax interpolated => EmitInterpolatedString(interpolated, scope, substitutions),
            CheckedExpressionSyntax checkedExpression => EmitScalarExpression(checkedExpression.Expression, scope, substitutions),
            _ => SqlScalarExpression.Primary(UnsupportedExpression(expression))
        };
        return result.WithAnalysis(analysis.Type, analysis.Nullability);
    }

    private string EmitPredicate(
        ExpressionSyntax expression,
        VariableScope scope,
        IReadOnlyDictionary<string, Substitution>? substitutions = null)
    {
        expression = StripParentheses(expression);
        var analysis = AnalyzeExpression(expression, scope, substitutions);
        if (analysis.HasConstantValue && analysis.ConstantValue is bool constant)
            return constant ? "1 = 1" : "1 = 0";

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

    private SqlScalarExpression EmitBinaryScalar(
        BinaryExpressionSyntax binary,
        VariableScope scope,
        IReadOnlyDictionary<string, Substitution>? substitutions)
    {
        if (binary.IsKind(SyntaxKind.CoalesceExpression))
            return SqlScalarExpression.Primary(
                $"COALESCE({EmitScalar(binary.Left, scope, substitutions)}, {EmitScalar(binary.Right, scope, substitutions)})");

        if (binary.IsKind(SyntaxKind.AddExpression) &&
            (InferType(binary.Left, scope, substitutions).IsString || InferType(binary.Right, scope, substitutions).IsString))
            return SqlScalarExpression.Primary(
                $"CONCAT({EmitScalar(binary.Left, scope, substitutions)}, {EmitScalar(binary.Right, scope, substitutions)})");

        var op = SqlOperator(binary.Kind());
        if (op.Length == 0)
            return SqlScalarExpression.Primary(UnsupportedExpression(binary));

        var precedence = BinaryPrecedence(binary.Kind());
        var left = EmitScalarExpression(binary.Left, scope, substitutions).Render(precedence);
        var right = EmitScalarExpression(binary.Right, scope, substitutions).Render(precedence + 1);
        return new SqlScalarExpression($"{left} {op} {right}", IrType.Unknown, precedence);
    }

    private SqlScalarExpression EmitPrefixScalar(
        PrefixUnaryExpressionSyntax prefix,
        VariableScope scope,
        IReadOnlyDictionary<string, Substitution>? substitutions) => prefix.Kind() switch
        {
            SyntaxKind.UnaryMinusExpression => new SqlScalarExpression(
                $"-{EmitScalarExpression(prefix.Operand, scope, substitutions).Render(PrecedenceUnary + 1)}",
                IrType.Unknown,
                PrecedenceUnary),
            SyntaxKind.UnaryPlusExpression => new SqlScalarExpression(
                $"+{EmitScalarExpression(prefix.Operand, scope, substitutions).Render(PrecedenceUnary + 1)}",
                IrType.Unknown,
                PrecedenceUnary),
            SyntaxKind.BitwiseNotExpression => new SqlScalarExpression(
                $"~{EmitScalarExpression(prefix.Operand, scope, substitutions).Render(PrecedenceUnary + 1)}",
                IrType.Unknown,
                PrecedenceUnary),
            SyntaxKind.LogicalNotExpression => SqlScalarExpression.Primary(
                $"CASE WHEN {EmitPredicate(prefix.Operand, scope, substitutions)} THEN CAST(0 AS BIT) ELSE CAST(1 AS BIT) END"),
            _ => SqlScalarExpression.Primary(UnsupportedExpression(prefix))
        };

    private SqlScalarExpression EmitInvocation(
        InvocationExpressionSyntax invocation,
        VariableScope scope,
        IReadOnlyDictionary<string, Substitution>? substitutions)
    {
        if (TryEmitLinqInvocation(invocation, scope, substitutions, out var linqExpression))
            return linqExpression;

        if (substitutions is null && TryEmitHeapInvocationScalar(invocation, scope, out var heapExpression))
            return heapExpression;

        if (!TryGetMethod(invocation, out var method))
            return SqlScalarExpression.Primary(
                UnsupportedExpression(invocation, "Only user-defined methods and Console.WriteLine are supported."));
        if (method.PureExpression is null)
            return SqlScalarExpression.Primary(
                UnsupportedExpression(invocation, "A branching method call must be the complete variable initializer."));
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
            if ((IsSequenceType(parameter.Type.Name) || IsLinqSequenceType(parameter.Type.Name)) &&
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

    private static IReadOnlyList<IrExpression> InvocationArgumentExpressions(
        IrInvocationExpression invocation,
        MethodDefinition method)
    {
        var arguments = new List<IrExpression>();
        if (method.IsInstance)
        {
            arguments.Add(invocation.Target is IrMemberExpression member
                ? member.Receiver
                : new IrThisExpression(
                    invocation.Source,
                    new ExpressionFacts(
                        method.Parameters[0].Type,
                        ScalarNullability.MaybeNull,
                        HasConstantValue: false,
                        ConstantValue: null),
                    method.Parameters[0].Symbol));
        }
        arguments.AddRange(invocation.Arguments);
        return arguments;
    }

    private SqlScalarExpression EmitInterpolatedString(
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
            0 => SqlScalarExpression.Primary("N''"),
            1 when interpolated.Contents[0] is InterpolatedStringTextSyntax => SqlScalarExpression.Primary(parts[0]),
            1 => SqlScalarExpression.Primary($"CONCAT(N'', {parts[0]})"),
            _ => SqlScalarExpression.Primary($"CONCAT({string.Join(", ", parts)})")
        };
    }

    private SqlScalarExpression EmitIntrinsicObjectCreation(
        ObjectCreationExpressionSyntax creation,
        VariableScope scope,
        IReadOnlyDictionary<string, Substitution>? substitutions)
    {
        if (IsStringCharacterArrayCreation(creation, scope, substitutions) &&
            creation.ArgumentList!.Arguments is { Count: 1 } arguments)
        {
            var characters = EmitScalar(arguments[0].Expression, scope, substitutions);
            return SqlScalarExpression.Primary(StringFromCharacterArraySql(characters));
        }

        return SqlScalarExpression.Primary(UnsupportedExpression(
            creation,
            "Only the string(char[]) scalar constructor is supported."));
    }

    private bool IsStringCharacterArrayCreation(
        ObjectCreationExpressionSyntax creation,
        VariableScope scope,
        IReadOnlyDictionary<string, Substitution>? substitutions = null) =>
        CSharpTypeFactory.From(creation.Type).IsString &&
        creation.ArgumentList?.Arguments is { Count: 1 } arguments &&
        InferType(arguments[0].Expression, scope, substitutions).Name == "char[]";

    private string StringFromCharacterArraySql(string characters) =>
        $"COALESCE((SELECT STRING_AGG(CONVERT(NVARCHAR(MAX), CONVERT(NCHAR(1), __value)), N'') " +
        $"WITHIN GROUP (ORDER BY __index) FROM {HeapIndexedItems} WHERE {HeapExecutionFilter()}__owner_id = {characters}), N'')";

    private string EmitInterpolation(
        InterpolationSyntax interpolation,
        VariableScope scope,
        IReadOnlyDictionary<string, Substitution>? substitutions)
    {
        var value = EmitScalar(interpolation.Expression, scope, substitutions);
        return FormatTextValue(InferType(interpolation.Expression, scope, substitutions), value);
    }

    private string EmitIdentifier(
        IdentifierNameSyntax identifier,
        VariableScope scope,
        IReadOnlyDictionary<string, Substitution>? substitutions) =>
        EmitIdentifierExpression(identifier, scope, substitutions).Sql;

    private SqlScalarExpression EmitIdentifierExpression(
        IdentifierNameSyntax identifier,
        VariableScope scope,
        IReadOnlyDictionary<string, Substitution>? substitutions)
    {
        var name = identifier.Identifier.ValueText;
        if (substitutions is not null && substitutions.TryGetValue(name, out var replacement))
            return replacement.Expression;
        if (scope.Find(name) is ScalarVariableBinding binding)
            return binding.Scalar;
        if (TryEmitImplicitHeapField(identifier, scope, substitutions, out var heapField))
            return heapField;
        AddDiagnostic("SS4001", $"Unknown identifier '{name}'.", identifier);
        return SqlScalarExpression.Primary("NULL");
    }

    private static SqlScalarExpression EmitThisExpression(
        VariableScope scope,
        IReadOnlyDictionary<string, Substitution>? substitutions)
    {
        if (substitutions is not null && substitutions.TryGetValue("this", out var replacement))
            return replacement.Expression;
        return scope.Find("this") is ScalarVariableBinding binding
            ? binding.Scalar
            : SqlScalarExpression.Primary("NULL");
    }

    private IrType InferType(
        ExpressionSyntax expression,
        VariableScope scope,
        IReadOnlyDictionary<string, Substitution>? substitutions = null) =>
        AnalyzeExpression(expression, scope, substitutions).Type;

    private ExpressionFacts AnalyzeExpression(
        ExpressionSyntax expression,
        VariableScope scope,
        IReadOnlyDictionary<string, Substitution>? substitutions = null)
    {
        expression = StripParentheses(expression);
        if (expression is IdentifierNameSyntax substitutedIdentifier &&
            substitutions is not null &&
            substitutions.TryGetValue(substitutedIdentifier.Identifier.ValueText, out var substitution))
        {
            return new ExpressionFacts(
                substitution.Expression.Type,
                substitution.Expression.Nullability,
                HasConstantValue: false,
                ConstantValue: null);
        }

        var semanticModel = SemanticModelFor(expression);
        if (semanticModel is not null)
        {
            var typeInfo = semanticModel.GetTypeInfo(expression);
            if (typeInfo.Type is { TypeKind: not TypeKind.Error } semanticType)
            {
                var constant = semanticModel.GetConstantValue(expression);
                return new ExpressionFacts(
                    CSharpTypeFactory.From(semanticType),
                    ToScalarNullability(typeInfo.Nullability.FlowState, expression),
                    constant.HasValue,
                    constant.HasValue ? constant.Value : null);
            }
        }

        var fallbackType = InferTypeFallback(expression, scope, substitutions);
        return new ExpressionFacts(
            fallbackType,
            expression.IsKind(SyntaxKind.NullLiteralExpression)
                ? ScalarNullability.Null
                : fallbackType.IsReference
                    ? ScalarNullability.MaybeNull
                    : ScalarNullability.NonNull,
            HasConstantValue: false,
            ConstantValue: null);
    }

    private static ScalarNullability ToScalarNullability(
        NullableFlowState flowState,
        ExpressionSyntax expression)
    {
        if (expression.IsKind(SyntaxKind.NullLiteralExpression))
            return ScalarNullability.Null;
        return flowState switch
        {
            NullableFlowState.NotNull => ScalarNullability.NonNull,
            NullableFlowState.MaybeNull => ScalarNullability.MaybeNull,
            _ => ScalarNullability.Unknown
        };
    }

    private IrType InferTypeFallback(
        ExpressionSyntax expression,
        VariableScope scope,
        IReadOnlyDictionary<string, Substitution>? substitutions = null)
    {
        expression = StripParentheses(expression);
        return expression switch
        {
            LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.StringLiteralExpression) => IrType.String,
            LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.CharacterLiteralExpression) => new("char"),
            LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.TrueLiteralExpression) || literal.IsKind(SyntaxKind.FalseLiteralExpression) => IrType.Bool,
            LiteralExpressionSyntax literal when literal.Token.Value is decimal => new("decimal"),
            LiteralExpressionSyntax literal when literal.Token.Value is double => new("double"),
            LiteralExpressionSyntax literal when literal.Token.Value is float => new("float"),
            LiteralExpressionSyntax literal when literal.Token.Value is long => new("long"),
            LiteralExpressionSyntax => IrType.Int,
            IdentifierNameSyntax identifier when substitutions is not null && substitutions.TryGetValue(identifier.Identifier.ValueText, out var value) => value.Type,
            IdentifierNameSyntax identifier => scope.Find(identifier.Identifier.ValueText)?.Type ?? IrType.Unknown,
            ThisExpressionSyntax or BaseExpressionSyntax => scope.Find("this")?.Type ?? IrType.Unknown,
            BinaryExpressionSyntax binary when IsPredicateShape(binary) => IrType.Bool,
            BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.AddExpression) &&
                (InferType(binary.Left, scope, substitutions).IsString || InferType(binary.Right, scope, substitutions).IsString) => IrType.String,
            BinaryExpressionSyntax binary => InferType(binary.Left, scope, substitutions),
            PrefixUnaryExpressionSyntax prefix when prefix.IsKind(SyntaxKind.LogicalNotExpression) => IrType.Bool,
            PrefixUnaryExpressionSyntax prefix => InferType(prefix.Operand, scope, substitutions),
            CastExpressionSyntax cast => CSharpTypeFactory.From(cast.Type),
            ConditionalExpressionSyntax conditional => InferType(conditional.WhenTrue, scope, substitutions),
            InterpolatedStringExpressionSyntax => IrType.String,
            ObjectCreationExpressionSyntax creation => CSharpTypeFactory.From(creation.Type),
            ArrayCreationExpressionSyntax creation => CSharpTypeFactory.From(creation.Type),
            MemberAccessExpressionSyntax member => InferHeapMemberType(member, scope),
            ElementAccessExpressionSyntax element => InferHeapElementType(element, scope),
            InvocationExpressionSyntax invocation when invocation.Expression is MemberAccessExpressionSyntax member &&
                ((IsDictionaryType(InferType(member.Expression, scope, substitutions).Name) &&
                  member.Name.Identifier.ValueText is "ContainsKey" or "ContainsValue") ||
                 (IsListType(InferType(member.Expression, scope, substitutions).Name) &&
                  member.Name.Identifier.ValueText == "Contains")) => IrType.Bool,
            InvocationExpressionSyntax invocation when TryGetMethod(invocation, out var method) => method.ReturnType,
            _ => IrType.Unknown
        };
    }

    private string UnsupportedExpression(SyntaxNode node, string? detail = null)
    {
        AddDiagnostic("SS4002", detail ?? $"Unsupported expression: {node.Kind()}.", node);
        return "NULL";
    }

    private string UnsupportedExpression(IrSource source, string? detail = null)
    {
        AddDiagnostic("SS4002", detail ?? "Unsupported IR expression.", source);
        return "NULL";
    }

    private void Unsupported(SyntaxNode node, string category) =>
        AddDiagnostic("SS4003", $"Unsupported {category}: {node.Kind()}.", node);

    private void Unsupported(IrSource source, string category) =>
        AddDiagnostic("SS4003", $"Unsupported {category}.", source);

    private void AddDiagnostic(string code, string message, SyntaxNode node)
    {
        var location = node.GetLocation().GetLineSpan().StartLinePosition;
        var diagnostic = new CompilerDiagnostic(
            code,
            message,
            location.Line + 1,
            location.Character + 1,
            node.SyntaxTree.FilePath);
        if (!_diagnostics.Contains(diagnostic))
            _diagnostics.Add(diagnostic);
    }

    private void AddDiagnostic(string code, string message, IrSource source)
    {
        var filePath = _csharpSourceNodes.TryGetValue(source, out var node)
            ? node.SyntaxTree.FilePath
            : null;
        var diagnostic = new CompilerDiagnostic(code, message, source.Span.Line, source.Span.Column, filePath);
        if (!_diagnostics.Contains(diagnostic))
            _diagnostics.Add(diagnostic);
    }

    private static string? InvocationName(ExpressionSyntax expression) => expression switch
    {
        IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
        GenericNameSyntax generic => generic.Identifier.ValueText,
        MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
        _ => null
    };

    private bool TryGetMethod(IrInvocationExpression invocation, out MethodDefinition method) =>
        _methods.TryResolve(invocation, out method);

    private bool TryGetMethod(InvocationExpressionSyntax invocation, out MethodDefinition method)
    {
        var symbol = SemanticModelFor(invocation)?.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
        if (symbol is not null)
            return _methods.TryGetValue(MethodIdentity(symbol), out method);
        var name = InvocationName(invocation.Expression);
        if (name is not null && _methods.TryGetValue(name, out method))
            return true;
        method = null!;
        return false;
    }

    private bool InvocationInputsAreDiscardable(InvocationExpressionSyntax invocation)
    {
        if (invocation.Expression is MemberAccessExpressionSyntax member &&
            !ExpressionIsDiscardable(member.Expression))
            return false;
        return invocation.ArgumentList.Arguments.All(argument => ExpressionIsDiscardable(argument.Expression));
    }

    private bool InvocationInputsAreDiscardable(IrInvocationExpression invocation)
    {
        if (invocation.Target is IrMemberExpression member && !ExpressionIsDiscardable(member.Receiver))
            return false;
        return invocation.Arguments.All(ExpressionIsDiscardable);
    }

    private bool ExpressionIsDiscardable(IrExpression expression) => expression switch
    {
        IrConstantExpression or IrVariableExpression or IrThisExpression => true,
        IrConversionExpression conversion => ExpressionIsDiscardable(conversion.Operand),
        IrUnaryExpression unary when unary.Operator is not (
            IrUnaryOperator.PreIncrement or IrUnaryOperator.PreDecrement or
            IrUnaryOperator.PostIncrement or IrUnaryOperator.PostDecrement) =>
            ExpressionIsDiscardable(unary.Operand),
        IrBinaryExpression binary when binary.Operator is not (
            IrBinaryOperator.Divide or IrBinaryOperator.Remainder) =>
            ExpressionIsDiscardable(binary.Left) && ExpressionIsDiscardable(binary.Right),
        IrConditionalExpression conditional =>
            ExpressionIsDiscardable(conditional.Condition) &&
            ExpressionIsDiscardable(conditional.WhenTrue) &&
            ExpressionIsDiscardable(conditional.WhenFalse),
        IrInvocationExpression nested when TryGetMethod(nested, out var method) =>
            method.Behavior.IsSideEffectFree && InvocationInputsAreDiscardable(nested),
        _ => false
    };

    private bool ExpressionIsDiscardable(ExpressionSyntax expression)
    {
        expression = StripParentheses(expression);
        return expression switch
        {
            LiteralExpressionSyntax or IdentifierNameSyntax or ThisExpressionSyntax or BaseExpressionSyntax => true,
            CastExpressionSyntax cast => ExpressionIsDiscardable(cast.Expression),
            PrefixUnaryExpressionSyntax prefix when prefix.Kind() is not (
                SyntaxKind.PreIncrementExpression or SyntaxKind.PreDecrementExpression) =>
                ExpressionIsDiscardable(prefix.Operand),
            BinaryExpressionSyntax binary when binary.Kind() is not (
                SyntaxKind.DivideExpression or SyntaxKind.ModuloExpression) =>
                ExpressionIsDiscardable(binary.Left) && ExpressionIsDiscardable(binary.Right),
            ConditionalExpressionSyntax conditional =>
                ExpressionIsDiscardable(conditional.Condition) &&
                ExpressionIsDiscardable(conditional.WhenTrue) &&
                ExpressionIsDiscardable(conditional.WhenFalse),
            InvocationExpressionSyntax nested when TryGetMethod(nested, out var method) =>
                method.Behavior.IsSideEffectFree && InvocationInputsAreDiscardable(nested),
            _ => false
        };
    }

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
    private sealed record Substitution(SqlScalarExpression Expression)
    {
        public IrType Type => Expression.Type;
    }
}
