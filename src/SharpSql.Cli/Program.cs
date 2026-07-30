using System.Reflection;
using SharpSql.Cli;
using Spectre.Console;
using Spectre.Console.Cli;

var app = new CommandApp();
var environment = new CliExecutionEnvironment(AnsiConsole.Console, Console.In, Console.Out, Console.Error);
var applicationVersion = Assembly.GetExecutingAssembly()
    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
    .InformationalVersion.Split('+', 2)[0] ?? "unknown";
app.Configure(configurator =>
{
    configurator.SetApplicationName("sharpsql");
    configurator.SetApplicationVersion(applicationVersion);
    configurator.ConfigureConsole(AnsiConsole.Console);
    configurator.AddCommand<InitCommand>("init").WithData(environment);
    configurator.AddCommand<ConformanceCommand>("conformance").WithData(environment);
    configurator.AddCommand<PublishCommand>("publish").WithData(environment);
    configurator.AddCommand<RunCommand>("run").WithData(environment);
    configurator.AddCommand<TranspileCommand>("transpile").WithData(environment);
    configurator.AddCommand<UnpublishCommand>("unpublish").WithData(environment);
    configurator.AddCommand<VerifyCommand>("verify").WithData(environment);
});

return await app.RunAsync(CliArgumentRouter.Route(args));
