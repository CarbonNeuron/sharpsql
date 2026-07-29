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
            "global using System; global using System.Collections.Generic; global using System.Linq;",
            path: "GlobalUsings.g.cs",
            cancellationToken: TestContext.Current.CancellationToken);
        return CSharpCompilation.Create(
            $"SharpSqlAnalyzerTests_{Guid.NewGuid():N}",
            [tree, globalUsings],
            loaded.Compilation!.References,
            new CSharpCompilationOptions(OutputKind.ConsoleApplication));
    }
}
