using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace SharpSql.Analyzers;

/// <summary>Reports SharpSql compatibility diagnostics during normal C# analysis.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class SharpSqlCompatibilityAnalyzer : DiagnosticAnalyzer
{
    /// <summary>The analyzer configuration property that enables SharpSql analysis.</summary>
    public const string EnabledProperty = "build_property.SharpSqlEnableAnalyzer";
    /// <summary>The analyzer configuration property that selects the entry point.</summary>
    public const string EntryPointProperty = "build_property.SharpSqlEntryPoint";
    /// <summary>The analyzer configuration property that selects runtime storage.</summary>
    public const string RuntimeStorageProperty = "build_property.SharpSqlRuntimeStorage";
    /// <summary>The analyzer configuration property that selects runtime execution.</summary>
    public const string ExecutionProperty = "build_property.SharpSqlExecution";
    /// <summary>The analyzer configuration property that selects runtime durability.</summary>
    public const string DurabilityProperty = "build_property.SharpSqlDurability";
    /// <summary>The analyzer configuration property that enables memory-optimized tables.</summary>
    public const string MemoryOptimizedProperty = "build_property.SharpSqlMemoryOptimized";
    /// <summary>The diagnostic identifier used for unexpected analyzer failures.</summary>
    public const string InternalErrorId = "SSA0001";

    private static readonly ImmutableDictionary<string, DiagnosticDescriptor> Descriptors =
        CreateDescriptors();

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        [.. Descriptors.Values];

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterCompilationStartAction(StartCompilationAnalysis);
    }

    private static void StartCompilationAnalysis(CompilationStartAnalysisContext context)
    {
        if (context.Compilation is not CSharpCompilation compilation ||
            !AnalyzerEnabled(context.Options.AnalyzerConfigOptionsProvider.GlobalOptions))
        {
            return;
        }

        var analysis = new Lazy<ImmutableArray<Diagnostic>>(
            () => AnalyzeCompilation(
                compilation,
                context.Options.AnalyzerConfigOptionsProvider.GlobalOptions),
            LazyThreadSafetyMode.ExecutionAndPublication);

        context.RegisterSemanticModelAction(semanticContext =>
        {
            var tree = semanticContext.SemanticModel.SyntaxTree;
            foreach (var diagnostic in analysis.Value)
            {
                if (diagnostic.Location.SourceTree == tree)
                    semanticContext.ReportDiagnostic(diagnostic);
            }
        });

        // Diagnostics without a source location cannot be reported by a document-scoped
        // action. Keep those in a compilation-end action while source diagnostics flow
        // through semantic analysis so IDEs can display them as the user edits a file.
        context.RegisterCompilationEndAction(compilationContext =>
        {
            foreach (var diagnostic in analysis.Value)
            {
                if (diagnostic.Location == Location.None)
                    compilationContext.ReportDiagnostic(diagnostic);
            }
        });
    }

    private static ImmutableArray<Diagnostic> AnalyzeCompilation(
        CSharpCompilation compilation,
        AnalyzerConfigOptions options)
    {
        if (compilation.GetDiagnostics()
            .Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
        {
            return ImmutableArray<Diagnostic>.Empty;
        }

        options.TryGetValue(EntryPointProperty, out var entryPoint);
        entryPoint = string.IsNullOrWhiteSpace(entryPoint) ? null : entryPoint;
        if (!TryGetRuntimeConfiguration(options, out var runtime, out var configurationError))
        {
            return [Diagnostic.Create(
                Descriptors[InternalErrorId],
                Location.None,
                configurationError)];
        }

        TranspileResult result;
        try
        {
            result = new SharpSqlCompiler().Transpile(
                compilation,
                entryPoint,
                new TranspileOptions
                {
                    Execution = runtime.Execution,
                    Durability = runtime.Durability,
                    UseMemoryOptimizedTables = runtime.UseMemoryOptimizedTables
                });
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return [Diagnostic.Create(
                Descriptors[InternalErrorId],
                Location.None,
                exception.Message)];
        }

        return [.. result.Diagnostics
            .Where(item => item.Code.StartsWith("SS", StringComparison.Ordinal))
            .Select(diagnostic => Diagnostic.Create(
                Descriptors.TryGetValue(diagnostic.Code, out var descriptor)
                    ? descriptor
                    : Descriptors[InternalErrorId],
                FindLocation(compilation, diagnostic),
                diagnostic.Message))];
    }

    private static bool AnalyzerEnabled(AnalyzerConfigOptions options) =>
        !options.TryGetValue(EnabledProperty, out var configured) ||
        !bool.TryParse(configured, out var enabled) ||
        enabled;

    private static bool TryGetRuntimeConfiguration(
        AnalyzerConfigOptions options,
        out RuntimeConfiguration runtime,
        out string error)
    {
        if (options.TryGetValue(RuntimeStorageProperty, out var legacy) &&
            !string.IsNullOrWhiteSpace(legacy))
        {
            if (!TryParseEnum(legacy, out RuntimeStorageKind storage))
            {
                runtime = default!;
                error = "SharpSqlRuntimeStorage must be Ephemeral, MemoryOptimized, Durable, or ServiceBroker.";
                return false;
            }

            runtime = storage switch
            {
                RuntimeStorageKind.MemoryOptimized => new RuntimeConfiguration(
                    RuntimeExecutionKind.Inline,
                    RuntimeDurabilityKind.Ephemeral,
                    UseMemoryOptimizedTables: true),
                RuntimeStorageKind.Durable => new RuntimeConfiguration(
                    RuntimeExecutionKind.Inline,
                    RuntimeDurabilityKind.Durable,
                    UseMemoryOptimizedTables: false),
                RuntimeStorageKind.ServiceBroker => new RuntimeConfiguration(
                    RuntimeExecutionKind.ServiceBroker,
                    RuntimeDurabilityKind.Durable,
                    UseMemoryOptimizedTables: false),
                _ => new RuntimeConfiguration(
                    RuntimeExecutionKind.Inline,
                    RuntimeDurabilityKind.Ephemeral,
                    UseMemoryOptimizedTables: false)
            };
            error = string.Empty;
            return true;
        }

        var executionValue = Value(options, ExecutionProperty) ?? nameof(RuntimeExecutionKind.Auto);
        if (!TryParseEnum(executionValue, out RuntimeExecutionKind execution))
        {
            runtime = default!;
            error = "SharpSqlExecution must be Auto, Inline, or ServiceBroker.";
            return false;
        }

        var durabilityValue = Value(options, DurabilityProperty) ?? nameof(RuntimeDurabilityKind.Ephemeral);
        if (!TryParseEnum(durabilityValue, out RuntimeDurabilityKind durability))
        {
            runtime = default!;
            error = "SharpSqlDurability must be Ephemeral or Durable.";
            return false;
        }

        var memoryValue = Value(options, MemoryOptimizedProperty) ?? bool.FalseString;
        if (!bool.TryParse(memoryValue, out var memoryOptimized))
        {
            runtime = default!;
            error = "SharpSqlMemoryOptimized must be true or false.";
            return false;
        }

        runtime = new RuntimeConfiguration(execution, durability, memoryOptimized);
        error = string.Empty;
        return true;
    }

    private static string? Value(AnalyzerConfigOptions options, string property) =>
        options.TryGetValue(property, out var configured) && !string.IsNullOrWhiteSpace(configured)
            ? configured
            : null;

    private static bool TryParseEnum<T>(string configured, out T value)
        where T : struct, Enum =>
        Enum.TryParse(configured, ignoreCase: true, out value) && Enum.IsDefined(typeof(T), value);

    private static Location FindLocation(CSharpCompilation compilation, CompilerDiagnostic diagnostic)
    {
        if (diagnostic.Line <= 0)
            return Location.None;

        var tree = compilation.SyntaxTrees.FirstOrDefault(candidate =>
            !string.IsNullOrWhiteSpace(diagnostic.FilePath) &&
            string.Equals(candidate.FilePath, diagnostic.FilePath, StringComparison.OrdinalIgnoreCase));
        tree ??= compilation.SyntaxTrees.FirstOrDefault(candidate =>
            SamePath(candidate.FilePath, diagnostic.FilePath));
        tree ??= compilation.SyntaxTrees.FirstOrDefault();
        if (tree is null)
            return Location.None;

        var text = tree.GetText();
        var lineIndex = Clamp(diagnostic.Line - 1, 0, Math.Max(0, text.Lines.Count - 1));
        var line = text.Lines[lineIndex];
        var column = Clamp(diagnostic.Column - 1, 0, line.Span.Length);
        return Location.Create(tree, new TextSpan(line.Start + column, 0));
    }

    private static bool SamePath(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            return false;
        try
        {
            return string.Equals(
                Path.GetFullPath(left),
                Path.GetFullPath(right),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static int Clamp(int value, int minimum, int maximum) =>
        Math.Min(Math.Max(value, minimum), maximum);

    private static ImmutableDictionary<string, DiagnosticDescriptor> CreateDescriptors()
    {
        string[] ids =
        [
            "SS0001", "SS0002", "SS1001", "SS2001", "SS2003", "SS2005",
            "SS2010", "SS2011", "SS2012", "SS2013",
            "SS3001", "SS3002", "SS3003", "SS3004", "SS4001", "SS4002", "SS4003",
            "SS5001", "SS6001", "SS6003", "SS6004", "SS6005", "SS6006",
            "SS6101", "SS6102", "SS6201", "SS6202", "SS6301", "SS6302",
            "SS6401", "SS6402", "SS6403", "SS6410", "SS6411",
            "SS7001", "SS7002", "SS7003", "SS7004", "SS7005", "SS7006", "SS8201", InternalErrorId
        ];

        return ids.ToImmutableDictionary(
            id => id,
            id => new DiagnosticDescriptor(
                id,
                id == InternalErrorId ? "SharpSql analyzer failure" : "SharpSql compatibility error",
                "{0}",
                "SharpSql",
                DiagnosticSeverity.Error,
                isEnabledByDefault: true,
                description: id == InternalErrorId
                    ? "The SharpSql analyzer could not inspect the compilation."
                    : "The selected SharpSql entry point cannot currently be transpiled to SQL."),
            StringComparer.Ordinal);
    }
}
