using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using SharpSql;

namespace SharpSql.Conformance;

public enum ConformanceTestStatus
{
    Transpiled,
    Failed,
    Skipped
}

public sealed record ConformanceRunOptions(
    string TestsDirectory,
    int MaximumParallelism,
    TimeSpan TestTimeout);

public sealed record ConformanceTestCase(
    string FilePath,
    string RelativePath,
    string Category,
    IReadOnlyList<string>? SourcePaths = null);

public sealed record ConformanceTestResult(
    string File,
    string Category,
    ConformanceTestStatus Status,
    string? Error,
    string? Message,
    long DurationMilliseconds)
{
    public int SourceFileCount { get; init; } = 1;

    public bool ClrCompilationValidated { get; init; }
}

public sealed record ConformanceCounts(int Transpiled, int Failed, int Skipped, int Total);

public sealed record ConformanceFailure(string Error, string Message);

public sealed record ConformanceSemanticOutcome(
    string StandardOutput,
    string? FailureType,
    string? FailureMessage);

public sealed record ConformanceSemanticResult(
    string File,
    bool Matches,
    ConformanceSemanticOutcome Clr,
    ConformanceSemanticOutcome SqlServer);

public sealed record ConformanceReport(
    DateTimeOffset Timestamp,
    ConformanceCounts Total,
    IReadOnlyDictionary<string, ConformanceCounts> Categories,
    IReadOnlyDictionary<string, ConformanceFailure> Failures,
    IReadOnlyList<ConformanceTestResult> Results,
    int? Delta = null)
{
    public string Measurement { get; init; } = "transpilation";

    public bool SemanticConformanceValidated { get; init; }

    public bool ClrCompilationValidated { get; init; }

    public IReadOnlyList<ConformanceSemanticResult> SemanticResults { get; init; } = [];
}

public sealed partial class ConformanceRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters =
        {
            new ConformanceTestStatusJsonConverter(),
            new ConformanceCountsJsonConverter()
        }
    };

    private static readonly string[] CategoryOrder = ["core", "generics", "dynamic"];

    private readonly Func<IReadOnlyList<string>, ProcessStartInfo> _workerStartInfoFactory;

    public ConformanceRunner()
        : this(CreateWorkerStartInfo)
    {
    }

    internal ConformanceRunner(Func<IReadOnlyList<string>, ProcessStartInfo> workerStartInfoFactory)
    {
        _workerStartInfoFactory = workerStartInfoFactory;
    }

    public static IReadOnlyList<ConformanceTestCase> Discover(string testsDirectory)
    {
        if (!Directory.Exists(testsDirectory))
            return [];

        var sources = Directory.EnumerateFiles(testsDirectory, "*.cs", SearchOption.AllDirectories)
            .Select(path => new SourceFile(
                path,
                File.ReadAllText(path)))
            .ToArray();
        var outputIndex = sources
            .SelectMany(source => OutputNames(source).Select(name => (Name: name, Source: source)))
            .GroupBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Select(item => item.Source).DistinctBy(item => item.Path, StringComparer.OrdinalIgnoreCase).ToArray(),
                StringComparer.OrdinalIgnoreCase);

        return sources
            .Select(source => new
            {
                Source = source,
                RelativePath = Path.GetRelativePath(testsDirectory, source.Path).Replace('\\', '/'),
                Category = CategoryFor(Path.GetFileName(source.Path))
            })
            .Where(item => item.Category is not null && !IsLibrary(item.Source.Text))
            .OrderBy(item => item.RelativePath, StringComparer.Ordinal)
            .Select(item => new ConformanceTestCase(
                item.Source.Path,
                item.RelativePath,
                item.Category!,
                ResolveSources(item.Source, outputIndex)))
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
                    result.Message ?? "Unknown transpilation failure."),
                StringComparer.Ordinal);

        return new ConformanceReport(
            DateTimeOffset.UtcNow,
            Count(results),
            categories,
            failures,
            results)
        {
            ClrCompilationValidated = true
        };
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
            lines.Add($"{DisplayName(category)}: {counts.Transpiled}/{counts.Total} transpiled ({Percentage(counts)})" +
                      $"; {counts.Failed} failed to transpile, {counts.Skipped} skipped");
        }

        lines.Add($"Total: {report.Total.Transpiled}/{report.Total.Total} transpiled ({Percentage(report.Total)})" +
                  $"; {report.Total.Failed} failed to transpile, {report.Total.Skipped} skipped");
        lines.Add("Scope: CLR-validated transpilation; generated SQL and runtime behavior were not semantically validated.");
        if (report.SemanticResults.Count > 0)
        {
            var matched = report.SemanticResults.Count(result => result.Matches);
            lines[^1] = $"Scope: CLR-validated transpilation plus runtime semantic sampling; {matched}/{report.SemanticResults.Count} CLR/SQL outcomes matched.";
            var mismatches = report.SemanticResults.Where(result => !result.Matches).ToArray();
            if (mismatches.Length > 0)
            {
                lines.Add(string.Empty);
                lines.Add("Runtime semantic mismatches:");
                lines.AddRange(mismatches.Select(result =>
                    $"  {result.File}: CLR={FormatSemanticOutcome(result.Clr)}; SQL={FormatSemanticOutcome(result.SqlServer)}"));
            }
        }
        if (baseline is not null)
        {
            var delta = report.Total.Transpiled - baseline.Total.Transpiled;
            lines.Add($"Transpilation delta: {delta:+#;-#;0} since last baseline");
        }

        if (report.Failures.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add("Transpilation failures:");
            lines.AddRange(report.Failures.Select(item =>
                $"  {item.Key}: {item.Value.Error}: {SingleLine(item.Value.Message)}"));
        }

        return string.Join(Environment.NewLine, lines);
    }

    private async Task<ConformanceTestResult> RunTestAsync(
        ConformanceTestCase test,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        string source;
        var sourcePaths = test.SourcePaths ?? [test.FilePath];
        try
        {
            var sourceTexts = await Task.WhenAll(sourcePaths.Select(path =>
                File.ReadAllTextAsync(path, cancellationToken)));
            source = string.Join(Environment.NewLine, sourceTexts);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Result(test, ConformanceTestStatus.Failed, "IO", exception.Message, stopwatch);
        }

        if (SkipReason(test, source) is { } skipReason)
            return Result(test, ConformanceTestStatus.Skipped, "SKIPPED", skipReason, stopwatch);

        try
        {
            var transpileResult = await RunWorkerAsync(sourcePaths, timeout, cancellationToken);
            if (!transpileResult.ClrCompiled)
            {
                var clrCodes = string.Join(",", transpileResult.Diagnostics
                    .Select(diagnostic => diagnostic.Code)
                    .Distinct(StringComparer.Ordinal));
                var clrMessages = string.Join(" | ", transpileResult.Diagnostics.Select(diagnostic => diagnostic.Message));
                return Result(test, ConformanceTestStatus.Skipped, "CLR_INVALID", $"{clrCodes}: {clrMessages}", stopwatch, true);
            }
            if (transpileResult.Transpiled)
                return Result(test, ConformanceTestStatus.Transpiled, null, null, stopwatch, true);

            var codes = string.Join(",", transpileResult.Diagnostics
                .Select(diagnostic => diagnostic.Code)
                .Distinct(StringComparer.Ordinal));
            var messages = string.Join(" | ", transpileResult.Diagnostics.Select(diagnostic => diagnostic.Message));
            return Result(test, ConformanceTestStatus.Failed, codes, messages, stopwatch, true);
        }
        catch (TimeoutException)
        {
            return Result(
                test,
                ConformanceTestStatus.Failed,
                "TIMEOUT",
                $"Transpilation exceeded the {timeout.TotalSeconds:0.###} second timeout; the isolated worker was terminated.",
                stopwatch);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Result(test, ConformanceTestStatus.Failed, "EXCEPTION", exception.Message, stopwatch);
        }
    }

    private async Task<ConformanceWorkerResult> RunWorkerAsync(
        IReadOnlyList<string> sourcePaths,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var process = new Process { StartInfo = _workerStartInfoFactory(sourcePaths) };
        process.Start();
        var standardOutput = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var standardError = process.StandardError.ReadToEndAsync(CancellationToken.None);

        try
        {
            await process.WaitForExitAsync(cancellationToken).WaitAsync(timeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            await TerminateAsync(process);
            await Task.WhenAll(standardOutput, standardError);
            throw;
        }
        catch (OperationCanceledException)
        {
            await TerminateAsync(process);
            await Task.WhenAll(standardOutput, standardError);
            throw;
        }

        var output = await standardOutput;
        var error = await standardError;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Conformance worker exited with code {process.ExitCode}: {SingleLine(error)}");
        }

        return JsonSerializer.Deserialize<ConformanceWorkerResult>(output, JsonOptions) ??
               throw new InvalidOperationException("Conformance worker returned an empty result.");
    }

    private static async Task TerminateAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) when (process.HasExited)
        {
            // The worker exited between the state check and the kill request.
        }
        await process.WaitForExitAsync(CancellationToken.None);
    }

    private static ProcessStartInfo CreateWorkerStartInfo(IReadOnlyList<string> sourcePaths)
    {
        var workerPath = Path.Combine(AppContext.BaseDirectory, "SharpSql.ConformanceWorker.dll");
        if (!File.Exists(workerPath))
            throw new FileNotFoundException("The isolated conformance worker was not deployed.", workerPath);

        var startInfo = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(workerPath);
        foreach (var sourcePath in sourcePaths)
            startInfo.ArgumentList.Add(sourcePath);
        return startInfo;
    }

    private static ConformanceTestResult Result(
        ConformanceTestCase test,
        ConformanceTestStatus status,
        string? error,
        string? message,
        Stopwatch stopwatch,
        bool clrCompilationValidated = false) =>
        new(test.RelativePath, test.Category, status, error, message, stopwatch.ElapsedMilliseconds)
        {
            SourceFileCount = (test.SourcePaths ?? [test.FilePath]).Count,
            ClrCompilationValidated = clrCompilationValidated
        };

    private static IReadOnlyList<string> ResolveSources(
        SourceFile root,
        IReadOnlyDictionary<string, SourceFile[]> outputIndex)
    {
        var resolved = new List<string> { root.Path };
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { root.Path };
        ResolveReferences(root);
        return resolved;

        void ResolveReferences(SourceFile source)
        {
            foreach (Match match in ReferenceOption().Matches(source.Text))
            {
                var reference = match.Groups[1].Value.TrimEnd(';');
                var aliasSeparator = reference.LastIndexOf('=');
                if (aliasSeparator >= 0)
                    reference = reference[(aliasSeparator + 1)..];
                var outputName = Path.GetFileName(reference);
                if (!outputIndex.TryGetValue(outputName, out var candidates) || candidates.Length != 1)
                    continue;
                var dependency = candidates[0];
                if (!visited.Add(dependency.Path))
                    continue;
                resolved.Add(dependency.Path);
                ResolveReferences(dependency);
            }
        }
    }

    private static IEnumerable<string> OutputNames(SourceFile source)
    {
        var explicitOutput = OutputOption().Match(source.Text);
        if (explicitOutput.Success)
            yield return Path.GetFileName(explicitOutput.Groups[1].Value.TrimEnd(';'));

        var extension = ModuleTarget().IsMatch(source.Text) ? ".netmodule" : ".dll";
        yield return Path.GetFileNameWithoutExtension(source.Path) + extension;
    }

    private static bool IsLibrary(string source) => LibraryTarget().IsMatch(source);

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
            results.Count(result => result.Status == ConformanceTestStatus.Transpiled),
            results.Count(result => result.Status == ConformanceTestStatus.Failed),
            results.Count(result => result.Status == ConformanceTestStatus.Skipped),
            results.Length);
    }

    private static string Percentage(ConformanceCounts counts) => counts.Total == 0
        ? "0%"
        : $"{(double)counts.Transpiled / counts.Total * 100:0}%";

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

    private static string FormatSemanticOutcome(ConformanceSemanticOutcome outcome) =>
        outcome.FailureType is null
            ? $"success/output '{SingleLine(outcome.StandardOutput)}'"
            : $"{outcome.FailureType}/output '{SingleLine(outcome.StandardOutput)}'";

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

    [GeneratedRegex(@"(?:^|\s)-(?:r|reference):([^\s]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReferenceOption();

    [GeneratedRegex(@"(?:^|\s)-(?:out):([^\s]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OutputOption();

    [GeneratedRegex(@"(?:^|\s)-(?:t|target):(?:library|module)(?:\s|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LibraryTarget();

    [GeneratedRegex(@"(?:^|\s)-(?:t|target):module(?:\s|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ModuleTarget();

    private sealed record ConformanceWorkerResult(
        bool ClrCompiled,
        bool Transpiled,
        IReadOnlyList<ConformanceWorkerDiagnostic> Diagnostics);

    private sealed record SourceFile(string Path, string Text);

    private sealed record ConformanceWorkerDiagnostic(string Code, string Message);

    private sealed class ConformanceTestStatusJsonConverter : JsonConverter<ConformanceTestStatus>
    {
        public override ConformanceTestStatus Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options) => reader.GetString() switch
            {
                "transpiled" or "passed" => ConformanceTestStatus.Transpiled,
                "failed" => ConformanceTestStatus.Failed,
                "skipped" => ConformanceTestStatus.Skipped,
                var value => throw new JsonException($"Unknown conformance test status '{value}'.")
            };

        public override void Write(
            Utf8JsonWriter writer,
            ConformanceTestStatus value,
            JsonSerializerOptions options) => writer.WriteStringValue(value switch
            {
                ConformanceTestStatus.Transpiled => "transpiled",
                ConformanceTestStatus.Failed => "failed",
                ConformanceTestStatus.Skipped => "skipped",
                _ => throw new JsonException($"Unknown conformance test status '{value}'.")
            });
    }

    private sealed class ConformanceCountsJsonConverter : JsonConverter<ConformanceCounts>
    {
        public override ConformanceCounts Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            using var document = JsonDocument.ParseValue(ref reader);
            var root = document.RootElement;
            var transpiled = root.TryGetProperty("transpiled", out var transpiledProperty)
                ? transpiledProperty.GetInt32()
                : root.GetProperty("passed").GetInt32();
            return new ConformanceCounts(
                transpiled,
                root.GetProperty("failed").GetInt32(),
                root.GetProperty("skipped").GetInt32(),
                root.GetProperty("total").GetInt32());
        }

        public override void Write(
            Utf8JsonWriter writer,
            ConformanceCounts value,
            JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteNumber("transpiled", value.Transpiled);
            writer.WriteNumber("failed", value.Failed);
            writer.WriteNumber("skipped", value.Skipped);
            writer.WriteNumber("total", value.Total);
            writer.WriteEndObject();
        }
    }
}
