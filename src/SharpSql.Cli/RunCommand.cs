using System.ComponentModel;
using System.Xml.Linq;
using Spectre.Console;
using Spectre.Console.Cli;

namespace SharpSql.Cli;

[Description("Transpile and execute a project in SQL Server, using a configured connection or Testcontainers.")]
public sealed class RunCommand : AsyncCommand<RunCommand.Settings>
{
    internal const string DefaultImage = "mcr.microsoft.com/mssql/server:2022-latest";
    internal const string DefaultDatabase = "SharpSql";

    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "[INPUT]")]
        [Description("A .csproj, project directory, or generated .sql file. Uses the current directory when omitted.")]
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

        [CommandOption("--connection <NAME>")]
        [Description("Connection string name from environment, user secrets, or appsettings.")]
        public string? ConnectionName { get; init; }

        [CommandOption("--connection-string-env <VARIABLE>")]
        [Description("Environment variable containing the connection string.")]
        public string? ConnectionStringEnvironmentVariable { get; init; }

        [CommandOption("--container")]
        [Description("Force a SQL Server Testcontainer even when a connection is configured.")]
        public bool ForceContainer { get; init; }

        [CommandOption("--keep-container")]
        [Description("Keep and reuse the SQL Server Testcontainer.")]
        public bool KeepContainer { get; init; }

        [CommandOption("--remove-container")]
        [Description("Remove the Testcontainer after execution, overriding project configuration.")]
        public bool RemoveContainer { get; init; }

        [CommandOption("--image <IMAGE>")]
        [Description("SQL Server Testcontainer image.")]
        public string? SqlServerImage { get; init; }

        [CommandOption("--database <DATABASE>")]
        [Description("Database created and selected inside a Testcontainer.")]
        public string? DatabaseName { get; init; }

        [CommandOption("--timeout <SECONDS>")]
        [Description("SQL command timeout in seconds.")]
        public int? CommandTimeoutSeconds { get; init; }

        [CommandOption("--runtime-storage <MODE>")]
        [Description("Runtime state mode: Ephemeral (default), Durable, or ServiceBroker.")]
        [DefaultValue(RuntimeStorageKind.Ephemeral)]
        public RuntimeStorageKind RuntimeStorage { get; init; } = RuntimeStorageKind.Ephemeral;

        [CommandOption("-o|--output <OUTPUT>")]
        [Description("Write the generated program SQL to a file before reporting the result.")]
        public string? OutputPath { get; init; }

        [CommandOption("--installer-output <OUTPUT>")]
        [Description("Write the standalone Service Broker installer SQL to this file.")]
        public string? InstallerOutputPath { get; init; }

        [CommandOption("--debug")]
        [Description("Show SQL plan and live SharpSql heap diagnostics.")]
        public bool Debug { get; init; }

        [CommandOption("--profile")]
        [Description("Warm up and repeatedly measure SQL execution.")]
        public bool Profile { get; init; }

        public override ValidationResult Validate()
        {
            if (KeepContainer && RemoveContainer)
                return ValidationResult.Error("--keep-container and --remove-container cannot be combined.");
            if (string.IsNullOrWhiteSpace(ConnectionName) && ConnectionName is not null)
                return ValidationResult.Error("--connection cannot be empty.");
            if (string.IsNullOrWhiteSpace(ConnectionStringEnvironmentVariable) &&
                ConnectionStringEnvironmentVariable is not null)
                return ValidationResult.Error("--connection-string-env cannot be empty.");
            if (string.IsNullOrWhiteSpace(SqlServerImage) && SqlServerImage is not null)
                return ValidationResult.Error("--image cannot be empty.");
            if (string.IsNullOrWhiteSpace(DatabaseName) && DatabaseName is not null)
                return ValidationResult.Error("--database cannot be empty.");
            if (CommandTimeoutSeconds <= 0)
                return ValidationResult.Error("--timeout must be greater than zero.");
            if (string.IsNullOrWhiteSpace(OutputPath) && OutputPath is not null)
                return ValidationResult.Error("--output cannot be empty.");
            if (string.IsNullOrWhiteSpace(InstallerOutputPath) && InstallerOutputPath is not null)
                return ValidationResult.Error("--installer-output cannot be empty.");
            if (InstallerOutputPath is not null && RuntimeStorage != RuntimeStorageKind.ServiceBroker)
                return ValidationResult.Error("--installer-output requires --runtime-storage ServiceBroker.");
            if (OutputPath is not null && InstallerOutputPath is not null &&
                string.Equals(
                    Path.GetFullPath(OutputPath),
                    Path.GetFullPath(InstallerOutputPath),
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                return ValidationResult.Error("--output and --installer-output must use different files.");
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
        string inputPath;
        try
        {
            inputPath = ResolveInput(settings.InputPath);
        }
        catch (ProjectInitializationException exception)
        {
            environment.Console.MarkupLine($"[red]{Markup.Escape(exception.Message)}[/]");
            return 1;
        }

        var isProject = inputPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase);
        if (!isProject && !inputPath.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
        {
            environment.Console.MarkupLine("[red]Run input must be a .csproj project or generated .sql file.[/]");
            return 1;
        }
        if (!isProject && (settings.EntryPoint is not null || settings.TargetFramework is not null))
        {
            environment.Console.MarkupLine("[red]--entry and --framework are supported only for .csproj inputs.[/]");
            return 1;
        }

        var projectSettings = isProject
            ? SharpSqlRunProjectSettings.Load(inputPath)
            : SharpSqlRunProjectSettings.Default;
        var artifactPaths = SqlOutputArtifacts.ResolvePaths(
            settings.OutputPath,
            settings.InstallerOutputPath,
            settings.RuntimeStorage);
        var keepContainer = settings.KeepContainer ||
                            (!settings.RemoveContainer && projectSettings.KeepContainer);
        var request = new SqlRunRequest(
            inputPath,
            isProject ? null : await File.ReadAllTextAsync(inputPath, cancellationToken),
            settings.EntryPoint ?? projectSettings.EntryPoint,
            settings.Configuration,
            settings.TargetFramework,
            settings.ConnectionName ?? projectSettings.ConnectionName,
            settings.ConnectionStringEnvironmentVariable ?? projectSettings.ConnectionStringEnvironmentVariable,
            settings.ForceContainer,
            keepContainer,
            settings.SqlServerImage ?? projectSettings.SqlServerImage,
            settings.DatabaseName ?? projectSettings.DatabaseName,
            settings.CommandTimeoutSeconds ?? projectSettings.CommandTimeoutSeconds,
            settings.RuntimeStorage,
            settings.Debug,
            settings.Profile,
            artifactPaths.ProgramPath,
            artifactPaths.InstallerPath);

        SqlRunResult result;
        try
        {
            result = await (environment.SqlRunService ?? new SqlRunService())
                .RunAsync(
                    request,
                    message => environment.Console.WriteLine(message),
                    cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            environment.Console.MarkupLine("[red]✗ SQL execution could not start[/]");
            environment.Console.Write(new Text(exception.Message + Environment.NewLine));
            return 2;
        }

        foreach (var diagnostic in result.Diagnostics)
            environment.Console.WriteLine(diagnostic.ToString());
        RenderDebugDiagnostics(environment.Console, result);
        RenderProfile(environment.Console, result.Profile);
        RenderArtifactPaths(environment.Console, artifactPaths, result);
        if (!result.Success)
        {
            if (result.ErrorMessage is not null)
            {
                var code = result.ErrorNumber is null ? string.Empty : $" {result.ErrorNumber}";
                environment.Console.MarkupLine($"[red]✗ SQL Server error{code}[/]");
                environment.Console.Write(new Text(result.ErrorMessage + Environment.NewLine));
            }
            return 1;
        }

        environment.Console.MarkupLine($"[green]✓ SQL executed[/] on [blue]{Markup.Escape(result.SqlServer)}[/]");
        if (result.ContainerKept)
            environment.Console.MarkupLine("[grey]SQL Server container kept running for reuse.[/]");
        return 0;
    }

    private static void RenderArtifactPaths(
        IAnsiConsole console,
        SqlOutputArtifactPaths artifactPaths,
        SqlRunResult result)
    {
        if (artifactPaths.ProgramPath is not null && result.GeneratedSql is not null)
        {
            console.MarkupLine(
                $"[grey]Program SQL: {Markup.Escape(Path.GetFullPath(artifactPaths.ProgramPath))}[/]");
        }
        if (artifactPaths.InstallerPath is not null && result.InstallerSql is not null)
        {
            console.MarkupLine(
                $"[grey]Installer SQL: {Markup.Escape(Path.GetFullPath(artifactPaths.InstallerPath))}[/]");
        }
    }

    private static string ResolveInput(string? requestedPath)
    {
        if (requestedPath is null || Directory.Exists(requestedPath))
            return ProjectSdkInstaller.ResolveProject(requestedPath, Environment.CurrentDirectory);
        var path = Path.GetFullPath(requestedPath);
        if (!File.Exists(path))
            throw new ProjectInitializationException($"Input file was not found: {path}");
        return path;
    }

    private static void RenderDebugDiagnostics(IAnsiConsole console, SqlRunResult result)
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
        if (debug.HeapDiagnosticsObserved)
        {
            console.MarkupLine(
                $"  Heap now: [yellow]{debug.HeapObjectsAllocated:N0} objects[/], " +
                $"{debug.IndexedItemsAllocated:N0} indexed items, " +
                $"{debug.DictionaryEntriesAllocated:N0} dictionary entries");
        }
        else
        {
            console.MarkupLine("  Heap now: [grey]not reported by this SQL batch[/]");
        }
        if (result.GeneratedSql is not null)
        {
            console.MarkupLine(
                $"  Generated SQL: {ParityRunResult.CountLines(result.GeneratedSql):N0} lines, " +
                $"{result.GeneratedSql.Length:N0} characters");
        }
    }

    private static void RenderProfile(IAnsiConsole console, SqlRunProfile? profile)
    {
        if (profile is null)
            return;

        console.MarkupLine(
            $"[blue]Profile[/] [grey]({profile.WarmupRuns} warm-up, " +
            $"{profile.SqlServerSamples.Count} measured runs)[/]");
        if (profile.SqlServerSamples.Count == 0)
        {
            console.MarkupLine("  SQL Server: [grey]no successful samples[/]");
            return;
        }

        var ordered = profile.SqlServerSamples.Order().ToArray();
        var median = ordered.Length % 2 == 1
            ? ordered[ordered.Length / 2]
            : TimeSpan.FromTicks((ordered[ordered.Length / 2 - 1].Ticks + ordered[ordered.Length / 2].Ticks) / 2);
        console.MarkupLine(
            $"  SQL Server: [yellow]{FormatDuration(median)} median[/] " +
            $"[grey]({FormatDuration(ordered[0])}–{FormatDuration(ordered[^1])})[/]");
    }

    private static string FormatDuration(TimeSpan elapsed) => elapsed.TotalSeconds < 1
        ? $"{elapsed.TotalMilliseconds:0} ms"
        : elapsed.TotalMinutes < 1
            ? $"{elapsed.TotalSeconds:0.00} s"
            : $"{(int)elapsed.TotalMinutes}:{elapsed.Seconds:00}.{elapsed.Milliseconds / 10:00}";
}

internal sealed record SharpSqlRunProjectSettings(
    string? EntryPoint,
    string? ConnectionName,
    string? ConnectionStringEnvironmentVariable,
    bool KeepContainer,
    string SqlServerImage,
    string DatabaseName,
    int CommandTimeoutSeconds)
{
    public static SharpSqlRunProjectSettings Default { get; } = new(
        null,
        null,
        null,
        false,
        RunCommand.DefaultImage,
        RunCommand.DefaultDatabase,
        60);

    public static SharpSqlRunProjectSettings Load(string projectPath)
    {
        var document = XDocument.Load(projectPath);
        string? Property(string name) => document.Descendants()
            .LastOrDefault(element => element.Name.LocalName == name)?
            .Value.Trim() is { Length: > 0 } value
            ? value
            : null;
        var keepContainer = bool.TryParse(Property("SharpSqlKeepContainer"), out var keep) && keep;
        var timeout = int.TryParse(Property("SharpSqlCommandTimeoutSeconds"), out var parsedTimeout) && parsedTimeout > 0
            ? parsedTimeout
            : 60;
        return new SharpSqlRunProjectSettings(
            Property("SharpSqlEntryPoint"),
            Property("SharpSqlConnectionName"),
            Property("SharpSqlConnectionStringEnvironment"),
            keepContainer,
            Property("SharpSqlContainerImage") ?? RunCommand.DefaultImage,
            Property("SharpSqlContainerDatabase") ?? RunCommand.DefaultDatabase,
            timeout);
    }
}
