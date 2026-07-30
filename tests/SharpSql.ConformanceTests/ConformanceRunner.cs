using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using SharpSql;

namespace SharpSql.Conformance;

public enum ConformanceTestStatus
{
    Passed,
    Failed,
    Skipped
}

public sealed record ConformanceRunOptions(
    string TestsDirectory,
    int MaximumParallelism,
    TimeSpan TestTimeout);

public sealed record ConformanceTestCase(string FilePath, string RelativePath, string Category);

public sealed record ConformanceTestResult(
    string File,
    string Category,
    ConformanceTestStatus Status,
    string? Error,
    string? Message,
    long DurationMilliseconds);

public sealed record ConformanceCounts(int Passed, int Failed, int Skipped, int Total);

public sealed record ConformanceFailure(string Error, string Message);

public sealed record ConformanceReport(
    DateTimeOffset Timestamp,
    ConformanceCounts Total,
    IReadOnlyDictionary<string, ConformanceCounts> Categories,
    IReadOnlyDictionary<string, ConformanceFailure> Failures,
    IReadOnlyList<ConformanceTestResult> Results,
    int? Delta = null);

public sealed partial class ConformanceRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private static readonly string[] CategoryOrder = ["core", "generics", "dynamic"];

    public static IReadOnlyList<ConformanceTestCase> Discover(string testsDirectory)
    {
        if (!Directory.Exists(testsDirectory))
            return [];

        return Directory.EnumerateFiles(testsDirectory, "*.cs", SearchOption.AllDirectories)
            .Select(path => new
            {
                Path = path,
                RelativePath = Path.GetRelativePath(testsDirectory, path).Replace('\\', '/'),
                Category = CategoryFor(Path.GetFileName(path))
            })
            .Where(item => item.Category is not null)
            .OrderBy(item => item.RelativePath, StringComparer.Ordinal)
            .Select(item => new ConformanceTestCase(item.Path, item.RelativePath, item.Category!))
            .ToArray();
    }

    public async Task<ConformanceReport> RunAsync(
        ConformanceRunOptions options,
        CancellationToken cancellationToken = default)
    {
        if (options.MaximumParallelism <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Maximum parallelism must be positive.");
        if (options.TestTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "Test timeout must be positive.");

        var tests = Discover(options.TestsDirectory);
        var results = new ConcurrentBag<ConformanceTestResult>();
        await Parallel.ForEachAsync(
            tests,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = options.MaximumParallelism,
                CancellationToken = cancellationToken
            },
            async (test, token) => results.Add(await RunTestAsync(test, options.TestTimeout, token)));

        return CreateReport(results.OrderBy(result => result.File, StringComparer.Ordinal).ToArray());
    }

    public static ConformanceReport CreateReport(IReadOnlyList<ConformanceTestResult> results)
    {
        var categories = CategoryOrder.ToDictionary(
            category => category,
            category => Count(results.Where(result => result.Category == category)),
            StringComparer.Ordinal);
        foreach (var category in results.Select(result => result.Category).Distinct(StringComparer.Ordinal))
            categories.TryAdd(category, Count(results.Where(result => result.Category == category)));

        var failures = results
            .Where(result => result.Status == ConformanceTestStatus.Failed)
            .ToDictionary(
                result => result.File,
                result => new ConformanceFailure(
                    result.Error ?? "UNKNOWN",
                    result.Message ?? "Unknown compiler failure."),
                StringComparer.Ordinal);

        return new ConformanceReport(
            DateTimeOffset.UtcNow,
            Count(results),
            categories,
            failures,
            results);
    }

    public static async Task<ConformanceReport?> ReadReportAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
            return null;
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<ConformanceReport>(stream, JsonOptions, cancellationToken);
    }

    public static async Task WriteReportAsync(
        string path,
        ConformanceReport report,
        CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (directory is not null)
            Directory.CreateDirectory(directory);
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, report, JsonOptions, cancellationToken);
        await stream.WriteAsync("\n"u8.ToArray(), cancellationToken);
    }

    public static string FormatSummary(ConformanceReport report, ConformanceReport? baseline = null)
    {
        var lines = new List<string>();
        foreach (var category in CategoryOrder)
        {
            if (!report.Categories.TryGetValue(category, out var counts))
                continue;
            lines.Add($"{DisplayName(category)}: {counts.Passed}/{counts.Total} ({Percentage(counts)})" +
                      $"; {counts.Failed} failed, {counts.Skipped} skipped");
        }

        lines.Add($"Total: {report.Total.Passed}/{report.Total.Total} ({Percentage(report.Total)})" +
                  $"; {report.Total.Failed} failed, {report.Total.Skipped} skipped");
        if (baseline is not null)
        {
            var delta = report.Total.Passed - baseline.Total.Passed;
            lines.Add($"Delta: {delta:+#;-#;0} since last baseline");
        }

        if (report.Failures.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add("Failures:");
            lines.AddRange(report.Failures.Select(item =>
                $"  {item.Key}: {item.Value.Error}: {SingleLine(item.Value.Message)}"));
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static async Task<ConformanceTestResult> RunTestAsync(
        ConformanceTestCase test,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        string source;
        try
        {
            source = await File.ReadAllTextAsync(test.FilePath, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Result(test, ConformanceTestStatus.Failed, "IO", exception.Message, stopwatch);
        }

        if (SkipReason(test, source) is { } skipReason)
            return Result(test, ConformanceTestStatus.Skipped, "SKIPPED", skipReason, stopwatch);

        try
        {
            var transpileTask = Task.Run(() => new SharpSqlCompiler().Transpile(source), CancellationToken.None);
            var transpileResult = await transpileTask.WaitAsync(timeout, cancellationToken);
            if (transpileResult.Success)
                return Result(test, ConformanceTestStatus.Passed, null, null, stopwatch);

            var codes = string.Join(",", transpileResult.Diagnostics
                .Select(diagnostic => diagnostic.Code)
                .Distinct(StringComparer.Ordinal));
            var messages = string.Join(" | ", transpileResult.Diagnostics.Select(diagnostic => diagnostic.ToString()));
            return Result(test, ConformanceTestStatus.Failed, codes, messages, stopwatch);
        }
        catch (TimeoutException)
        {
            return Result(
                test,
                ConformanceTestStatus.Failed,
                "TIMEOUT",
                $"Compilation exceeded the {timeout.TotalSeconds:0.###} second timeout.",
                stopwatch);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Result(test, ConformanceTestStatus.Failed, "EXCEPTION", exception.Message, stopwatch);
        }
    }

    private static ConformanceTestResult Result(
        ConformanceTestCase test,
        ConformanceTestStatus status,
        string? error,
        string? message,
        Stopwatch stopwatch) =>
        new(test.RelativePath, test.Category, status, error, message, stopwatch.ElapsedMilliseconds);

    private static string? SkipReason(ConformanceTestCase test, string source)
    {
        if (test.Category == "dynamic" || DynamicKeyword().IsMatch(source))
            return "Dynamic language features are outside SharpSql's target surface.";
        if (UnsafeKeyword().IsMatch(source))
            return "Unsafe code is outside SharpSql's target surface.";
        if (ReflectionApi().IsMatch(source))
            return "Reflection is outside SharpSql's target surface.";
        if (ComInteropApi().IsMatch(source))
            return "COM/native interop is outside SharpSql's target surface.";
        if (ExternalAssemblyDirective().IsMatch(source))
            return "The test requires external assembly references.";
        return null;
    }

    private static string? CategoryFor(string fileName)
    {
        if (fileName.StartsWith("test-", StringComparison.OrdinalIgnoreCase))
            return "core";
        if (fileName.StartsWith("gtest-", StringComparison.OrdinalIgnoreCase))
            return "generics";
        if (fileName.StartsWith("dtest-", StringComparison.OrdinalIgnoreCase))
            return "dynamic";
        return null;
    }

    private static ConformanceCounts Count(IEnumerable<ConformanceTestResult> source)
    {
        var results = source.ToArray();
        return new ConformanceCounts(
            results.Count(result => result.Status == ConformanceTestStatus.Passed),
            results.Count(result => result.Status == ConformanceTestStatus.Failed),
            results.Count(result => result.Status == ConformanceTestStatus.Skipped),
            results.Length);
    }

    private static string Percentage(ConformanceCounts counts) => counts.Total == 0
        ? "0%"
        : $"{(double)counts.Passed / counts.Total * 100:0}%";

    private static string DisplayName(string category) => category switch
    {
        "core" => "Core",
        "generics" => "Generics",
        "dynamic" => "Dynamic",
        _ => category
    };

    private static string SingleLine(string value) =>
        value.Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);

    [GeneratedRegex(@"\bdynamic\b", RegexOptions.CultureInvariant)]
    private static partial Regex DynamicKeyword();

    [GeneratedRegex(@"\bunsafe\b|\bstackalloc\b", RegexOptions.CultureInvariant)]
    private static partial Regex UnsafeKeyword();

    [GeneratedRegex(@"\bSystem\.Reflection\b|\bAssembly\.(Load|Get)|\btypeof\s*\([^)]*\)\s*\.Get", RegexOptions.CultureInvariant)]
    private static partial Regex ReflectionApi();

    [GeneratedRegex(@"\bSystem\.Runtime\.InteropServices\b|\[(ComImport|DllImport)|\bMarshal\.", RegexOptions.CultureInvariant)]
    private static partial Regex ComInteropApi();

    [GeneratedRegex(@"^\s*#\s*r\s+|\bextern\s+alias\b", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex ExternalAssemblyDirective();
}
