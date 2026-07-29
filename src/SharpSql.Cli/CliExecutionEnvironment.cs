using Spectre.Console;

namespace SharpSql.Cli;

public sealed record CliExecutionEnvironment(
    IAnsiConsole Console,
    TextReader Input,
    TextWriter? Output = null,
    TextWriter? Error = null,
    IParityRunner? ParityRunner = null);
