using System.ComponentModel;
using System.Text.RegularExpressions;
using Spectre.Console;
using Spectre.Console.Cli;

namespace SharpSql.Cli;

/// <summary>Compiles and publishes a versioned SharpSql application to SQL Server.</summary>
[Description("Compile and publish a versioned SharpSql application to SQL Server.")]
public sealed partial class PublishCommand : AsyncCommand<PublishCommand.Settings>
{
    /// <summary>Defines the options accepted by the <c>publish</c> command.</summary>
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<INPUT>")]
        [Description("A .cs source file or SDK-style .csproj project.")]
        public string InputPath { get; init; } = string.Empty;

        [CommandOption("--schema <SCHEMA>")]
        [Description("Application SQL schema. Defaults to sharpsql_<application name>.")]
        public string? SchemaName { get; init; }

        [CommandOption("--name <NAME>")]
        [Description("Application name. Defaults to the input file or project name.")]
        public string? ApplicationName { get; init; }

        [CommandOption("--version <VERSION>")]
        [Description("Published application version.")]
        [DefaultValue("1.0.0")]
        public string Version { get; init; } = "1.0.0";

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

        [CommandOption("--timeout <SECONDS>")]
        [Description("SQL command timeout in seconds.")]
        [DefaultValue(60)]
        public int CommandTimeoutSeconds { get; init; } = 60;

        [CommandOption("--memory-optimized")]
        [Description("Provision and use application-local memory-optimized runtime objects.")]
        public bool MemoryOptimized { get; init; }

        [CommandOption("--native-kernels")]
        [Description("Extract supported pure scalar methods into natively compiled procedures; requires --memory-optimized.")]
        public bool EnableNativeKernels { get; init; }

        /// <inheritdoc />
        public override ValidationResult Validate()
        {
            if (string.IsNullOrWhiteSpace(InputPath))
                return ValidationResult.Error("An input .cs or .csproj path is required.");
            if (!InputPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) &&
                !InputPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            {
                return ValidationResult.Error("Publish input must be a .cs source file or .csproj project.");
            }
            if (!File.Exists(InputPath))
                return ValidationResult.Error($"Input file was not found: {InputPath}");
            if (string.IsNullOrWhiteSpace(SchemaName) && SchemaName is not null)
                return ValidationResult.Error("--schema cannot be empty.");
            if (SchemaName is not null && !IsSqlIdentifier(SchemaName))
                return ValidationResult.Error("--schema must be a valid SQL identifier of at most 128 characters.");
            if (string.IsNullOrWhiteSpace(ApplicationName) && ApplicationName is not null)
                return ValidationResult.Error("--name cannot be empty.");
            if (ApplicationName is not null && !IsManifestValue(ApplicationName))
                return ValidationResult.Error("--name cannot exceed 128 characters or contain control characters.");
            if (string.IsNullOrWhiteSpace(Version))
                return ValidationResult.Error("--version cannot be empty.");
            if (!IsManifestValue(Version))
                return ValidationResult.Error("--version cannot exceed 128 characters or contain control characters.");
            if (string.IsNullOrWhiteSpace(EntryPoint) && EntryPoint is not null)
                return ValidationResult.Error("--entry cannot be empty.");
            if (string.IsNullOrWhiteSpace(Configuration))
                return ValidationResult.Error("--configuration cannot be empty.");
            if (string.IsNullOrWhiteSpace(TargetFramework) && TargetFramework is not null)
                return ValidationResult.Error("--framework cannot be empty.");
            if (!InputPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) &&
                (EntryPoint is not null || TargetFramework is not null))
            {
                return ValidationResult.Error("--entry and --framework are supported only for .csproj inputs.");
            }
            if (string.IsNullOrWhiteSpace(ConnectionName) && ConnectionName is not null)
                return ValidationResult.Error("--connection cannot be empty.");
            if (string.IsNullOrWhiteSpace(ConnectionStringEnvironmentVariable) &&
                ConnectionStringEnvironmentVariable is not null)
            {
                return ValidationResult.Error("--connection-string-env cannot be empty.");
            }
            if (CommandTimeoutSeconds <= 0)
                return ValidationResult.Error("--timeout must be greater than zero.");
            if (EnableNativeKernels && !MemoryOptimized)
                return ValidationResult.Error("--native-kernels requires --memory-optimized.");
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
        var inputPath = Path.GetFullPath(settings.InputPath);
        var applicationName = settings.ApplicationName ?? Path.GetFileNameWithoutExtension(inputPath);
        var schemaName = settings.SchemaName ?? DefaultSchemaName(applicationName);
        PublishResult result;
        try
        {
            result = await (environment.PublishService ?? new PublishService()).PublishAsync(
                new PublishRequest(
                    inputPath,
                    schemaName,
                    applicationName,
                    settings.Version,
                    settings.EntryPoint,
                    settings.Configuration,
                    settings.TargetFramework,
                    settings.ConnectionName,
                    settings.ConnectionStringEnvironmentVariable,
                    settings.CommandTimeoutSeconds,
                    settings.MemoryOptimized,
                    settings.EnableNativeKernels),
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            environment.Console.MarkupLine("[red]✗ Application publishing could not start[/]");
            environment.Console.Write(new Text(exception.Message + Environment.NewLine));
            return 2;
        }

        foreach (var diagnostic in result.Diagnostics)
            environment.Console.WriteLine(diagnostic.ToString());
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

        environment.Console.MarkupLine(
            $"[green]✓ Published[/] [blue]{Markup.Escape(applicationName)}[/] " +
            $"[grey]v{Markup.Escape(settings.Version)}[/] to " +
            $"[blue]{Markup.Escape(schemaName)}[/] on [blue]{Markup.Escape(result.SqlServer)}[/]");
        return 0;
    }

    internal static string DefaultSchemaName(string applicationName)
    {
        var normalized = InvalidIdentifierCharacter().Replace(applicationName, "_").Trim('_').ToLowerInvariant();
        if (normalized.Length == 0)
            normalized = "application";
        if (char.IsDigit(normalized[0]))
            normalized = "app_" + normalized;
        const string prefix = "sharpsql_";
        return prefix + normalized[..Math.Min(normalized.Length, 128 - prefix.Length)];
    }

    private static bool IsSqlIdentifier(string value) =>
        value.Length <= 128 &&
        string.Equals(value, value.Trim(), StringComparison.Ordinal) &&
        !value.Any(character => char.IsControl(character) || char.IsSurrogate(character));

    private static bool IsManifestValue(string value) =>
        value.Length <= 128 &&
        !value.Any(character => char.IsControl(character) || char.IsSurrogate(character));

    [GeneratedRegex("[^A-Za-z0-9_]+", RegexOptions.CultureInvariant)]
    private static partial Regex InvalidIdentifierCharacter();
}
