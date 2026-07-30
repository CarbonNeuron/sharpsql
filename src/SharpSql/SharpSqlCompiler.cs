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

    /// <summary>Transpiles a C# source string into a SQL Server batch.</summary>
    /// <param name="source">The C# source to transpile.</param>
    /// <param name="options">Optional compiler settings.</param>
    /// <returns>The generated SQL and any compiler diagnostics.</returns>
    public TranspileResult Transpile(string source, TranspileOptions? options = null)
    {
        if (_used)
            return new SharpSqlCompiler().Transpile(source, options);
        _used = true;

        var tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview));
        return TranspileCompilation(CreateCompilation(tree), entryPoint: null, options, compileReachableOnly: false);
    }

    /// <summary>Transpiles an existing Roslyn compilation into a SQL Server batch.</summary>
    /// <param name="compilation">The C# compilation to transpile.</param>
    /// <param name="entryPoint">
    /// An optional entry method name, qualified as <c>Namespace.Type::Method</c> when needed.
    /// </param>
    /// <param name="options">Optional compiler settings.</param>
    /// <returns>The generated SQL and any compiler diagnostics.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="compilation"/> is <see langword="null"/>.</exception>
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
        _ = SqlIdentifier.Validate(_options.ApplicationSchema, nameof(TranspileOptions.ApplicationSchema));
        _compilation = compilation;
        var roots = compilation.SyntaxTrees
            .Select(tree => tree.GetCompilationUnitRoot())
            .ToArray();

        if (roots.Length == 0)
        {
            ResolveRuntimeConfigurationWithoutProgram();
            _diagnostics.Add(new CompilerDiagnostic("SS0001", "The compilation contains no C# source files.", 0, 0));
            return CreateTranspileResult(string.Empty);
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
        _methodGraph = MethodGraph.Create(_boundProgram.Methods, _boundProgram.EntryPoint);
        if (!ResolveRuntimeConfiguration(_boundProgram))
            return CreateTranspileResult(string.Empty);
        ValidateNativeKernelOptions(_boundProgram.EntryPoint.Source);
        PrepareHeapRuntime(_boundProgram);
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

        return CreateTranspileResult(CompleteSql());
    }

    internal TranspileResult Transpile(IrProgram program, TranspileOptions? options = null)
    {
        if (_used)
            return new SharpSqlCompiler().Transpile(program, options);
        _used = true;
        _options = options ?? new TranspileOptions();
        _ = SqlIdentifier.Validate(_options.ApplicationSchema, nameof(TranspileOptions.ApplicationSchema));
        foreach (var method in program.Methods)
            _methods.TryAdd(method, out _);
        AnalyzeMethodBehaviors();
        _boundProgram = program with { Methods = _methods.Values.ToArray() };
        _methodGraph = MethodGraph.Create(_boundProgram.Methods, _boundProgram.EntryPoint);
        if (!ResolveRuntimeConfiguration(_boundProgram))
            return CreateTranspileResult(string.Empty);
        ValidateNativeKernelOptions(_boundProgram.EntryPoint.Source);
        PrepareHeapRuntime(_boundProgram);
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
        return CreateTranspileResult(CompleteSql());
    }

    private TranspileResult CreateTranspileResult(string sql) =>
        new(sql, _diagnostics.AsReadOnly())
        {
            EffectiveRuntime = _effectiveRuntime
        };

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
            "global using System; global using System.Collections.Generic; global using System.Linq; global using System.Threading.Tasks;",
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
            var returnType = CSharpTypeFactory.From(local.ReturnType);
            AddMethod(new MethodDefinition(
                local.Identifier.ValueText,
                returnType,
                local.ParameterList.Parameters.Select(ToParameter).ToArray(),
                local.Body is null ? null : (ProceduralBlock)BindProceduralStatement(local.Body, scope),
                local.ExpressionBody is null ? null : BindIrExpression(local.ExpressionBody.Expression, scope),
                ToIrSource(local))
            {
                Id = MethodIdentity(localSymbol),
                IsAsync = localSymbol?.IsAsync == true || local.Modifiers.Any(SyntaxKind.AsyncKeyword),
                Flow = BindMethodFlow(local.Body, returnType)
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
            var returnType = CSharpTypeFactory.From(method.ReturnType);
            var parameters = method.ParameterList.Parameters.Select(ToParameter).ToList();
            if (isInstance)
                parameters.Insert(0, new ParameterDefinition(GetOrCreateIrSymbol(
                    null,
                    "this",
                    new IrType(containingType!.Identifier.ValueText, IsReference: true))));

            AddMethod(new MethodDefinition(
                method.Identifier.ValueText,
                returnType,
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
                ImplementedInterfaceMethodIds = InterfaceMethodIdentities(methodSymbol),
                Flow = BindMethodFlow(method.Body, returnType)
            });
        }
    }

    private void AddMethod(MethodDefinition method)
    {
        if (!_methods.TryAdd(method, out _))
            AddDiagnostic("SS1001", $"Duplicate method identity is not supported: '{method.Name}'.", method.Source);
    }

    private ParameterDefinition ToParameter(ParameterSyntax parameter) =>
        new(GetOrCreateIrSymbol(
            SemanticModelFor(parameter)?.GetDeclaredSymbol(parameter),
            parameter.Identifier.ValueText,
            parameter.Type is null ? IrType.Unknown : CSharpTypeFactory.From(parameter.Type)));

    private sealed record InlineReturn(string? TargetSql, string EndLabel);
    private sealed record LoopContext(string BreakLabel, string ContinueLabel);
    private sealed record Substitution(SqlScalarExpression Expression)
    {
        public IrType Type => Expression.Type;
    }
}
