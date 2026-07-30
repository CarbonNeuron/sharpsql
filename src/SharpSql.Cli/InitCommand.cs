using System.ComponentModel;
using System.Reflection;
using System.Text.RegularExpressions;
using Spectre.Console;
using Spectre.Console.Cli;

namespace SharpSql.Cli;

/// <summary>Installs and configures the SharpSql SDK in a console project.</summary>
[Description("Install and configure SharpSql.Sdk in a console project.")]
public sealed class InitCommand : AsyncCommand<InitCommand.Settings>
{
    internal const string DefaultOutputPath = "$(OutputPath)$(AssemblyName).sql";

    /// <summary>Defines the options accepted by the <c>init</c> command.</summary>
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "[PROJECT]")]
        [Description("A console .csproj or a directory containing one. Uses the current directory when omitted.")]
        public string? ProjectPath { get; init; }

        [CommandOption("-o|--output <PATH>")]
        [Description("MSBuild path for generated SQL. Defaults beside the compiled application.")]
        public string? OutputPath { get; init; }

        [CommandOption("--entry <METHOD>")]
        [Description("Entry method in Namespace.Type::Method form. The console entry point is used by default.")]
        public string? EntryPoint { get; init; }

        [CommandOption("--sdk-version <VERSION>")]
        [Description("SharpSql.Sdk package version. Defaults to the installed tool version.")]
        public string? SdkVersion { get; init; }

        [CommandOption("--analyzer-only")]
        [Description("Enable live diagnostics without generating SQL during normal builds.")]
        public bool AnalyzerOnly { get; init; }

        [CommandOption("--no-analyzer")]
        [Description("Disable live Roslyn compatibility diagnostics.")]
        public bool NoAnalyzer { get; init; }

        [CommandOption("--no-restore")]
        [Description("Update the project without running dotnet restore.")]
        public bool NoRestore { get; init; }

        [CommandOption("--connection <NAME>")]
        [Description("Connection string name from environment, user secrets, or appsettings.")]
        public string? ConnectionName { get; init; }

        [CommandOption("--connection-string-env <VARIABLE>")]
        [Description("Environment variable containing the connection string.")]
        public string? ConnectionStringEnvironmentVariable { get; init; }

        [CommandOption("--container")]
        [Description("Use a SQL Server Testcontainer instead of a configured connection.")]
        public bool UseContainer { get; init; }

        [CommandOption("--keep-container")]
        [Description("Keep and reuse the SQL Server Testcontainer.")]
        public bool KeepContainer { get; init; }

        [CommandOption("--database <DATABASE>")]
        [Description("Database created inside the SQL Server Testcontainer.")]
        [DefaultValue(RunCommand.DefaultDatabase)]
        public string DatabaseName { get; init; } = RunCommand.DefaultDatabase;

        [CommandOption("--image <IMAGE>")]
        [Description("SQL Server Testcontainer image.")]
        [DefaultValue(RunCommand.DefaultImage)]
        public string SqlServerImage { get; init; } = RunCommand.DefaultImage;

        [CommandOption("--timeout <SECONDS>")]
        [Description("SQL command timeout in seconds.")]
        [DefaultValue(60)]
        public int CommandTimeoutSeconds { get; init; } = 60;

        [CommandOption("--no-launch-profile")]
        [Description("Do not add the SharpSql (SQL Server) IDE launch profile.")]
        public bool NoLaunchProfile { get; init; }

        /// <inheritdoc />
        public override ValidationResult Validate()
        {
            if (string.IsNullOrWhiteSpace(OutputPath) && OutputPath is not null)
                return ValidationResult.Error("--output cannot be empty.");
            if (string.IsNullOrWhiteSpace(EntryPoint) && EntryPoint is not null)
                return ValidationResult.Error("--entry cannot be empty.");
            if (string.IsNullOrWhiteSpace(SdkVersion) && SdkVersion is not null)
                return ValidationResult.Error("--sdk-version cannot be empty.");
            if (string.IsNullOrWhiteSpace(ConnectionName) && ConnectionName is not null)
                return ValidationResult.Error("--connection cannot be empty.");
            if (string.IsNullOrWhiteSpace(ConnectionStringEnvironmentVariable) &&
                ConnectionStringEnvironmentVariable is not null)
                return ValidationResult.Error("--connection-string-env cannot be empty.");
            if (string.IsNullOrWhiteSpace(DatabaseName))
                return ValidationResult.Error("--database cannot be empty.");
            if (string.IsNullOrWhiteSpace(SqlServerImage))
                return ValidationResult.Error("--image cannot be empty.");
            if (SdkVersion is not null && !Regex.IsMatch(
                    SdkVersion,
                    "^[0-9]+\\.[0-9]+\\.[0-9]+(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?$",
                    RegexOptions.CultureInvariant))
                return ValidationResult.Error("--sdk-version must be a semantic version such as 1.2.3 or 1.2.3-preview.1.");
            if (CommandTimeoutSeconds <= 0)
                return ValidationResult.Error("--timeout must be greater than zero.");
            if (UseContainer && (ConnectionName is not null || ConnectionStringEnvironmentVariable is not null))
                return ValidationResult.Error("--container cannot be combined with connection options.");
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

        string projectPath;
        try
        {
            projectPath = ProjectSdkInstaller.ResolveProject(settings.ProjectPath, Environment.CurrentDirectory);
        }
        catch (ProjectInitializationException exception)
        {
            await WriteErrorAsync(environment, exception.Message, cancellationToken);
            return 1;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            await WriteErrorAsync(
                environment,
                $"Could not resolve the project path: {exception.Message}",
                cancellationToken);
            return 2;
        }

        var version = settings.SdkVersion ?? GetToolVersion();
        ProjectSdkInstallation installation;
        try
        {
            installation = ProjectSdkInstaller.Install(
                projectPath,
                version,
                settings.OutputPath,
                settings.EntryPoint,
                settings.AnalyzerOnly,
                settings.NoAnalyzer,
                settings.ConnectionName,
                settings.ConnectionStringEnvironmentVariable,
                settings.UseContainer,
                settings.KeepContainer,
                settings.SqlServerImage,
                settings.DatabaseName,
                settings.CommandTimeoutSeconds,
                addLaunchProfile: !settings.NoLaunchProfile);
        }
        catch (ProjectInitializationException exception)
        {
            await WriteErrorAsync(environment, exception.Message, cancellationToken);
            return 1;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            await WriteErrorAsync(
                environment,
                $"Could not configure the project: {exception.Message}",
                cancellationToken);
            return 2;
        }

        await WriteOutputAsync(
            environment,
            $"Configured {installation.ProjectPath}{Environment.NewLine}" +
            $"  SDK: SharpSql.Sdk {installation.SdkVersion}{Environment.NewLine}" +
            $"  SQL: {installation.OutputPath}{Environment.NewLine}" +
            $"  Build generation: {(installation.GenerateOnBuild ? "enabled" : "disabled")}{Environment.NewLine}" +
            $"  Live diagnostics: {(installation.EnableAnalyzer ? "enabled" : "disabled")}{Environment.NewLine}" +
            $"  SQL Server: {installation.SqlServerConfiguration}{Environment.NewLine}" +
            $"  IDE profile: {(installation.LaunchProfileAdded ? "SharpSql (SQL Server)" : "not changed")}{Environment.NewLine}",
            cancellationToken);

        if (settings.NoRestore)
            return 0;

        var restorer = environment.ProjectRestorer ?? new DotNetProjectRestorer();
        int restoreExitCode;
        try
        {
            restoreExitCode = await restorer.RestoreAsync(
                projectPath,
                environment.Output ?? Console.Out,
                environment.Error ?? Console.Error,
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await WriteErrorAsync(
                environment,
                $"Could not run dotnet restore: {exception.Message}. The project remains configured.",
                cancellationToken);
            return 2;
        }
        if (restoreExitCode == 0)
        {
            await WriteOutputAsync(environment, "Restore completed." + Environment.NewLine, cancellationToken);
            return 0;
        }

        await WriteErrorAsync(
            environment,
            $"dotnet restore failed with exit code {restoreExitCode}; the project remains configured.",
            cancellationToken);
        return restoreExitCode;
    }

    internal static string GetToolVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                   .InformationalVersion.Split('+', 2)[0] ??
               assembly.GetName().Version?.ToString(3) ??
               throw new InvalidOperationException("The SharpSql tool version could not be determined.");
    }

    private static Task WriteOutputAsync(
        CliExecutionEnvironment environment,
        string message,
        CancellationToken cancellationToken)
    {
        if (environment.Output is not null)
            return environment.Output.WriteAsync(message.AsMemory(), cancellationToken);
        environment.Console.Write(new Text(message));
        return Task.CompletedTask;
    }

    private static Task WriteErrorAsync(
        CliExecutionEnvironment environment,
        string message,
        CancellationToken cancellationToken)
    {
        if (environment.Error is not null)
            return environment.Error.WriteLineAsync(message.AsMemory(), cancellationToken);
        environment.Console.MarkupLine($"[red]{Markup.Escape(message)}[/]");
        return Task.CompletedTask;
    }
}
