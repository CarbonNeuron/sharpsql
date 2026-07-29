using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;

namespace SharpSql.Cli;

[Description("Transpile a C# source file or SDK-style project into a T-SQL program batch.")]
public sealed class TranspileCommand : AsyncCommand<TranspileCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "[INPUT]")]
        [Description("A .cs or .csproj input. Reads C# from standard input when omitted.")]
        public string? InputPath { get; init; }

        [CommandOption("-o|--output <OUTPUT>")]
        [Description("Write generated SQL to a file instead of standard output.")]
        public string? OutputPath { get; init; }

        [CommandOption("--installer-output <OUTPUT>")]
        [Description("Write the standalone Service Broker installer SQL to this file.")]
        public string? InstallerOutputPath { get; init; }

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

        [CommandOption("--runtime-storage <MODE>")]
        [Description("Runtime state mode: Ephemeral (default), Durable, or ServiceBroker.")]
        [DefaultValue(RuntimeStorageKind.Ephemeral)]
        public RuntimeStorageKind RuntimeStorage { get; init; } = RuntimeStorageKind.Ephemeral;

        public override ValidationResult Validate()
        {
            var isProject = InputPath?.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) == true;
            if (!isProject && EntryPoint is not null)
                return ValidationResult.Error("--entry is supported only for .csproj inputs.");
            if (!isProject && TargetFramework is not null)
                return ValidationResult.Error("--framework is supported only for .csproj inputs.");
            if (InputPath is not null && !File.Exists(InputPath))
                return ValidationResult.Error($"Input file was not found: {InputPath}");
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
                          new CliExecutionEnvironment(AnsiConsole.Console, Console.In);
        var isProject = settings.InputPath?.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) == true;
        var compilerOptions = new TranspileOptions { RuntimeStorage = settings.RuntimeStorage };

        TranspileResult result;
        if (isProject)
        {
            result = await new SharpSqlProjectCompiler().TranspileAsync(
                settings.InputPath!,
                new ProjectTranspileOptions
                {
                    EntryPoint = settings.EntryPoint,
                    Configuration = settings.Configuration,
                    TargetFramework = settings.TargetFramework,
                    CompilerOptions = compilerOptions
                },
                cancellationToken);
        }
        else
        {
            var source = settings.InputPath is null
                ? await environment.Input.ReadToEndAsync(cancellationToken)
                : await File.ReadAllTextAsync(settings.InputPath, cancellationToken);
            result = new SharpSqlCompiler().Transpile(source, compilerOptions);
        }

        foreach (var diagnostic in result.Diagnostics)
        {
            if (environment.Error is null)
                environment.Console.WriteLine(diagnostic.ToString());
            else
                await environment.Error.WriteLineAsync(diagnostic.ToString().AsMemory(), cancellationToken);
        }
        if (!result.Success)
            return 1;

        var artifactPaths = SqlOutputArtifacts.ResolvePaths(
            settings.OutputPath,
            settings.InstallerOutputPath,
            settings.RuntimeStorage);
        if (artifactPaths.ProgramPath is null)
        {
            if (environment.Output is null)
                environment.Console.Write(new Text(result.Sql));
            else
                await environment.Output.WriteAsync(result.Sql.AsMemory(), cancellationToken);
        }
        await SqlOutputArtifacts.WriteAsync(
            artifactPaths,
            result.Sql,
            settings.RuntimeStorage == RuntimeStorageKind.ServiceBroker
                ? SharpSqlServiceBrokerRuntime.GenerateProvisioningSql()
                : null,
            cancellationToken);
        if (settings.RuntimeStorage == RuntimeStorageKind.ServiceBroker && artifactPaths.InstallerPath is null)
        {
            const string message =
                "Service Broker installer SQL was not written; use --output or --installer-output to create it.";
            if (environment.Error is not null)
                await environment.Error.WriteLineAsync(message.AsMemory(), cancellationToken);
        }
        return 0;
    }
}
