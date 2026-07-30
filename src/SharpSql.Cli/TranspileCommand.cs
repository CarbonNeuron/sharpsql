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

        [CommandOption("--runtime-storage <MODE>")]
        [Description("Runtime state mode: Ephemeral (default), MemoryOptimized, Durable, or ServiceBroker.")]
        [DefaultValue(RuntimeStorageKind.Ephemeral)]
        public RuntimeStorageKind RuntimeStorage { get; init; } = RuntimeStorageKind.Ephemeral;

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
            if (InstallerOutputPath is not null && !SqlOutputArtifacts.RequiresInstaller(RuntimeStorage))
                return ValidationResult.Error("--installer-output requires MemoryOptimized or ServiceBroker runtime storage.");
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
        var compilerOptions = new TranspileOptions
        {
            RuntimeStorage = settings.RuntimeStorage,
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
            InstallerSql(settings.RuntimeStorage),
            cancellationToken);
        if (SqlOutputArtifacts.RequiresInstaller(settings.RuntimeStorage) && artifactPaths.InstallerPath is null)
        {
            const string message =
                "Runtime installer SQL was not written; use --output or --installer-output to create it.";
            if (environment.Error is not null)
                await environment.Error.WriteLineAsync(message.AsMemory(), cancellationToken);
        }
        return 0;
    }

    private static string? InstallerSql(RuntimeStorageKind runtimeStorage) => runtimeStorage switch
    {
        RuntimeStorageKind.MemoryOptimized => SharpSqlMemoryOptimizedRuntime.GenerateProvisioningSql(),
        RuntimeStorageKind.ServiceBroker => SharpSqlServiceBrokerRuntime.GenerateProvisioningSql(),
        _ => null
    };
}
