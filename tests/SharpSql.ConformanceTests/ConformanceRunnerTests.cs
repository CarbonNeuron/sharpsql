using Xunit;

namespace SharpSql.Conformance.Tests;

public sealed class ConformanceRunnerTests
{
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
    public async Task RunsCompilableTestsAndSkipsUnsupportedFeatures()
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(directory.Path, "test-001.cs"), "Console.WriteLine(1);");
        File.WriteAllText(
            Path.Combine(directory.Path, "dtest-001.cs"),
            "class Program { static void Main() { dynamic value = 1; } }");

        var report = await new ConformanceRunner().RunAsync(
            new ConformanceRunOptions(directory.Path, 2, TimeSpan.FromSeconds(5)),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, report.Total.Total);
        Assert.Equal(1, report.Total.Passed);
        Assert.Equal(1, report.Total.Skipped);
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

        Assert.NotNull(restored);
        Assert.Equal("SS1001", restored.Failures["test-001.cs"].Error);
        Assert.Contains("Total: 0/1 (0%)", summary);
        Assert.Contains("Delta: 0 since last baseline", summary);
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
