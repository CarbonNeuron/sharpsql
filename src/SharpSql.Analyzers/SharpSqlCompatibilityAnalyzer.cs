using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace SharpSql.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class SharpSqlCompatibilityAnalyzer : DiagnosticAnalyzer
{
    public const string EnabledProperty = "build_property.SharpSqlEnableAnalyzer";
    public const string EntryPointProperty = "build_property.SharpSqlEntryPoint";
    public const string RuntimeStorageProperty = "build_property.SharpSqlRuntimeStorage";
    public const string InternalErrorId = "SSA0001";

    private static readonly ImmutableDictionary<string, DiagnosticDescriptor> Descriptors =
        CreateDescriptors();

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        [.. Descriptors.Values];

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterCompilationAction(AnalyzeCompilation);
    }

    private static void AnalyzeCompilation(CompilationAnalysisContext context)
    {
        if (context.Compilation is not CSharpCompilation compilation ||
            !AnalyzerEnabled(context.Options.AnalyzerConfigOptionsProvider.GlobalOptions) ||
            compilation.GetDiagnostics(context.CancellationToken)
                .Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
        {
            return;
        }

        var options = context.Options.AnalyzerConfigOptionsProvider.GlobalOptions;
        options.TryGetValue(EntryPointProperty, out var entryPoint);
        entryPoint = string.IsNullOrWhiteSpace(entryPoint) ? null : entryPoint;
        if (!TryGetRuntimeStorage(options, out var runtimeStorage))
        {
            context.ReportDiagnostic(Diagnostic.Create(
                Descriptors[InternalErrorId],
                Location.None,
                "SharpSqlRuntimeStorage must be Ephemeral, MemoryOptimized, Durable, or ServiceBroker."));
            return;
        }

        TranspileResult result;
        try
        {
            result = new SharpSqlCompiler().Transpile(
                compilation,
                entryPoint,
                new TranspileOptions { RuntimeStorage = runtimeStorage });
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                Descriptors[InternalErrorId],
                Location.None,
                exception.Message));
            return;
        }

        foreach (var diagnostic in result.Diagnostics.Where(item => item.Code.StartsWith("SS", StringComparison.Ordinal)))
        {
            if (!Descriptors.TryGetValue(diagnostic.Code, out var descriptor))
                descriptor = Descriptors[InternalErrorId];
            context.ReportDiagnostic(Diagnostic.Create(
                descriptor,
                FindLocation(compilation, diagnostic),
                diagnostic.Message));
        }
    }

    private static bool AnalyzerEnabled(AnalyzerConfigOptions options) =>
        !options.TryGetValue(EnabledProperty, out var configured) ||
        !bool.TryParse(configured, out var enabled) ||
        enabled;

    private static bool TryGetRuntimeStorage(
        AnalyzerConfigOptions options,
        out RuntimeStorageKind runtimeStorage)
    {
        if (!options.TryGetValue(RuntimeStorageProperty, out var configured) ||
            string.IsNullOrWhiteSpace(configured))
        {
            runtimeStorage = RuntimeStorageKind.Ephemeral;
            return true;
        }

        foreach (var name in Enum.GetNames(typeof(RuntimeStorageKind)))
        {
            if (!string.Equals(name, configured, StringComparison.OrdinalIgnoreCase))
                continue;
            runtimeStorage = (RuntimeStorageKind)Enum.Parse(typeof(RuntimeStorageKind), name);
            return true;
        }

        runtimeStorage = default;
        return false;
    }

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
            "SS7001", "SS7002", "SS7003", "SS7004", "SS7005", "SS8201", InternalErrorId
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
