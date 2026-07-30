using System.ComponentModel;
using System.Diagnostics;
using Spectre.Console;
using Spectre.Console.Cli;
using Spectre.Console.Rendering;

namespace SharpSql.Cli;

/// <summary>Compares local C# execution with generated SQL running in SQL Server.</summary>
[Description("Run C# locally and generated SQL in a SQL Server Testcontainer, then compare outcomes.")]
public sealed class VerifyCommand : AsyncCommand<VerifyCommand.Settings>
{
    /// <summary>Defines the options accepted by the <c>verify</c> command.</summary>
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "[INPUT]")]
        [Description("A .cs or .csproj input. Reads C# from standard input when omitted.")]
        public string? InputPath { get; init; }

        [CommandOption("--entry <METHOD>")]
        [Description("Project entry method in Namespace.Type::Method form.")]
        public string? EntryPoint { get; init; }

        [CommandOption("-c|--configuration <CONFIGURATION>")]
        [Description("MSBuild project configuration.")]
        [DefaultValue("Release")]
        public string Configuration { get; init; } = "Release";

        [CommandOption("-f|--framework <FRAMEWORK>")]
        [Description("Target framework to select for a multi-targeted project.")]
        public string? TargetFramework { get; init; }

        [CommandOption("--sql-output <PATH>")]
        [Description("Write the generated SQL to a file, including when verification fails.")]
        public string? SqlOutputPath { get; init; }

        [CommandOption("--image <IMAGE>")]
        [Description("SQL Server Testcontainer image.")]
        [DefaultValue("mcr.microsoft.com/mssql/server:2022-latest")]
        public string SqlServerImage { get; init; } = "mcr.microsoft.com/mssql/server:2022-latest";

        [CommandOption("--timeout <SECONDS>")]
        [Description("Generated SQL command timeout in seconds.")]
        [DefaultValue(60)]
        public int CommandTimeoutSeconds { get; init; } = 60;

        [CommandOption("--keep-container")]
        [Description("Keep and reuse the SQL Server Testcontainer across verify runs.")]
        public bool KeepContainer { get; init; }

        [CommandOption("--debug")]
        [Description("Show SQL plan and live SharpSql heap diagnostics.")]
        public bool Debug { get; init; }

        [CommandOption("--profile")]
        [Description("Warm up and repeatedly measure the C# and SQL executions.")]
        public bool Profile { get; init; }

        /// <inheritdoc />
        public override ValidationResult Validate()
        {
            var isProject = InputPath?.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) == true;
            if (string.IsNullOrWhiteSpace(InputPath) && InputPath is not null)
                return ValidationResult.Error("Input path cannot be empty.");
            if (string.IsNullOrWhiteSpace(EntryPoint) && EntryPoint is not null)
                return ValidationResult.Error("--entry cannot be empty.");
            if (string.IsNullOrWhiteSpace(Configuration))
                return ValidationResult.Error("--configuration cannot be empty.");
            if (string.IsNullOrWhiteSpace(TargetFramework) && TargetFramework is not null)
                return ValidationResult.Error("--framework cannot be empty.");
            if (!isProject && EntryPoint is not null)
                return ValidationResult.Error("--entry is supported only for .csproj inputs.");
            if (!isProject && TargetFramework is not null)
                return ValidationResult.Error("--framework is supported only for .csproj inputs.");
            if (InputPath is not null && !File.Exists(InputPath))
                return ValidationResult.Error($"Input file was not found: {InputPath}");
            if (string.IsNullOrWhiteSpace(SqlOutputPath) && SqlOutputPath is not null)
                return ValidationResult.Error("--sql-output cannot be empty.");
            if (string.IsNullOrWhiteSpace(SqlServerImage))
                return ValidationResult.Error("--image cannot be empty.");
            if (CommandTimeoutSeconds <= 0)
                return ValidationResult.Error("--timeout must be greater than zero.");
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
        var isProject = settings.InputPath?.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) == true;
        var runner = environment.ParityRunner ?? new TestcontainersParityRunner();

        ParityRunResult result;
        var totalTime = Stopwatch.StartNew();
        try
        {
            var request = await CreateRequestAsync(environment, settings, isProject, cancellationToken);
            result = await environment.Console.Progress()
                .AutoClear(false)
                .HideCompleted(false)
                .Columns(
                    new SpinnerColumn(Spinner.Known.Dots)
                    {
                        Style = new Style(Color.Yellow),
                        CompletedText = "✓",
                        CompletedStyle = new Style(Color.Green),
                        PendingText = "•",
                        PendingStyle = new Style(Color.Grey)
                    },
                    new TaskDescriptionColumn { Alignment = Justify.Left },
                    new StageDurationColumn())
                .StartAsync(async progressContext =>
                {
                    var progress = new VerificationProgress(progressContext);
                    try
                    {
                        var runResult = await runner.RunAsync(
                            request,
                            progress.Advance,
                            cancellationToken);
                        if (runResult.CSharp.Failure is { Category: ParityFailureCategory.Compilation } ||
                            runResult.SqlServer.Failure is { Category: ParityFailureCategory.Transpilation })
                            progress.Fail();
                        else
                            progress.Complete();
                        return runResult;
                    }
                    catch
                    {
                        progress.Fail();
                        throw;
                    }
                });
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            totalTime.Stop();
            environment.Console.MarkupLine("[red]✗ Verification could not run[/]");
            environment.Console.Write(new Text(exception.Message + Environment.NewLine));
            return 2;
        }
        totalTime.Stop();

        if (settings.SqlOutputPath is not null)
        {
            try
            {
                await File.WriteAllTextAsync(settings.SqlOutputPath, result.GeneratedSql, cancellationToken);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                environment.Console.MarkupLine("[red]✗ Generated SQL could not be written[/]");
                environment.Console.Write(new Text(exception.Message + Environment.NewLine));
                return 2;
            }
        }

        if (settings.KeepContainer)
            environment.Console.MarkupLine("[grey]SQL Server container kept running for reuse.[/]");

        RenderDebugDiagnostics(environment.Console, result);
        RenderProfile(environment.Console, result.Profile);

        if (result.Matches)
        {
            environment.Console.MarkupLine(
                $"[green]✓ Parity verified[/]: C# and SQL Server outcomes match. " +
                $"[blue]{FormatSqlLineCount(result.GeneratedSqlLineCount)}[/] " +
                $"[grey]({FormatDuration(totalTime.Elapsed)} total)[/]");
            return 0;
        }

        environment.Console.MarkupLine("[red]✗ Parity mismatch[/]");
        environment.Console.MarkupLine($"[blue]{FormatSqlLineCount(result.GeneratedSqlLineCount)} generated[/]");
        var report = $"C#:  {FormatOutcome(result.CSharp)}{Environment.NewLine}" +
                     $"SQL: {FormatOutcome(result.SqlServer)}{Environment.NewLine}";
        environment.Console.Write(new Text(report));
        return 1;
    }

    private static async Task<ParityRunRequest> CreateRequestAsync(
        CliExecutionEnvironment environment,
        Settings settings,
        bool isProject,
        CancellationToken cancellationToken)
    {
        var source = isProject
            ? null
            : settings.InputPath is null
                ? await environment.Input.ReadToEndAsync(cancellationToken)
                : await File.ReadAllTextAsync(settings.InputPath, cancellationToken);
        return new ParityRunRequest(
            settings.InputPath ?? "stdin.cs",
            source,
            settings.EntryPoint,
            settings.Configuration,
            settings.TargetFramework,
            settings.SqlServerImage,
            settings.CommandTimeoutSeconds,
            settings.KeepContainer,
            settings.Debug,
            settings.Profile);
    }

    private static string FormatOutcome(ParityOutcome outcome) => outcome.Failure is null
        ? $"success; stdout={Quote(outcome.StandardOutput)}"
        : $"{outcome.Failure.Category}/{outcome.Failure.Type}; stdout={Quote(outcome.StandardOutput)}; message={Quote(outcome.Failure.Message)}";

    private static string Quote(string value) => $"\"{value.Replace("\n", "\\n", StringComparison.Ordinal)}\"";

    private static string FormatSqlLineCount(int lineCount) =>
        $"{lineCount:N0} " + (lineCount == 1 ? "SQL line" : "SQL lines");

    private static string FormatDuration(TimeSpan elapsed) => elapsed.TotalSeconds < 1
        ? $"{elapsed.TotalMilliseconds:0} ms"
        : elapsed.TotalMinutes < 1
            ? $"{elapsed.TotalSeconds:0.00} s"
            : $"{(int)elapsed.TotalMinutes}:{elapsed.Seconds:00}.{elapsed.Milliseconds / 10:00}";

    private static void RenderDebugDiagnostics(IAnsiConsole console, ParityRunResult result)
    {
        if (result.DebugInfo is not { } debug)
            return;

        console.MarkupLine("[blue]Debug diagnostics[/]");
        console.MarkupLine(
            $"  Plan: [yellow]{debug.PlanOperatorCount:N0} operators[/], " +
            $"{debug.PlanStatementCount:N0} statements, maximum depth {debug.MaximumPlanDepth:N0}");
        console.MarkupLine(
            $"  Estimate: subtree cost {debug.EstimatedSubtreeCost:0.####}, " +
            $"compile {debug.CompileTimeMilliseconds:N0} ms, " +
            $"compile memory {debug.CompileMemoryKilobytes:N0} KB");
        console.MarkupLine(
            $"  Heap now: [yellow]{debug.HeapObjectsAllocated:N0} objects[/], " +
            $"{debug.IndexedItemsAllocated:N0} indexed items, " +
            $"{debug.DictionaryEntriesAllocated:N0} dictionary entries");
        console.MarkupLine(
            $"  Generated SQL: {result.GeneratedSqlLineCount:N0} lines, " +
            $"{result.GeneratedSql.Length:N0} characters");
    }

    private static void RenderProfile(IAnsiConsole console, ParityProfile? profile)
    {
        if (profile is null)
            return;

        console.MarkupLine(
            $"[blue]Profile[/] [grey]({profile.WarmupRuns} warm-up, " +
            $"{Math.Max(profile.CSharpSamples.Count, profile.SqlServerSamples.Count)} measured runs)[/]");
        RenderSamples(console, "C#", profile.CSharpSamples);
        RenderSamples(console, "SQL Server", profile.SqlServerSamples);
    }

    private static void RenderSamples(IAnsiConsole console, string label, IReadOnlyList<TimeSpan> samples)
    {
        if (samples.Count == 0)
        {
            console.MarkupLine($"  {label}: [grey]no successful samples[/]");
            return;
        }

        var ordered = samples.Order().ToArray();
        var median = ordered.Length % 2 == 1
            ? ordered[ordered.Length / 2]
            : TimeSpan.FromTicks((ordered[ordered.Length / 2 - 1].Ticks + ordered[ordered.Length / 2].Ticks) / 2);
        console.MarkupLine(
            $"  {label}: [yellow]{FormatDuration(median)} median[/] " +
            $"[grey]({FormatDuration(ordered[0])}–{FormatDuration(ordered[^1])})[/]");
    }

    private sealed class VerificationProgress
    {
        private static readonly IReadOnlyDictionary<ParityStage, string> Labels =
            new Dictionary<ParityStage, string>
            {
                [ParityStage.Parsing] = "Parsing",
                [ParityStage.SqlGenerated] = "SQL Generated",
                [ParityStage.EvaluatingCSharp] = "Evaluating C#",
                [ParityStage.StartingSqlServer] = "Starting SQL Server",
                [ParityStage.EvaluatingSqlServer] = "Evaluating SQL Server"
            };

        private readonly IReadOnlyDictionary<ParityStage, ProgressTask> _tasks;
        private ProgressTask? _active;
        private string? _activeLabel;

        /// <summary>Creates progress tasks for all parity verification stages.</summary>
        public VerificationProgress(ProgressContext context)
        {
            _tasks = Labels.ToDictionary(
                item => item.Key,
                item => context.AddTask(
                    $"[grey]{item.Value}[/]",
                    new ProgressTaskSettings { AutoStart = false, MaxValue = 100 }));
        }

        /// <summary>Advances progress to the specified verification stage.</summary>
        public void Advance(ParityStageUpdate update)
        {
            if (_active == _tasks[update.Stage])
            {
                if (update.SqlLineCount is not null)
                {
                    _activeLabel = $"SQL Generated ({update.SqlLineCount:N0} " +
                                   (update.SqlLineCount == 1 ? "line)" : "lines)");
                    _active.Description = $"[yellow]{_activeLabel}[/]";
                }
                return;
            }

            Complete();
            _active = _tasks[update.Stage];
            _activeLabel = Labels[update.Stage];
            _active.Description = $"[yellow]{_activeLabel}[/]";
            _active.IsIndeterminate = true;
            _active.StartTask();
        }

        /// <summary>Marks the active stage as complete.</summary>
        public void Complete()
        {
            if (_active is null)
                return;
            _active.IsIndeterminate = false;
            _active.Value = _active.MaxValue;
            _active.StopTask();
            _active.Description = $"[green]{_activeLabel}[/]";
            _active = null;
            _activeLabel = null;
        }

        /// <summary>Marks the active stage as failed.</summary>
        public void Fail()
        {
            if (_active is null)
                return;
            _active.IsIndeterminate = false;
            _active.StopTask();
            _active.Description = $"[red]{_activeLabel}[/]";
            _active = null;
            _activeLabel = null;
        }
    }

    private sealed class StageDurationColumn : ProgressColumn
    {
        /// <inheritdoc />
        public override IRenderable Render(
            RenderOptions options,
            ProgressTask task,
            TimeSpan deltaTime)
        {
            if (!task.IsStarted || task.ElapsedTime is not { } elapsed)
                return Text.Empty;

            return new Markup($"[grey]{FormatDuration(elapsed)}[/]");
        }
    }
}
