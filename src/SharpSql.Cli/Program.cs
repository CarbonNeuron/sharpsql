using SharpSql.Cli;
using Spectre.Console;
using Spectre.Console.Cli;

var app = new CommandApp();
var environment = new CliExecutionEnvironment(AnsiConsole.Console, Console.In, Console.Out, Console.Error);
app.Configure(configurator =>
{
    configurator.SetApplicationName("sharpsql");
    configurator.SetApplicationVersion("0.1.0");
    configurator.ConfigureConsole(AnsiConsole.Console);
    configurator.AddCommand<TranspileCommand>("transpile").WithData(environment);
    configurator.AddCommand<VerifyCommand>("verify").WithData(environment);
});

return await app.RunAsync(CliArgumentRouter.Route(args));
