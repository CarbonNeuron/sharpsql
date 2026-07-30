using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;

namespace SharpSql.Cli;

/// <summary>Removes an installed SharpSql application from SQL Server.</summary>
[Description("Remove an installed SharpSql application from SQL Server.")]
public sealed class UnpublishCommand : AsyncCommand<UnpublishCommand.Settings>
{
    /// <summary>Defines the options accepted by the <c>unpublish</c> command.</summary>
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--schema <SCHEMA>")]
        [Description("Application SQL schema.")]
        public string SchemaName { get; init; } = string.Empty;

        [CommandOption("--name <NAME>")]
        [Description("Application name recorded in PackageManifest.")]
        public string ApplicationName { get; init; } = string.Empty;

        [CommandOption("--connection <NAME>")]
        [Description("Connection string name from environment or appsettings.")]
        public string? ConnectionName { get; init; }

        [CommandOption("--connection-string-env <VARIABLE>")]
        [Description("Environment variable containing the connection string.")]
        public string? ConnectionStringEnvironmentVariable { get; init; }

        [CommandOption("--timeout <SECONDS>")]
        [Description("SQL command timeout in seconds.")]
        [DefaultValue(60)]
        public int CommandTimeoutSeconds { get; init; } = 60;

        /// <inheritdoc />
        public override ValidationResult Validate()
        {
            if (!ValidIdentifier(SchemaName))
                return ValidationResult.Error("--schema must be a valid SQL identifier of at most 128 characters.");
            if (!ValidManifestValue(ApplicationName))
                return ValidationResult.Error("--name is required, cannot exceed 128 characters, and cannot contain control characters.");
            if (string.IsNullOrWhiteSpace(ConnectionName) && ConnectionName is not null)
                return ValidationResult.Error("--connection cannot be empty.");
            if (string.IsNullOrWhiteSpace(ConnectionStringEnvironmentVariable) && ConnectionStringEnvironmentVariable is not null)
                return ValidationResult.Error("--connection-string-env cannot be empty.");
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
        UnpublishResult result;
        try
        {
            result = await (environment.UnpublishService ?? new UnpublishService()).UnpublishAsync(
                new UnpublishRequest(
                    settings.SchemaName,
                    settings.ApplicationName,
                    settings.ConnectionName,
                    settings.ConnectionStringEnvironmentVariable,
                    settings.CommandTimeoutSeconds),
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            environment.Console.MarkupLine("[red]✗ Application removal could not start[/]");
            environment.Console.Write(new Text(exception.Message + Environment.NewLine));
            return 2;
        }

        if (!result.Success)
        {
            var code = result.ErrorNumber is null ? string.Empty : $" {result.ErrorNumber}";
            environment.Console.MarkupLine($"[red]✗ SQL Server error{code}[/]");
            if (result.ErrorMessage is not null)
                environment.Console.Write(new Text(result.ErrorMessage + Environment.NewLine));
            return 1;
        }

        environment.Console.MarkupLine(
            $"[green]✓ Removed[/] [blue]{Markup.Escape(settings.ApplicationName)}[/] from " +
            $"[blue]{Markup.Escape(settings.SchemaName)}[/] on [blue]{Markup.Escape(result.SqlServer)}[/]");
        return 0;
    }

    private static bool ValidIdentifier(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 128 &&
        string.Equals(value, value.Trim(), StringComparison.Ordinal) &&
        !value.Any(character => char.IsControl(character) || char.IsSurrogate(character));

    private static bool ValidManifestValue(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 128 &&
        !value.Any(character => char.IsControl(character) || char.IsSurrogate(character));
}
