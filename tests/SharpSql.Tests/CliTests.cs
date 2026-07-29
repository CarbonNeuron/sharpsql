using SharpSql.Cli;
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
        var tester = CreateTester();

        var result = await tester.RunAsync(["--help"], TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("USAGE:", result.Output);
        Assert.Contains("--entry", result.Output);
        Assert.Contains("--framework", result.Output);
    }

    private static CommandAppTester CreateTester(string standardInput = "")
    {
        var tester = new CommandAppTester(new CommandAppTesterSettings { TrimConsoleOutput = false });
        tester.SetDefaultCommand<TranspileCommand>(
            data: new CliExecutionEnvironment(tester.Console, new StringReader(standardInput)));
        return tester;
    }
}
