using Spectre.Console;

namespace SharpSql.Cli;

/// <summary>Provides console streams and replaceable services to CLI commands.</summary>
public sealed record CliExecutionEnvironment(
    IAnsiConsole Console,
    TextReader Input,
    TextWriter? Output = null,
    TextWriter? Error = null,
    IParityRunner? ParityRunner = null,
    IProjectRestorer? ProjectRestorer = null,
    ISqlRunService? SqlRunService = null,
    IPublishService? PublishService = null);
