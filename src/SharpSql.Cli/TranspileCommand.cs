using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;

namespace SharpSql.Cli;

[Description("Transpile a C# source file or SDK-style project into a self-contained T-SQL batch.")]
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

        public override ValidationResult Validate()
        {
            var isProject = InputPath?.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) == true;
            if (!isProject && EntryPoint is not null)
                return ValidationResult.Error("--entry is supported only for .csproj inputs.");
            if (!isProject && TargetFramework is not null)
                return ValidationResult.Error("--framework is supported only for .csproj inputs.");
            if (InputPath is not null && !File.Exists(InputPath))
                return ValidationResult.Error($"Input file was not found: {InputPath}");
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

        TranspileResult result;
        if (isProject)
        {
            result = await new SharpSqlProjectCompiler().TranspileAsync(
                settings.InputPath!,
                new ProjectTranspileOptions
                {
                    EntryPoint = settings.EntryPoint,
                    Configuration = settings.Configuration,
                    TargetFramework = settings.TargetFramework
                },
                cancellationToken);
        }
        else
        {
            var source = settings.InputPath is null
                ? await environment.Input.ReadToEndAsync(cancellationToken)
                : await File.ReadAllTextAsync(settings.InputPath, cancellationToken);
            result = new SharpSqlCompiler().Transpile(source);
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

        if (settings.OutputPath is null)
        {
            if (environment.Output is null)
                environment.Console.Write(new Text(result.Sql));
            else
                await environment.Output.WriteAsync(result.Sql.AsMemory(), cancellationToken);
        }
        else
            await File.WriteAllTextAsync(settings.OutputPath, result.Sql, cancellationToken);
        return 0;
    }
}
