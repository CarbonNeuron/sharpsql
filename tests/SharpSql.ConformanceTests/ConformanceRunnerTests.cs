using System.Diagnostics;
using Xunit;

namespace SharpSql.Conformance.Tests;

public sealed class ConformanceRunnerTests
{
    private static readonly TimeSpan WorkerTimeout = TimeSpan.FromSeconds(15);

    [Fact]
    public void DiscoversAndCategorizesOnlyMonoTestFiles()
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(directory.Path, "test-001.cs"), "Console.WriteLine(1);");
        File.WriteAllText(Path.Combine(directory.Path, "gtest-002.cs"), "Console.WriteLine(2);");
        File.WriteAllText(Path.Combine(directory.Path, "dtest-003.cs"), "Console.WriteLine(3);");
        File.WriteAllText(Path.Combine(directory.Path, "helper.cs"), "internal class Helper { }");

        var tests = ConformanceRunner.Discover(directory.Path);

        Assert.Equal(3, tests.Count);
        Assert.Collection(
            tests,
            test => Assert.Equal("dynamic", test.Category),
            test => Assert.Equal("generics", test.Category),
            test => Assert.Equal("core", test.Category));
    }

    [Fact]
    public void ExcludesLibraryProjectsAndAttachesReferencedSources()
    {
        using var directory = new TemporaryDirectory();
        var mainPath = Path.Combine(directory.Path, "test-001.cs");
        var libraryPath = Path.Combine(directory.Path, "test-001-lib.cs");
        File.WriteAllText(mainPath, "// Compiler options: -r:test-001-lib.dll\nConsole.WriteLine(Helper.Value());");
        File.WriteAllText(libraryPath, "// Compiler options: -t:library\nstatic class Helper { public static int Value() => 1; }");

        var test = Assert.Single(ConformanceRunner.Discover(directory.Path));

        Assert.Equal(mainPath, test.FilePath);
        Assert.Equal([mainPath, libraryPath], test.SourcePaths);
    }

    [Fact]
    public async Task RunsCompilableTestsAndSkipsUnsupportedFeatures()
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(directory.Path, "test-001.cs"), "Console.WriteLine(1);");
        File.WriteAllText(
            Path.Combine(directory.Path, "dtest-001.cs"),
            "class Program { static void Main() { dynamic value = 1; } }");

        var report = await new ConformanceRunner().RunAsync(
            new ConformanceRunOptions(directory.Path, 2, WorkerTimeout),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, report.Total.Total);
        Assert.Equal(1, report.Total.Transpiled);
        Assert.Equal(1, report.Total.Skipped);
        Assert.Empty(report.Failures);
    }

    [Fact]
    public async Task ValidatesAndTranspilesAReferencedSourceSet()
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllText(
            Path.Combine(directory.Path, "test-001.cs"),
            "// Compiler options: -r:test-001-lib.dll\nConsole.WriteLine(Helper.Value());");
        File.WriteAllText(
            Path.Combine(directory.Path, "test-001-lib.cs"),
            "// Compiler options: -t:library\nstatic class Helper { public static int Value() => 1; }");

        var report = await new ConformanceRunner().RunAsync(
            new ConformanceRunOptions(directory.Path, 1, WorkerTimeout),
            TestContext.Current.CancellationToken);

        var result = Assert.Single(report.Results);
        Assert.True(
            result.Status == ConformanceTestStatus.Transpiled,
            $"Expected transpilation, got {result.Error}: {result.Message}");
        Assert.Equal(2, result.SourceFileCount);
        Assert.True(result.ClrCompilationValidated);
    }

    [Fact]
    public async Task TreatsClrInvalidCorpusInputsAsSkippedInsteadOfSharpSqlFailures()
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(directory.Path, "test-001.cs"), "Console.WriteLine(Missing.Value);");

        var report = await new ConformanceRunner().RunAsync(
            new ConformanceRunOptions(directory.Path, 1, WorkerTimeout),
            TestContext.Current.CancellationToken);

        var result = Assert.Single(report.Results);
        Assert.Equal(ConformanceTestStatus.Skipped, result.Status);
        Assert.Equal("CLR_INVALID", result.Error);
        Assert.True(result.ClrCompilationValidated);
        Assert.Empty(report.Failures);
    }

    [Fact]
    public async Task WritesDetailedJsonAndCalculatesSummaryDelta()
    {
        using var directory = new TemporaryDirectory();
        var resultPath = Path.Combine(directory.Path, "results.json");
        var result = new ConformanceTestResult(
            "test-001.cs",
            "core",
            ConformanceTestStatus.Failed,
            "SS1001",
            "Unsupported feature",
            12);
        var report = ConformanceRunner.CreateReport([result]);
        var baseline = ConformanceRunner.CreateReport([]);

        await ConformanceRunner.WriteReportAsync(
            resultPath,
            report,
            TestContext.Current.CancellationToken);
        var restored = await ConformanceRunner.ReadReportAsync(
            resultPath,
            TestContext.Current.CancellationToken);
        var summary = ConformanceRunner.FormatSummary(report, baseline);
        var json = await File.ReadAllTextAsync(resultPath, TestContext.Current.CancellationToken);

        Assert.NotNull(restored);
        Assert.Equal("SS1001", restored.Failures["test-001.cs"].Error);
        Assert.Contains("\"transpiled\": 0", json);
        Assert.DoesNotContain("\"passed\"", json);
        Assert.Contains("Total: 0/1 transpiled (0%)", summary);
        Assert.Contains("generated SQL and runtime behavior were not semantically validated", summary);
        Assert.Contains("Transpilation delta: 0 since last baseline", summary);
        Assert.Equal("transpilation", restored.Measurement);
        Assert.True(restored.ClrCompilationValidated);
        Assert.False(restored.SemanticConformanceValidated);
    }

    [Fact]
    public async Task WritesRuntimeSemanticSampleSeparatelyFromTranspilationCounts()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "semantic.json");
        var report = ConformanceRunner.CreateReport([]) with
        {
            Measurement = "transpilation+runtime-semantic-sample",
            SemanticConformanceValidated = true,
            SemanticResults =
            [
                new ConformanceSemanticResult(
                    "test-001.cs",
                    true,
                    new ConformanceSemanticOutcome("42", null, null),
                    new ConformanceSemanticOutcome("42", null, null)),
                new ConformanceSemanticResult(
                    "test-002.cs",
                    false,
                    new ConformanceSemanticOutcome("left", null, null),
                    new ConformanceSemanticOutcome("right", null, null))
            ]
        };

        await ConformanceRunner.WriteReportAsync(path, report, TestContext.Current.CancellationToken);
        var restored = await ConformanceRunner.ReadReportAsync(path, TestContext.Current.CancellationToken);
        var summary = ConformanceRunner.FormatSummary(report);

        Assert.NotNull(restored);
        Assert.True(restored.SemanticConformanceValidated);
        Assert.Equal(2, restored.SemanticResults.Count);
        Assert.Contains("1/2 CLR/SQL outcomes matched", summary);
        Assert.Contains("Runtime semantic mismatches", summary);
        Assert.Contains("test-002.cs", summary);
    }

    [Fact]
    public async Task ReadsLegacyPassedTerminologyAsTranspiled()
    {
        using var directory = new TemporaryDirectory();
        var resultPath = Path.Combine(directory.Path, "legacy.json");
        await File.WriteAllTextAsync(
            resultPath,
            """
            {
              "timestamp": "2026-07-29T00:00:00Z",
              "total": { "passed": 1, "failed": 0, "skipped": 0, "total": 1 },
              "categories": {
                "core": { "passed": 1, "failed": 0, "skipped": 0, "total": 1 }
              },
              "failures": {},
              "results": [
                {
                  "file": "test-001.cs",
                  "category": "core",
                  "status": "passed",
                  "error": null,
                  "message": null,
                  "durationMilliseconds": 1
                }
              ]
            }
            """,
            TestContext.Current.CancellationToken);

        var report = await ConformanceRunner.ReadReportAsync(
            resultPath,
            TestContext.Current.CancellationToken);

        Assert.NotNull(report);
        Assert.Equal(1, report.Total.Transpiled);
        Assert.False(report.ClrCompilationValidated);
        Assert.Equal(ConformanceTestStatus.Transpiled, Assert.Single(report.Results).Status);
    }

    [Fact]
    public async Task TimeoutTerminatesTheIsolatedWorker()
    {
        using var directory = new TemporaryDirectory();
        var sourcePath = Path.Combine(directory.Path, "test-timeout.cs");
        var startedPath = Path.Combine(directory.Path, "started.txt");
        await File.WriteAllTextAsync(
            sourcePath,
            "Console.WriteLine(1);",
            TestContext.Current.CancellationToken);
        var runner = new ConformanceRunner(paths => DelayedWorkerStartInfo(paths, startedPath));

        var report = await runner.RunAsync(
            new ConformanceRunOptions(directory.Path, 1, TimeSpan.FromSeconds(2)),
            TestContext.Current.CancellationToken);

        var result = Assert.Single(report.Results);
        Assert.Equal(ConformanceTestStatus.Failed, result.Status);
        Assert.Equal("TIMEOUT", result.Error);
        Assert.Contains("worker was terminated", result.Message);
        var processId = int.Parse(await File.ReadAllTextAsync(
            startedPath,
            TestContext.Current.CancellationToken));
        Assert.Throws<ArgumentException>(() => Process.GetProcessById(processId));
    }

    private static ProcessStartInfo DelayedWorkerStartInfo(IReadOnlyList<string> sourcePaths, string startedPath)
    {
        var workerPath = Path.Combine(AppContext.BaseDirectory, "SharpSql.ConformanceWorker.dll");
        var startInfo = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(workerPath);
        startInfo.ArgumentList.Add("--delay");
        startInfo.ArgumentList.Add(TimeSpan.FromMinutes(1).TotalMilliseconds.ToString("0"));
        startInfo.ArgumentList.Add("--started");
        startInfo.ArgumentList.Add(startedPath);
        foreach (var sourcePath in sourcePaths)
            startInfo.ArgumentList.Add(sourcePath);
        return startInfo;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"sharpsql-conformance-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
