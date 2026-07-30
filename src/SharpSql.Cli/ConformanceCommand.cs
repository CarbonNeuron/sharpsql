using System.ComponentModel;
using SharpSql.Conformance;
using SharpSql.SqlServer;
using Spectre.Console;
using Spectre.Console.Cli;

namespace SharpSql.Cli;

/// <summary>Measures SharpSql transpilation coverage against the Mono C# compiler test corpus.</summary>
[Description("Measure SharpSql transpilation coverage (not runtime semantic conformance) against the Mono C# compiler test corpus.")]
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
        [Description("Transpilation timeout for each test; timed-out worker processes are terminated.")]
        [DefaultValue(10)]
        public int TimeoutSeconds { get; init; } = 10;

        [CommandOption("--semantic <COUNT>")]
        [Description("Execute and compare up to COUNT observable transpiled cases on CLR and SQL Server (disabled by default).")]
        public int SemanticCount { get; init; }

        [CommandOption("--image <IMAGE>")]
        [Description("SQL Server Testcontainer image used by semantic sampling.")]
        [DefaultValue("mcr.microsoft.com/mssql/server:2022-latest")]
        public string SqlServerImage { get; init; } = "mcr.microsoft.com/mssql/server:2022-latest";

        /// <inheritdoc />
        public override ValidationResult Validate()
        {
            if (Parallelism <= 0)
                return ValidationResult.Error("--parallel must be greater than zero.");
            if (TimeoutSeconds <= 0)
                return ValidationResult.Error("--timeout must be greater than zero.");
            if (SemanticCount < 0)
                return ValidationResult.Error("--semantic cannot be negative.");
            if (string.IsNullOrWhiteSpace(SqlServerImage))
                return ValidationResult.Error("--image cannot be empty.");
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
            var reportWithSemantics = settings.SemanticCount == 0
                ? report
                : await RunSemanticSampleAsync(
                    environment,
                    report,
                    testsDirectory,
                    repositoryRoot,
                    settings,
                    cancellationToken);
            var reportWithDelta = reportWithSemantics with
            {
                Delta = baseline is null ? null : reportWithSemantics.Total.Transpiled - baseline.Total.Transpiled
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
            return 1;
        }

        return 0;
    }

    private static async Task<ConformanceReport> RunSemanticSampleAsync(
        CliExecutionEnvironment environment,
        ConformanceReport report,
        string testsDirectory,
        string repositoryRoot,
        Settings settings,
        CancellationToken cancellationToken)
    {
        var cases = ConformanceRunner.Discover(testsDirectory)
            .Join(
                report.Results.Where(result => result.Status == ConformanceTestStatus.Transpiled),
                test => test.RelativePath,
                result => result.File,
                (test, _) => test,
                StringComparer.Ordinal)
            .Where(IsRuntimeObservable)
            .Take(settings.SemanticCount)
            .ToArray();
        if (cases.Length == 0)
            return report;

        var parityRunner = environment.ParityRunner ?? new TestcontainersParityRunner();
        SqlServerSession? session = null;
        string? connectionString = null;
        try
        {
            if (environment.ParityRunner is null)
            {
                session = await SqlServerSessionFactory.OpenAsync(
                    new SqlServerSessionOptions(
                        Path.Combine(repositoryRoot, "tests", "conformance", "semantic"),
                        Image: settings.SqlServerImage,
                        DatabaseName: "SharpSqlConformance"),
                    cancellationToken);
                connectionString = session.ConnectionString;
            }

            var semanticResults = new List<ConformanceSemanticResult>(cases.Length);
            foreach (var test in cases)
            {
                var source = await File.ReadAllTextAsync(test.FilePath, cancellationToken);
                var parity = await parityRunner.RunAsync(
                    new ParityRunRequest(
                        test.FilePath,
                        source,
                        EntryPoint: null,
                        Configuration: "Release",
                        TargetFramework: null,
                        settings.SqlServerImage,
                        settings.TimeoutSeconds,
                        KeepContainer: false)
                    {
                        SourcePaths = test.SourcePaths ?? [test.FilePath],
                        ConnectionString = connectionString
                    },
                    reportStage: null,
                    cancellationToken);
                semanticResults.Add(new ConformanceSemanticResult(
                    test.RelativePath,
                    parity.Matches,
                    Outcome(parity.CSharp),
                    Outcome(parity.SqlServer)));
            }

            return report with
            {
                Measurement = "transpilation+runtime-semantic-sample",
                SemanticConformanceValidated = true,
                SemanticResults = semanticResults
            };
        }
        finally
        {
            if (session is not null)
                await session.DisposeAsync();
        }
    }

    private static bool IsRuntimeObservable(ConformanceTestCase test)
    {
        var source = string.Join(
            Environment.NewLine,
            (test.SourcePaths ?? [test.FilePath]).Select(File.ReadAllText));
        return source.Contains("Console.Write", StringComparison.Ordinal) ||
               source.Contains("throw new", StringComparison.Ordinal);
    }

    private static ConformanceSemanticOutcome Outcome(ParityOutcome outcome) => new(
        outcome.StandardOutput,
        outcome.Failure?.Type,
        outcome.Failure?.Message);
}
