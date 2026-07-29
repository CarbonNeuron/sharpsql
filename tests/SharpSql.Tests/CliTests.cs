using SharpSql.Cli;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Spectre.Console.Cli.Testing;
using Xunit;

namespace SharpSql.Tests;

public sealed class CliTests
{
    private static string ProjectPath => Path.Combine(
        AppContext.BaseDirectory,
        "fixtures",
        "MultiFileProject",
        "MultiFileProject.csproj");

    [Fact]
    public async Task CompilesStandardInputThroughTheDefaultCommand()
    {
        var tester = CreateTester("Console.WriteLine(\"from stdin\");");

        var result = await tester.RunAsync([], TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("SET NOCOUNT ON;", result.Output);
        Assert.Contains("PRINT N'from stdin';", result.Output);
    }

    [Fact]
    public async Task BindsProjectOptionsAndCompilesTheSelectedEntryPoint()
    {
        var tester = CreateTester();

        var result = await tester.RunAsync(
            [ProjectPath, "--entry", "MultiFileProject.SqlJob::Run", "--configuration", "Release", "--framework", "net10.0"],
            TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("21 * 2", result.Output);
        var settings = Assert.IsType<TranspileCommand.Settings>(result.Settings);
        Assert.Equal("MultiFileProject.SqlJob::Run", settings.EntryPoint);
        Assert.Equal("Release", settings.Configuration);
        Assert.Equal("net10.0", settings.TargetFramework);
    }

    [Fact]
    public async Task ExecutesQueryableProjectsWithRuntimeAssemblies()
    {
        var loaded = await new SharpSqlProjectCompiler().LoadCompilationAsync(
            ProjectPath,
            new ProjectTranspileOptions
            {
                EntryPoint = "MultiFileProject.SqlJob::Run",
                Configuration = "Release",
                TargetFramework = "net10.0"
            },
            TestContext.Current.CancellationToken);

        Assert.True(loaded.Success, string.Join(Environment.NewLine, loaded.Diagnostics));
        var source = CSharpSyntaxTree.ParseText("""
            using System;
            using System.Collections.Generic;
            using System.Linq;

            IQueryable<int> values = new List<int> { 42 }.AsQueryable();
            Console.WriteLine($"project={values.Single()}");
            """, cancellationToken: TestContext.Current.CancellationToken);
        var compilation = CSharpCompilation.Create(
            $"SharpSqlQueryableRuntime_{Guid.NewGuid():N}",
            [source],
            loaded.Compilation!.References,
            new CSharpCompilationOptions(OutputKind.ConsoleApplication));
        var outcome = await TestcontainersParityRunner.ExecuteProjectCSharpForTestingAsync(
            compilation,
            ProjectPath,
            requestedEntryPoint: null);

        Assert.Null(outcome.Failure);
        Assert.Equal("project=42", outcome.StandardOutput);
    }

    [Fact]
    public async Task ReportsSpectreValidationErrorsForProjectOnlyOptions()
    {
        var tester = CreateTester();

        var result = await tester.RunAsync(
            ["script.cs", "--entry", "Example.Job::Run"],
            TestContext.Current.CancellationToken);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("--entry is supported only for .csproj inputs", result.Output);
    }

    [Fact]
    public async Task WritesSqlToTheRequestedOutputFile()
    {
        var tester = CreateTester("Console.WriteLine(42);");
        var outputPath = Path.Combine(Path.GetTempPath(), $"sharpsql-{Guid.NewGuid():N}.sql");
        try
        {
            var result = await tester.RunAsync(
                ["--output", outputPath],
                TestContext.Current.CancellationToken);

            Assert.Equal(0, result.ExitCode);
            Assert.True(File.Exists(outputPath));
            Assert.Contains("PRINT", await File.ReadAllTextAsync(outputPath, TestContext.Current.CancellationToken));
        }
        finally
        {
            File.Delete(outputPath);
        }
    }

    [Fact]
    public async Task RendersGeneratedSpectreHelp()
    {
        var tester = CreateRoutedTester();

        var result = await tester.RunAsync(
            CliArgumentRouter.Route(["--help"]),
            TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("USAGE:", result.Output);
        Assert.Contains("transpile", result.Output);
        Assert.Contains("verify", result.Output);
    }

    [Fact]
    public async Task VerifyCommandReportsMatchingOutcomesAndCanSaveSql()
    {
        var parityResult = new ParityRunResult(
            new ParityOutcome("same", null),
            new ParityOutcome("same", null),
            "PRINT N'same';");
        var tester = CreateTester("Console.WriteLine(\"same\");", new StubParityRunner(parityResult));
        var outputPath = Path.Combine(Path.GetTempPath(), $"sharpsql-verify-{Guid.NewGuid():N}.sql");
        try
        {
            var result = await tester.RunAsync(
                ["verify", "--sql-output", outputPath],
                TestContext.Current.CancellationToken);

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("Parity verified", result.Output);
            Assert.Contains("1 SQL line", result.Output);
            Assert.Equal("PRINT N'same';", await File.ReadAllTextAsync(outputPath, TestContext.Current.CancellationToken));
        }
        finally
        {
            File.Delete(outputPath);
        }
    }

    [Fact]
    public async Task VerifyCommandReportsAParityMismatch()
    {
        var parityResult = new ParityRunResult(
            new ParityOutcome("local", null),
            new ParityOutcome("sql", null),
            string.Empty);
        var tester = CreateTester("Console.WriteLine(\"local\");", new StubParityRunner(parityResult));

        var result = await tester.RunAsync(["verify"], TestContext.Current.CancellationToken);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Parity mismatch", result.Output);
        Assert.Contains("stdout=\"local\"", result.Output);
        Assert.Contains("stdout=\"sql\"", result.Output);
    }

    [Fact]
    public async Task RoutesVerifyBeforeItsInputPath()
    {
        var parityResult = new ParityRunResult(
            new ParityOutcome("same", null),
            new ParityOutcome("same", null),
            string.Empty);
        var inputPath = Path.Combine(Path.GetTempPath(), $"sharpsql-verify-{Guid.NewGuid():N}.cs");
        await File.WriteAllTextAsync(
            inputPath,
            "Console.WriteLine(\"same\");",
            TestContext.Current.CancellationToken);
        try
        {
            var tester = CreateRoutedTester(new StubParityRunner(parityResult));

            var result = await tester.RunAsync(
                CliArgumentRouter.Route(["verify", inputPath]),
                TestContext.Current.CancellationToken);

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("Parity verified", result.Output);
            var settings = Assert.IsType<VerifyCommand.Settings>(result.Settings);
            Assert.Equal(inputPath, settings.InputPath);
        }
        finally
        {
            File.Delete(inputPath);
        }
    }

    [Fact]
    public async Task VerifyCommandPassesProjectOptionsToTheParityRunner()
    {
        var parityResult = new ParityRunResult(
            new ParityOutcome("same", null),
            new ParityOutcome("same", null),
            "PRINT N'same';");
        var runner = new StubParityRunner(parityResult);
        var tester = CreateRoutedTester(runner);

        var result = await tester.RunAsync(
            CliArgumentRouter.Route([
                "verify",
                ProjectPath,
                "--entry", "MultiFileProject.SqlJob::Run",
                "--configuration", "Debug",
                "--framework", "net10.0",
                "--keep-container",
                "--debug",
                "--profile"
            ]),
            TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("container kept running for reuse", result.Output);
        var request = Assert.IsType<ParityRunRequest>(runner.LastRequest);
        Assert.True(request.IsProject);
        Assert.Null(request.Source);
        Assert.Equal(ProjectPath, request.InputPath);
        Assert.Equal("MultiFileProject.SqlJob::Run", request.EntryPoint);
        Assert.Equal("Debug", request.Configuration);
        Assert.Equal("net10.0", request.TargetFramework);
        Assert.True(request.KeepContainer);
        Assert.True(request.Debug);
        Assert.True(request.Profile);
    }

    [Fact]
    public async Task VerifyCommandRendersDebugAndProfileDiagnostics()
    {
        var parityResult = new ParityRunResult(
            new ParityOutcome("same", null),
            new ParityOutcome("same", null),
            "PRINT N'same';",
            new ParityDebugInfo(
                PlanStatementCount: 4,
                PlanOperatorCount: 12,
                MaximumPlanDepth: 3,
                EstimatedSubtreeCost: 0.125,
                CompileTimeMilliseconds: 2,
                CompileMemoryKilobytes: 128,
                HeapObjectsAllocated: 7,
                IndexedItemsAllocated: 15,
                DictionaryEntriesAllocated: 2),
            new ParityProfile(
                WarmupRuns: 1,
                CSharpSamples: [TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(2), TimeSpan.FromMilliseconds(3)],
                SqlServerSamples: [TimeSpan.FromMilliseconds(4), TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(6)]));
        var tester = CreateTester("Console.WriteLine(\"same\");", new StubParityRunner(parityResult));

        var result = await tester.RunAsync(
            ["verify", "--debug", "--profile"],
            TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Debug diagnostics", result.Output);
        Assert.Contains("12 operators", result.Output);
        Assert.Contains("7 objects", result.Output);
        Assert.Contains("Profile", result.Output);
        Assert.Contains("C#: 2 ms median", result.Output);
        Assert.Contains("SQL Server: 5 ms median", result.Output);
    }

    [Fact]
    public void RoutesLegacyArgumentsToTheTranspileCommand()
    {
        Assert.Equal(["transpile", "Program.cs", "--output", "Program.sql"],
            CliArgumentRouter.Route(["Program.cs", "--output", "Program.sql"]));
        Assert.Equal(["verify", "Program.cs"], CliArgumentRouter.Route(["verify", "Program.cs"]));
    }

    [Theory]
    [InlineData("", 0)]
    [InlineData("one", 1)]
    [InlineData("one\n", 1)]
    [InlineData("one\ntwo", 2)]
    [InlineData("one\ntwo\n", 2)]
    public void CountsGeneratedSqlLines(string sql, int expected)
    {
        Assert.Equal(expected, ParityRunResult.CountLines(sql));
    }

    private static CommandAppTester CreateTester(string standardInput = "", IParityRunner? parityRunner = null)
    {
        var tester = new CommandAppTester(new CommandAppTesterSettings { TrimConsoleOutput = false });
        var environment = new CliExecutionEnvironment(
            tester.Console,
            new StringReader(standardInput),
            ParityRunner: parityRunner);
        tester.SetDefaultCommand<TranspileCommand>(
            data: environment);
        tester.Configure(configurator => configurator.AddCommand<VerifyCommand>("verify").WithData(environment));
        return tester;
    }

    private static CommandAppTester CreateRoutedTester(IParityRunner? parityRunner = null)
    {
        var tester = new CommandAppTester(new CommandAppTesterSettings { TrimConsoleOutput = false });
        var environment = new CliExecutionEnvironment(
            tester.Console,
            new StringReader(string.Empty),
            ParityRunner: parityRunner);
        tester.Configure(configurator =>
        {
            configurator.AddCommand<TranspileCommand>("transpile").WithData(environment);
            configurator.AddCommand<VerifyCommand>("verify").WithData(environment);
        });
        return tester;
    }

    private sealed class StubParityRunner(ParityRunResult result) : IParityRunner
    {
        public ParityRunRequest? LastRequest { get; private set; }

        public Task<ParityRunResult> RunAsync(
            ParityRunRequest request,
            Action<ParityStageUpdate>? reportStage,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            reportStage?.Invoke(new ParityStageUpdate(ParityStage.Parsing));
            reportStage?.Invoke(new ParityStageUpdate(ParityStage.SqlGenerated));
            reportStage?.Invoke(new ParityStageUpdate(ParityStage.SqlGenerated, result.GeneratedSqlLineCount));
            reportStage?.Invoke(new ParityStageUpdate(ParityStage.EvaluatingCSharp));
            reportStage?.Invoke(new ParityStageUpdate(ParityStage.StartingSqlServer));
            reportStage?.Invoke(new ParityStageUpdate(ParityStage.EvaluatingSqlServer));
            return Task.FromResult(result);
        }
    }
}
