using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using SharpSql.Analyzers;
using Xunit;

namespace SharpSql.Tests;

public sealed class AnalyzerTests
{
    private static string ProjectPath => Path.Combine(
        AppContext.BaseDirectory,
        "fixtures",
        "MultiFileProject",
        "MultiFileProject.csproj");

    [Fact]
    public async Task ReportsSharpSqlCompatibilityErrorsAtTheirSourceLocation()
    {
        var compilation = await CreateCompilationAsync("""
            int[,] values = new int[2, 2];
            Console.WriteLine(values.Length);
            """);

        var diagnostics = await compilation
            .WithAnalyzers([new SharpSqlCompatibilityAnalyzer()])
            .GetAnalyzerDiagnosticsAsync(TestContext.Current.CancellationToken);

        var diagnostic = Assert.Single(diagnostics, item => item.Id == "SS6301");
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Equal("AnalyzerInput.cs", Path.GetFileName(diagnostic.Location.SourceTree?.FilePath));
        Assert.Equal(1, diagnostic.Location.GetLineSpan().StartLinePosition.Line + 1);
    }

    [Fact]
    public async Task AcceptsACompatibleCompilation()
    {
        var compilation = await CreateCompilationAsync("Console.WriteLine(42);");

        var diagnostics = await compilation
            .WithAnalyzers([new SharpSqlCompatibilityAnalyzer()])
            .GetAnalyzerDiagnosticsAsync(TestContext.Current.CancellationToken);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task UsesTheConfiguredServiceBrokerRuntimeForAsyncAnalysis()
    {
        var compilation = await CreateCompilationAsync("""
            public static class AnalyzerInput
            {
                public static async Task Main()
                {
                    var values = new List<int> { 1, 2 };
                    var tasks = values.Select(Work).ToList();
                    await Task.WhenAll(tasks);
                    Console.WriteLine("done");
                }

                private static async Task<int> Work(int value)
                {
                    await Task.Delay(value);
                    return value + 1;
                }
            }
            """);
        var analyzerOptions = CreateAnalyzerOptions(new Dictionary<string, string>
        {
            [SharpSqlCompatibilityAnalyzer.RuntimeStorageProperty] = "ServiceBroker"
        });

        var diagnostics = await compilation
            .WithAnalyzers([new SharpSqlCompatibilityAnalyzer()], analyzerOptions)
            .GetAnalyzerDiagnosticsAsync(TestContext.Current.CancellationToken);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task ReportsAnInvalidRuntimeStorageConfiguration()
    {
        var compilation = await CreateCompilationAsync("Console.WriteLine(42);");
        var analyzerOptions = CreateAnalyzerOptions(new Dictionary<string, string>
        {
            [SharpSqlCompatibilityAnalyzer.RuntimeStorageProperty] = "SomewhereElse"
        });

        var diagnostics = await compilation
            .WithAnalyzers([new SharpSqlCompatibilityAnalyzer()], analyzerOptions)
            .GetAnalyzerDiagnosticsAsync(TestContext.Current.CancellationToken);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal(SharpSqlCompatibilityAnalyzer.InternalErrorId, diagnostic.Id);
        Assert.Contains("SharpSqlRuntimeStorage", diagnostic.GetMessage());
    }

    private static async Task<CSharpCompilation> CreateCompilationAsync(string source)
    {
        var loaded = await new SharpSqlProjectCompiler().LoadCompilationAsync(
            ProjectPath,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(loaded.Success, string.Join(Environment.NewLine, loaded.Diagnostics));
        var tree = CSharpSyntaxTree.ParseText(
            source,
            path: "AnalyzerInput.cs",
            cancellationToken: TestContext.Current.CancellationToken);
        var globalUsings = CSharpSyntaxTree.ParseText(
            "global using System; global using System.Collections.Generic; global using System.Linq; global using System.Threading.Tasks;",
            path: "GlobalUsings.g.cs",
            cancellationToken: TestContext.Current.CancellationToken);
        return CSharpCompilation.Create(
            $"SharpSqlAnalyzerTests_{Guid.NewGuid():N}",
            [tree, globalUsings],
            loaded.Compilation!.References,
            new CSharpCompilationOptions(OutputKind.ConsoleApplication));
    }

    private static AnalyzerOptions CreateAnalyzerOptions(IReadOnlyDictionary<string, string> values) =>
        new(
            ImmutableArray<AdditionalText>.Empty,
            new TestAnalyzerConfigOptionsProvider(values));

    private sealed class TestAnalyzerConfigOptionsProvider(
        IReadOnlyDictionary<string, string> values) : AnalyzerConfigOptionsProvider
    {
        private readonly AnalyzerConfigOptions _globalOptions = new TestAnalyzerConfigOptions(values);
        private readonly AnalyzerConfigOptions _emptyOptions = new TestAnalyzerConfigOptions(
            new Dictionary<string, string>());

        public override AnalyzerConfigOptions GlobalOptions => _globalOptions;

        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => _emptyOptions;

        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => _emptyOptions;
    }

    private sealed class TestAnalyzerConfigOptions(
        IReadOnlyDictionary<string, string> values) : AnalyzerConfigOptions
    {
        public override bool TryGetValue(string key, out string value) =>
            values.TryGetValue(key, out value!);
    }
}
