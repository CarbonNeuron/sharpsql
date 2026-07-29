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
            settings.CommandTimeoutSeconds ?? projectSettings.CommandTimeoutSeconds);

        SqlRunResult result;
        try
        {
            result = await (environment.SqlRunService ?? new SqlRunService())
                .RunAsync(request, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            environment.Console.MarkupLine("[red]✗ SQL execution could not start[/]");
            environment.Console.Write(new Text(exception.Message + Environment.NewLine));
            return 2;
        }

        foreach (var diagnostic in result.Diagnostics)
            environment.Console.WriteLine(diagnostic.ToString());
        foreach (var message in result.Messages)
            environment.Console.WriteLine(message);
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

    private static string ResolveInput(string? requestedPath)
    {
        if (requestedPath is null || Directory.Exists(requestedPath))
            return ProjectSdkInstaller.ResolveProject(requestedPath, Environment.CurrentDirectory);
        var path = Path.GetFullPath(requestedPath);
        if (!File.Exists(path))
            throw new ProjectInitializationException($"Input file was not found: {path}");
        return path;
    }
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
