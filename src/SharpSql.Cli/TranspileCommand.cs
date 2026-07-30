using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;

namespace SharpSql.Cli;

/// <summary>Transpiles C# source or a .NET project into T-SQL.</summary>
[Description("Transpile a C# source file or SDK-style project into a T-SQL program batch.")]
public sealed class TranspileCommand : AsyncCommand<TranspileCommand.Settings>
{
    /// <summary>Defines the options accepted by the <c>transpile</c> command.</summary>
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "[INPUT]")]
        [Description("A .cs or .csproj input. Reads C# from standard input when omitted.")]
        public string? InputPath { get; init; }

        [CommandOption("-o|--output <PATH>")]
        [Description("Write generated SQL to a file instead of standard output.")]
        public string? OutputPath { get; init; }

        [CommandOption("--installer-output <PATH>")]
        [Description("Write standalone runtime provisioning SQL to this file.")]
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

        [CommandOption("--execution <MODE>")]
        [Description("Runtime execution mode: Auto (default), Inline, or ServiceBroker.")]
        [DefaultValue(RuntimeExecutionKind.Auto)]
        public RuntimeExecutionKind Execution { get; init; } = RuntimeExecutionKind.Auto;

        [CommandOption("--durability <MODE>")]
        [Description("Runtime durability: Ephemeral (default) or Durable.")]
        [DefaultValue(RuntimeDurabilityKind.Ephemeral)]
        public RuntimeDurabilityKind Durability { get; init; } = RuntimeDurabilityKind.Ephemeral;

        [CommandOption("--memory-optimized|--memory-optimized-tables")]
        [Description("Use provisioned memory-optimized runtime tables.")]
        public bool UseMemoryOptimizedTables { get; init; }

        [CommandOption("--runtime-storage <MODE>")]
        [Description("Compatibility alias for the legacy combined runtime mode.")]
        public RuntimeStorageKind? RuntimeStorage { get; init; }

        [CommandOption("--native-kernels")]
        [Description("Extract supported pure scalar methods into natively compiled procedures.")]
        public bool EnableNativeKernels { get; init; }

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
            if (string.IsNullOrWhiteSpace(OutputPath) && OutputPath is not null)
                return ValidationResult.Error("--output cannot be empty.");
            if (string.IsNullOrWhiteSpace(InstallerOutputPath) && InstallerOutputPath is not null)
                return ValidationResult.Error("--installer-output cannot be empty.");
            if (RuntimeStorage is not null && CliRuntimeOptions.HasSplitConfiguration(
                    Execution,
                    Durability,
                    UseMemoryOptimizedTables))
                return ValidationResult.Error("--runtime-storage cannot be combined with the split runtime options.");
            var runtime = CliRuntimeOptions.Resolve(
                Execution,
                Durability,
                UseMemoryOptimizedTables,
                RuntimeStorage);
            if (InstallerOutputPath is not null && !SqlOutputArtifacts.MayRequireInstaller(runtime))
                return ValidationResult.Error(
                    "--installer-output requires Auto or ServiceBroker execution, or memory-optimized tables.");
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
        try
        {
            return await ExecuteCoreAsync(environment, settings, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var message = $"Transpilation could not complete: {exception.Message}";
            if (environment.Error is null)
                environment.Console.MarkupLine($"[red]{Markup.Escape(message)}[/]");
            else
                await environment.Error.WriteLineAsync(message.AsMemory(), cancellationToken);
            return 2;
        }
    }

    private static async Task<int> ExecuteCoreAsync(
        CliExecutionEnvironment environment,
        Settings settings,
        CancellationToken cancellationToken)
    {
        var isProject = settings.InputPath?.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) == true;
        var requestedRuntime = CliRuntimeOptions.Resolve(
            settings.Execution,
            settings.Durability,
            settings.UseMemoryOptimizedTables,
            settings.RuntimeStorage);
        var compilerOptions = new TranspileOptions
        {
            Execution = requestedRuntime.Execution,
            Durability = requestedRuntime.Durability,
            UseMemoryOptimizedTables = requestedRuntime.UseMemoryOptimizedTables,
            EnableNativeKernels = settings.EnableNativeKernels
        };

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
            result.EffectiveRuntime);
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
            SqlOutputArtifacts.InstallerSql(result.EffectiveRuntime),
            cancellationToken);
        if (SqlOutputArtifacts.RequiresInstaller(result.EffectiveRuntime) && artifactPaths.InstallerPath is null)
        {
            const string message =
                "Runtime installer SQL was not written; use --output or --installer-output to create it.";
            if (environment.Error is not null)
                await environment.Error.WriteLineAsync(message.AsMemory(), cancellationToken);
        }
        return 0;
    }
}
