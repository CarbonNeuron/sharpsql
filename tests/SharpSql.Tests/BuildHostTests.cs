using BuildProgram = SharpSql.Build.Program;
using Xunit;

namespace SharpSql.Tests;

public sealed class BuildHostTests
{
    private static string ProjectPath => Path.Combine(
        AppContext.BaseDirectory,
        "fixtures",
        "MultiFileProject",
        "MultiFileProject.csproj");

    private static string ServiceBrokerProjectPath => Path.Combine(
        AppContext.BaseDirectory,
        "fixtures",
        "ServiceBrokerProject",
        "ServiceBrokerProject.csproj");

    [Fact]
    public async Task GeneratesSqlForAnMsBuildProject()
    {
        var outputPath = Path.Combine(Path.GetTempPath(), $"sharpsql-build-{Guid.NewGuid():N}.sql");
        try
        {
            var exitCode = await BuildProgram.RunAsync(
                [
                    "--project", ProjectPath,
                    "--output", outputPath,
                    "--configuration", "Release",
                    "--framework", "net10.0",
                    "--entry", "MultiFileProject.SqlJob::Run"
                ],
                TestContext.Current.CancellationToken);

            Assert.Equal(0, exitCode);
            var sql = await File.ReadAllTextAsync(outputPath, TestContext.Current.CancellationToken);
            Assert.Contains("SET NOCOUNT ON;", sql);
            Assert.Contains("project=", sql);
        }
        finally
        {
            File.Delete(outputPath);
        }
    }

    [Fact]
    public async Task RejectsMissingRequiredArguments()
    {
        var exitCode = await BuildProgram.RunAsync(
            ["--project", ProjectPath],
            TestContext.Current.CancellationToken);

        Assert.Equal(2, exitCode);
    }

    [Fact]
    public async Task RejectsRunOperationWithoutGeneratedSql()
    {
        var exitCode = await BuildProgram.RunAsync(
            ["--operation", "run", "--project", ProjectPath],
            TestContext.Current.CancellationToken);

        Assert.Equal(2, exitCode);
    }

    [Fact]
    public async Task PassesTheSelectedRuntimeStorageToTheCompiler()
    {
        var outputPath = Path.Combine(Path.GetTempPath(), $"sharpsql-build-{Guid.NewGuid():N}.sql");
        try
        {
            var exitCode = await BuildProgram.RunAsync(
                [
                    "--project", ServiceBrokerProjectPath,
                    "--output", outputPath,
                    "--configuration", "Release",
                    "--framework", "net10.0",
                    "--entry", "ServiceBrokerProject.SqlJob::Main",
                    "--runtime-storage", "ServiceBroker"
                ],
                TestContext.Current.CancellationToken);

            Assert.Equal(0, exitCode);
            var sql = await File.ReadAllTextAsync(outputPath, TestContext.Current.CancellationToken);
            Assert.Contains("CREATE OR ALTER PROCEDURE [SharpSql].[Program_", sql);
            Assert.Contains("EXEC [SharpSql].[ScheduleTask]", sql);
        }
        finally
        {
            File.Delete(outputPath);
        }
    }

    [Fact]
    public async Task RejectsAnInvalidRuntimeStorage()
    {
        var outputPath = Path.Combine(Path.GetTempPath(), $"sharpsql-build-{Guid.NewGuid():N}.sql");

        var exitCode = await BuildProgram.RunAsync(
            [
                "--project", ProjectPath,
                "--output", outputPath,
                "--runtime-storage", "SomewhereElse"
            ],
            TestContext.Current.CancellationToken);

        Assert.Equal(2, exitCode);
        Assert.False(File.Exists(outputPath));
    }

    [Fact]
    public async Task NormalizesMsBuildDirectorySeparatorsInOutputPaths()
    {
        var outputPath = Path.Combine(Path.GetTempPath(), $"sharpsql-build-{Guid.NewGuid():N}", "output.sql");
        var foreignPath = Path.DirectorySeparatorChar == '/'
            ? outputPath.Replace('/', '\\')
            : outputPath.Replace('\\', '/');
        try
        {
            var exitCode = await BuildProgram.RunAsync(
                [
                    "--project", ProjectPath,
                    "--output", foreignPath,
                    "--configuration", "Release",
                    "--framework", "net10.0",
                    "--entry", "MultiFileProject.SqlJob::Run"
                ],
                TestContext.Current.CancellationToken);

            Assert.Equal(0, exitCode);
            Assert.True(File.Exists(outputPath));
        }
        finally
        {
            var directory = Path.GetDirectoryName(outputPath)!;
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }
}
