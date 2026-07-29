using SharpSql.Cli;
using Spectre.Console;
using Spectre.Console.Cli;

var app = new CommandApp<TranspileCommand>();
app.WithData(new CliExecutionEnvironment(AnsiConsole.Console, Console.In, Console.Out, Console.Error));
app.Configure(configurator =>
{
    configurator.SetApplicationName("sharpsql");
    configurator.SetApplicationVersion("0.1.0");
    configurator.ConfigureConsole(AnsiConsole.Console);
});

return await app.RunAsync(args);
