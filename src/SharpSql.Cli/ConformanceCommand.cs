using System.ComponentModel;
using SharpSql.Conformance;
using Spectre.Console;
using Spectre.Console.Cli;

namespace SharpSql.Cli;

/// <summary>Measures SharpSql compatibility against the Mono C# compiler test corpus.</summary>
[Description("Measure SharpSql compatibility against the Mono C# compiler test corpus.")]
public sealed class ConformanceCommand : AsyncCommand<ConformanceCommand.Settings>
{
    /// <summary>Defines the options accepted by the <c>conformance</c> command.</summary>
    public sealed class Settings : CommandSettings
    {
        [CommandOption("-p|--parallel <WORKERS>")]
        [Description("Maximum tests compiled concurrently (defaults to the available processor count).")]
        public int Parallelism { get; init; } = Math.Max(1, Environment.ProcessorCount);

        [CommandOption("--baseline")]
        [Description("Replace the saved baseline with the results of this run.")]
        public bool SaveBaseline { get; init; }

        [CommandOption("--tests <DIRECTORY>")]
        [Description("Use a specific Mono test corpus directory.")]
        public string? TestsDirectory { get; init; }

        [CommandOption("-o|--output <FILE>")]
        [Description("Detailed JSON results file (defaults to tests/conformance/results.json).")]
        public string? OutputPath { get; init; }

        [CommandOption("--timeout <SECONDS>")]
        [Description("Compilation timeout for each test.")]
        [DefaultValue(10)]
        public int TimeoutSeconds { get; init; } = 10;

        /// <inheritdoc />
        public override ValidationResult Validate()
        {
            if (Parallelism <= 0)
                return ValidationResult.Error("--parallel must be greater than zero.");
            if (TimeoutSeconds <= 0)
                return ValidationResult.Error("--timeout must be greater than zero.");
            if (string.IsNullOrWhiteSpace(TestsDirectory) && TestsDirectory is not null)
                return ValidationResult.Error("--tests cannot be empty.");
            if (string.IsNullOrWhiteSpace(OutputPath) && OutputPath is not null)
                return ValidationResult.Error("--output cannot be empty.");
            return ValidationResult.Success();
        }
    }

    protected override async Task<int> ExecuteAsync(
        CommandContext context,
        Settings settings,
        CancellationToken cancellationToken)
    {
        var environment = context.Data as CliExecutionEnvironment ??
                          new CliExecutionEnvironment(AnsiConsole.Console, Console.In, Console.Out, Console.Error);
        var output = environment.Output ?? Console.Out;
        var error = environment.Error ?? Console.Error;

        try
        {
            var repositoryRoot = MonoTestCorpus.FindRepositoryRoot(Environment.CurrentDirectory);
            var conformanceDirectory = Path.Combine(repositoryRoot, "tests", "conformance");
            var testsDirectory = settings.TestsDirectory is null
                ? Path.Combine(conformanceDirectory, "mono-tests")
                : Path.GetFullPath(settings.TestsDirectory);
            var outputPath = settings.OutputPath is null
                ? Path.Combine(conformanceDirectory, "results.json")
                : Path.GetFullPath(settings.OutputPath);
            var baselinePath = Path.Combine(conformanceDirectory, "baseline.json");

            await MonoTestCorpus.EnsureDownloadedAsync(
                repositoryRoot,
                testsDirectory,
                output,
                error,
                cancellationToken);

            var baseline = await ConformanceRunner.ReadReportAsync(baselinePath, cancellationToken);
            var runner = new ConformanceRunner();
            var report = await runner.RunAsync(
                new ConformanceRunOptions(
                    testsDirectory,
                    settings.Parallelism,
                    TimeSpan.FromSeconds(settings.TimeoutSeconds)),
                cancellationToken);
            var reportWithDelta = report with
            {
                Delta = baseline is null ? null : report.Total.Passed - baseline.Total.Passed
            };

            await ConformanceRunner.WriteReportAsync(outputPath, reportWithDelta, cancellationToken);
            await output.WriteLineAsync(ConformanceRunner.FormatSummary(reportWithDelta, baseline));
            await output.WriteLineAsync();
            await output.WriteLineAsync($"Detailed results: {outputPath}");

            if (settings.SaveBaseline)
            {
                await ConformanceRunner.WriteReportAsync(
                    baselinePath,
                    report with { Results = [] },
                    cancellationToken);
                await output.WriteLineAsync($"Baseline updated: {baselinePath}");
            }
        }
        catch (Exception exception)
        {
            await error.WriteLineAsync($"Conformance run could not complete: {exception.Message}");
        }

        return 0;
    }
}
