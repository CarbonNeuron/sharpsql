using SharpSql.Cli;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Spectre.Console.Cli.Testing;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Xunit;

namespace SharpSql.Tests;

public sealed class CliTests
{
    private static string ProjectPath => Path.Combine(
        AppContext.BaseDirectory,
        "fixtures",
        "MultiFileProject",
        "MultiFileProject.csproj");

    [Fact]
    public async Task CompilesStandardInputThroughTheDefaultCommand()
    {
        var tester = CreateTester("Console.WriteLine(\"from stdin\");");

        var result = await tester.RunAsync([], TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("SET NOCOUNT ON;", result.Output);
        Assert.Contains("PRINT N'from stdin';", result.Output);
        Assert.DoesNotContain("-- SharpSql durable shared runtime", result.Output);
        var settings = Assert.IsType<TranspileCommand.Settings>(result.Settings);
        Assert.Equal(RuntimeStorageKind.Ephemeral, settings.RuntimeStorage);
    }

    [Fact]
    public async Task TranspileSelectsServiceBrokerRuntimeForSourceInput()
    {
        var tester = CreateTester("Console.WriteLine(\"from broker\");");

        var result = await tester.RunAsync(
            ["--runtime-storage", "ServiceBroker"],
            TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("-- SharpSql durable shared runtime", result.Output);
        var settings = Assert.IsType<TranspileCommand.Settings>(result.Settings);
        Assert.Equal(RuntimeStorageKind.ServiceBroker, settings.RuntimeStorage);
    }

    [Fact]
    public async Task BindsProjectOptionsAndCompilesTheSelectedEntryPoint()
    {
        var tester = CreateTester();

        var result = await tester.RunAsync(
            [ProjectPath, "--entry", "MultiFileProject.SqlJob::Run", "--configuration", "Release", "--framework", "net10.0"],
            TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("21 * 2", result.Output);
        var settings = Assert.IsType<TranspileCommand.Settings>(result.Settings);
        Assert.Equal("MultiFileProject.SqlJob::Run", settings.EntryPoint);
        Assert.Equal("Release", settings.Configuration);
        Assert.Equal("net10.0", settings.TargetFramework);
    }

    [Fact]
    public async Task TranspilePassesServiceBrokerRuntimeToProjectCompilation()
    {
        var tester = CreateTester();

        var result = await tester.RunAsync(
            [
                ProjectPath,
                "--entry", "MultiFileProject.SqlJob::Run",
                "--framework", "net10.0",
                "--runtime-storage", "ServiceBroker"
            ],
            TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("-- SharpSql durable shared runtime", result.Output);
    }

    [Fact]
    public async Task ExecutesQueryableProjectsWithRuntimeAssemblies()
    {
        var loaded = await new SharpSqlProjectCompiler().LoadCompilationAsync(
            ProjectPath,
            new ProjectTranspileOptions
            {
                EntryPoint = "MultiFileProject.SqlJob::Run",
                Configuration = "Release",
                TargetFramework = "net10.0"
            },
            TestContext.Current.CancellationToken);

        Assert.True(loaded.Success, string.Join(Environment.NewLine, loaded.Diagnostics));
        var source = CSharpSyntaxTree.ParseText("""
            using System;
            using System.Collections.Generic;
            using System.Linq;

            IQueryable<int> values = new List<int> { 42 }.AsQueryable();
            Console.WriteLine($"project={values.Single()}");
            """, cancellationToken: TestContext.Current.CancellationToken);
        var compilation = CSharpCompilation.Create(
            $"SharpSqlQueryableRuntime_{Guid.NewGuid():N}",
            [source],
            loaded.Compilation!.References,
            new CSharpCompilationOptions(OutputKind.ConsoleApplication));
        var outcome = await TestcontainersParityRunner.ExecuteProjectCSharpForTestingAsync(
            compilation,
            ProjectPath,
            requestedEntryPoint: null);

        Assert.Null(outcome.Failure);
        Assert.Equal("project=42", outcome.StandardOutput);
    }

    [Fact]
    public async Task ReportsSpectreValidationErrorsForProjectOnlyOptions()
    {
        var tester = CreateTester();

        var result = await tester.RunAsync(
            ["script.cs", "--entry", "Example.Job::Run"],
            TestContext.Current.CancellationToken);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("--entry is supported only for .csproj inputs", result.Output);
    }

    [Fact]
    public async Task WritesSqlToTheRequestedOutputFile()
    {
        var tester = CreateTester("Console.WriteLine(42);");
        var outputPath = Path.Combine(Path.GetTempPath(), $"sharpsql-{Guid.NewGuid():N}.sql");
        try
        {
            var result = await tester.RunAsync(
                ["--output", outputPath],
                TestContext.Current.CancellationToken);

            Assert.Equal(0, result.ExitCode);
            Assert.True(File.Exists(outputPath));
            Assert.Contains("PRINT", await File.ReadAllTextAsync(outputPath, TestContext.Current.CancellationToken));
        }
        finally
        {
            File.Delete(outputPath);
        }
    }

    [Fact]
    public async Task RendersGeneratedSpectreHelp()
    {
        var tester = CreateRoutedTester();

        var result = await tester.RunAsync(
            CliArgumentRouter.Route(["--help"]),
            TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("USAGE:", result.Output);
        Assert.Contains("init", result.Output);
        Assert.Contains("run", result.Output);
        Assert.Contains("transpile", result.Output);
        Assert.Contains("verify", result.Output);
    }

    [Fact]
    public async Task VerifyCommandReportsMatchingOutcomesAndCanSaveSql()
    {
        var parityResult = new ParityRunResult(
            new ParityOutcome("same", null),
            new ParityOutcome("same", null),
            "PRINT N'same';");
        var tester = CreateTester("Console.WriteLine(\"same\");", new StubParityRunner(parityResult));
        var outputPath = Path.Combine(Path.GetTempPath(), $"sharpsql-verify-{Guid.NewGuid():N}.sql");
        try
        {
            var result = await tester.RunAsync(
                ["verify", "--sql-output", outputPath],
                TestContext.Current.CancellationToken);

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("Parity verified", result.Output);
            Assert.Contains("1 SQL line", result.Output);
            Assert.Equal("PRINT N'same';", await File.ReadAllTextAsync(outputPath, TestContext.Current.CancellationToken));
        }
        finally
        {
            File.Delete(outputPath);
        }
    }

    [Fact]
    public async Task VerifyCommandReportsAParityMismatch()
    {
        var parityResult = new ParityRunResult(
            new ParityOutcome("local", null),
            new ParityOutcome("sql", null),
            string.Empty);
        var tester = CreateTester("Console.WriteLine(\"local\");", new StubParityRunner(parityResult));

        var result = await tester.RunAsync(["verify"], TestContext.Current.CancellationToken);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Parity mismatch", result.Output);
        Assert.Contains("stdout=\"local\"", result.Output);
        Assert.Contains("stdout=\"sql\"", result.Output);
    }

    [Fact]
    public async Task RoutesVerifyBeforeItsInputPath()
    {
        var parityResult = new ParityRunResult(
            new ParityOutcome("same", null),
            new ParityOutcome("same", null),
            string.Empty);
        var inputPath = Path.Combine(Path.GetTempPath(), $"sharpsql-verify-{Guid.NewGuid():N}.cs");
        await File.WriteAllTextAsync(
            inputPath,
            "Console.WriteLine(\"same\");",
            TestContext.Current.CancellationToken);
        try
        {
            var tester = CreateRoutedTester(new StubParityRunner(parityResult));

            var result = await tester.RunAsync(
                CliArgumentRouter.Route(["verify", inputPath]),
                TestContext.Current.CancellationToken);

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("Parity verified", result.Output);
            var settings = Assert.IsType<VerifyCommand.Settings>(result.Settings);
            Assert.Equal(inputPath, settings.InputPath);
        }
        finally
        {
            File.Delete(inputPath);
        }
    }

    [Fact]
    public async Task VerifyCommandPassesProjectOptionsToTheParityRunner()
    {
        var parityResult = new ParityRunResult(
            new ParityOutcome("same", null),
            new ParityOutcome("same", null),
            "PRINT N'same';");
        var runner = new StubParityRunner(parityResult);
        var tester = CreateRoutedTester(runner);

        var result = await tester.RunAsync(
            CliArgumentRouter.Route([
                "verify",
                ProjectPath,
                "--entry", "MultiFileProject.SqlJob::Run",
                "--configuration", "Debug",
                "--framework", "net10.0",
                "--keep-container",
                "--debug",
                "--profile"
            ]),
            TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("container kept running for reuse", result.Output);
        var request = Assert.IsType<ParityRunRequest>(runner.LastRequest);
        Assert.True(request.IsProject);
        Assert.Null(request.Source);
        Assert.Equal(ProjectPath, request.InputPath);
        Assert.Equal("MultiFileProject.SqlJob::Run", request.EntryPoint);
        Assert.Equal("Debug", request.Configuration);
        Assert.Equal("net10.0", request.TargetFramework);
        Assert.True(request.KeepContainer);
        Assert.True(request.Debug);
        Assert.True(request.Profile);
    }

    [Fact]
    public async Task VerifyCommandRendersDebugAndProfileDiagnostics()
    {
        var parityResult = new ParityRunResult(
            new ParityOutcome("same", null),
            new ParityOutcome("same", null),
            "PRINT N'same';",
            new ParityDebugInfo(
                PlanStatementCount: 4,
                PlanOperatorCount: 12,
                MaximumPlanDepth: 3,
                EstimatedSubtreeCost: 0.125,
                CompileTimeMilliseconds: 2,
                CompileMemoryKilobytes: 128,
                HeapObjectsAllocated: 7,
                IndexedItemsAllocated: 15,
                DictionaryEntriesAllocated: 2),
            new ParityProfile(
                WarmupRuns: 1,
                CSharpSamples: [TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(2), TimeSpan.FromMilliseconds(3)],
                SqlServerSamples: [TimeSpan.FromMilliseconds(4), TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(6)]));
        var tester = CreateTester("Console.WriteLine(\"same\");", new StubParityRunner(parityResult));

        var result = await tester.RunAsync(
            ["verify", "--debug", "--profile"],
            TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Debug diagnostics", result.Output);
        Assert.Contains("12 operators", result.Output);
        Assert.Contains("7 objects", result.Output);
        Assert.Contains("Profile", result.Output);
        Assert.Contains("C#: 2 ms median", result.Output);
        Assert.Contains("SQL Server: 5 ms median", result.Output);
    }

    [Fact]
    public void RoutesLegacyArgumentsToTheTranspileCommand()
    {
        Assert.Equal(["transpile", "Program.cs", "--output", "Program.sql"],
            CliArgumentRouter.Route(["Program.cs", "--output", "Program.sql"]));
        Assert.Equal(["verify", "Program.cs"], CliArgumentRouter.Route(["verify", "Program.cs"]));
        Assert.Equal(["init", "App.csproj"], CliArgumentRouter.Route(["init", "App.csproj"]));
        Assert.Equal(["run", "App.csproj"], CliArgumentRouter.Route(["run", "App.csproj"]));
    }

    [Fact]
    public async Task InitDiscoversAndConfiguresAConsoleProject()
    {
        using var project = TemporaryProject.Create();
        var tester = CreateRoutedTester();

        var result = await tester.RunAsync(
            CliArgumentRouter.Route(["init", project.DirectoryPath, "--no-restore"]),
            TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Configured", result.Output);
        var document = XDocument.Load(project.ProjectPath);
        Assert.Equal("SharpSql.Sdk", Attribute(document, "PackageReference", "Include"));
        Assert.Equal(InitCommand.GetToolVersion(), Attribute(document, "PackageReference", "Version"));
        Assert.Equal("all", Element(document, "PrivateAssets"));
        Assert.Null(Element(document, "SharpSqlOutputPath"));
        Assert.Equal("BuildOutput", Element(document, "SharpSqlOutputLocation"));
        Assert.Equal("true", Element(document, "SharpSqlGenerateOnBuild"));
        Assert.Equal("true", Element(document, "SharpSqlEnableAnalyzer"));
        Assert.Equal("false", Element(document, "SharpSqlKeepContainer"));
        Assert.Equal("SharpSql", Element(document, "SharpSqlContainerDatabase"));
        var launchSettings = JsonNode.Parse(await File.ReadAllTextAsync(
            Path.Combine(project.DirectoryPath, "Properties", "launchSettings.json"),
            TestContext.Current.CancellationToken));
        Assert.Equal(
            "dotnet",
            launchSettings?["profiles"]?["SharpSql (SQL Server)"]?["executablePath"]?.GetValue<string>());
        Assert.Equal(
            ".",
            launchSettings?["profiles"]?["SharpSql (SQL Server)"]?["workingDirectory"]?.GetValue<string>());
        Assert.Equal(
            $"msbuild \"{Path.GetFileName(project.ProjectPath)}\" -t:SharpSqlRun --tl:off -verbosity:minimal",
            launchSettings?["profiles"]?["SharpSql (SQL Server)"]?["commandLineArgs"]?.GetValue<string>());
        Assert.DoesNotContain(
            project.DirectoryPath,
            launchSettings!.ToJsonString(),
            StringComparison.Ordinal);
        Assert.Equal("keep me", Element(document, "ExistingProperty"));
        Assert.Contains("existing comment", await File.ReadAllTextAsync(
            project.ProjectPath,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task InitReconfiguresAnExistingInstallationWithoutDuplicates()
    {
        using var project = TemporaryProject.Create("""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net10.0</TargetFramework>
                <SharpSqlOutputPath>old.sql</SharpSqlOutputPath>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="SharpSql.Sdk" Version="0.0.1" />
              </ItemGroup>
            </Project>
            """);
        var tester = CreateRoutedTester();

        var first = await tester.RunAsync(
            CliArgumentRouter.Route([
                "init", project.ProjectPath,
                "--sdk-version", "9.8.7",
                "--output", "$(MSBuildProjectDirectory)/generated/App.sql",
                "--entry", "Demo.SqlJob::Run",
                "--analyzer-only",
                "--no-analyzer",
                "--no-restore"
            ]),
            TestContext.Current.CancellationToken);
        var second = await tester.RunAsync(
            CliArgumentRouter.Route([
                "init", project.ProjectPath,
                "--sdk-version", "9.8.7",
                "--no-restore"
            ]),
            TestContext.Current.CancellationToken);

        Assert.Equal(0, first.ExitCode);
        Assert.Equal(0, second.ExitCode);
        var document = XDocument.Load(project.ProjectPath);
        Assert.Single(
            Elements(document, "PackageReference"),
            element => Attribute(element, "Include") == "SharpSql.Sdk");
        Assert.Equal("9.8.7", Attribute(document, "PackageReference", "Version"));
        Assert.Equal("$(MSBuildProjectDirectory)/generated/App.sql", Element(document, "SharpSqlOutputPath"));
        Assert.Equal("Demo.SqlJob::Run", Element(document, "SharpSqlEntryPoint"));
        Assert.Equal("true", Element(document, "SharpSqlGenerateOnBuild"));
        Assert.Equal("true", Element(document, "SharpSqlEnableAnalyzer"));
    }

    [Fact]
    public async Task InitRestoresTheConfiguredProject()
    {
        using var project = TemporaryProject.Create();
        var restorer = new StubProjectRestorer(exitCode: 0);
        var tester = CreateRoutedTester(projectRestorer: restorer);

        var result = await tester.RunAsync(
            CliArgumentRouter.Route(["init", project.ProjectPath]),
            TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(project.ProjectPath, restorer.ProjectPath);
        Assert.Contains("Restore completed", result.Output);
    }

    [Fact]
    public async Task InitRejectsNonConsoleProjectsWithoutChangingThem()
    {
        using var project = TemporaryProject.Create("""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);
        var original = await File.ReadAllTextAsync(project.ProjectPath, TestContext.Current.CancellationToken);
        var tester = CreateRoutedTester();

        var result = await tester.RunAsync(
            CliArgumentRouter.Route(["init", project.ProjectPath, "--no-restore"]),
            TestContext.Current.CancellationToken);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("console projects", result.Output);
        Assert.Equal(original, await File.ReadAllTextAsync(
            project.ProjectPath,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task InitUsesAVersionOverrideWithCentralPackageManagement()
    {
        using var project = TemporaryProject.Create();
        await File.WriteAllTextAsync(
            Path.Combine(project.DirectoryPath, "Directory.Packages.props"),
            """
            <Project>
              <PropertyGroup>
                <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
              </PropertyGroup>
            </Project>
            """,
            TestContext.Current.CancellationToken);
        var tester = CreateRoutedTester();

        var result = await tester.RunAsync(
            CliArgumentRouter.Route([
                "init", project.ProjectPath,
                "--sdk-version", "4.5.6",
                "--no-restore"
            ]),
            TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        var packageReference = Elements(XDocument.Load(project.ProjectPath), "PackageReference").Single();
        Assert.Null(Attribute(packageReference, "Version"));
        Assert.Equal("4.5.6", Attribute(packageReference, "VersionOverride"));
    }

    [Fact]
    public async Task InitPreservesLaunchProfilesAndConfiguresSqlServerDefaults()
    {
        using var project = TemporaryProject.Create();
        var propertiesDirectory = Path.Combine(project.DirectoryPath, "Properties");
        Directory.CreateDirectory(propertiesDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(propertiesDirectory, "launchSettings.json"),
            """
            {
              "profiles": {
                "Demo": {
                  "commandName": "Project"
                }
              }
            }
            """,
            TestContext.Current.CancellationToken);
        var tester = CreateRoutedTester();

        var result = await tester.RunAsync(
            CliArgumentRouter.Route([
                "init", project.ProjectPath,
                "--connection", "Development",
                "--keep-container",
                "--database", "DemoDev",
                "--timeout", "90",
                "--no-restore"
            ]),
            TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        var document = XDocument.Load(project.ProjectPath);
        Assert.Equal("Development", Element(document, "SharpSqlConnectionName"));
        Assert.Equal("true", Element(document, "SharpSqlKeepContainer"));
        Assert.Equal("DemoDev", Element(document, "SharpSqlContainerDatabase"));
        Assert.Equal("90", Element(document, "SharpSqlCommandTimeoutSeconds"));
        var launchSettings = JsonNode.Parse(await File.ReadAllTextAsync(
            Path.Combine(propertiesDirectory, "launchSettings.json"),
            TestContext.Current.CancellationToken));
        Assert.Equal("Project", launchSettings?["profiles"]?["Demo"]?["commandName"]?.GetValue<string>());
        Assert.Contains(
            "-t:SharpSqlRun",
            launchSettings?["profiles"]?["SharpSql (SQL Server)"]?["commandLineArgs"]?.GetValue<string>());
        Assert.Equal(
            ".",
            launchSettings?["profiles"]?["SharpSql (SQL Server)"]?["workingDirectory"]?.GetValue<string>());
    }

    [Fact]
    public async Task InitCanSkipTheIdeLaunchProfileAndSelectContainerMode()
    {
        using var project = TemporaryProject.Create("""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net10.0</TargetFramework>
                <SharpSqlConnectionName>OldConnection</SharpSqlConnectionName>
              </PropertyGroup>
            </Project>
            """);
        var tester = CreateRoutedTester();

        var result = await tester.RunAsync(
            CliArgumentRouter.Route([
                "init", project.ProjectPath,
                "--container",
                "--no-launch-profile",
                "--no-restore"
            ]),
            TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.Null(Element(XDocument.Load(project.ProjectPath), "SharpSqlConnectionName"));
        Assert.False(File.Exists(Path.Combine(project.DirectoryPath, "Properties", "launchSettings.json")));
    }

    [Fact]
    public async Task RunUsesProjectConnectionAndContainerSettings()
    {
        using var project = TemporaryProject.Create("""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net10.0</TargetFramework>
                <SharpSqlConnectionName>Development</SharpSqlConnectionName>
                <SharpSqlKeepContainer>true</SharpSqlKeepContainer>
                <SharpSqlContainerDatabase>DemoDev</SharpSqlContainerDatabase>
                <SharpSqlCommandTimeoutSeconds>75</SharpSqlCommandTimeoutSeconds>
              </PropertyGroup>
            </Project>
            """);
        var service = new StubSqlRunService(new SqlRunResult(
            true,
            "container abc",
            ["hello from sql"],
            [],
            ContainerKept: true));
        var tester = CreateRoutedTester(sqlRunService: service);

        var result = await tester.RunAsync(
            CliArgumentRouter.Route(["run", project.ProjectPath]),
            TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("hello from sql", result.Output);
        Assert.Contains("container kept running", result.Output);
        var request = Assert.IsType<SqlRunRequest>(service.LastRequest);
        Assert.Equal("Development", request.ConnectionName);
        Assert.True(request.KeepContainer);
        Assert.Equal("DemoDev", request.DatabaseName);
        Assert.Equal(75, request.CommandTimeoutSeconds);
        Assert.Equal(RuntimeStorageKind.Ephemeral, request.RuntimeStorage);
    }

    [Fact]
    public async Task RunAcceptsGeneratedSqlAndCommandLineOverrides()
    {
        var sqlPath = Path.Combine(Path.GetTempPath(), $"sharpsql-run-{Guid.NewGuid():N}.sql");
        await File.WriteAllTextAsync(sqlPath, "PRINT N'hello';", TestContext.Current.CancellationToken);
        try
        {
            var service = new StubSqlRunService(new SqlRunResult(true, "server/database", [], [], false));
            var tester = CreateRoutedTester(sqlRunService: service);

            var result = await tester.RunAsync(
                CliArgumentRouter.Route([
                    "run", sqlPath,
                    "--connection", "Staging",
                    "--connection-string-env", "DEMO_SQL",
                    "--container",
                    "--database", "Scratch",
                    "--remove-container",
                    "--timeout", "12",
                    "--runtime-storage", "ServiceBroker"
                ]),
                TestContext.Current.CancellationToken);

            Assert.Equal(0, result.ExitCode);
            var request = Assert.IsType<SqlRunRequest>(service.LastRequest);
            Assert.Equal("PRINT N'hello';", request.Sql);
            Assert.Equal("Staging", request.ConnectionName);
            Assert.Equal("DEMO_SQL", request.ConnectionStringEnvironmentVariable);
            Assert.True(request.ForceContainer);
            Assert.False(request.KeepContainer);
            Assert.Equal("Scratch", request.DatabaseName);
            Assert.Equal(12, request.CommandTimeoutSeconds);
            Assert.Equal(RuntimeStorageKind.ServiceBroker, request.RuntimeStorage);
        }
        finally
        {
            File.Delete(sqlPath);
        }
    }

    [Fact]
    public void SqlRunServiceProvisionsServiceBrokerBeforeExecutingTheProgram()
    {
        const string sql = "PRINT N'program';";

        var batches = SqlRunService.CreateExecutionBatches(RuntimeStorageKind.ServiceBroker, sql);

        Assert.Equal(2, batches.Count);
        Assert.Contains("CREATE QUEUE [SharpSql].[WorkerQueue]", batches[0]);
        Assert.Contains("PROCEDURE_NAME = [SharpSql].[DispatchWorker]", batches[0]);
        Assert.Equal(sql, batches[1]);
        Assert.Equal([sql], SqlRunService.CreateExecutionBatches(RuntimeStorageKind.Ephemeral, sql));
    }

    [Theory]
    [InlineData("", 0)]
    [InlineData("one", 1)]
    [InlineData("one\n", 1)]
    [InlineData("one\ntwo", 2)]
    [InlineData("one\ntwo\n", 2)]
    public void CountsGeneratedSqlLines(string sql, int expected)
    {
        Assert.Equal(expected, ParityRunResult.CountLines(sql));
    }

    private static CommandAppTester CreateTester(string standardInput = "", IParityRunner? parityRunner = null)
    {
        var tester = new CommandAppTester(new CommandAppTesterSettings { TrimConsoleOutput = false });
        var environment = new CliExecutionEnvironment(
            tester.Console,
            new StringReader(standardInput),
            ParityRunner: parityRunner);
        tester.SetDefaultCommand<TranspileCommand>(
            data: environment);
        tester.Configure(configurator => configurator.AddCommand<VerifyCommand>("verify").WithData(environment));
        return tester;
    }

    private static CommandAppTester CreateRoutedTester(
        IParityRunner? parityRunner = null,
        IProjectRestorer? projectRestorer = null,
        ISqlRunService? sqlRunService = null)
    {
        var tester = new CommandAppTester(new CommandAppTesterSettings { TrimConsoleOutput = false });
        var environment = new CliExecutionEnvironment(
            tester.Console,
            new StringReader(string.Empty),
            ParityRunner: parityRunner,
            ProjectRestorer: projectRestorer,
            SqlRunService: sqlRunService);
        tester.Configure(configurator =>
        {
            configurator.AddCommand<InitCommand>("init").WithData(environment);
            configurator.AddCommand<RunCommand>("run").WithData(environment);
            configurator.AddCommand<TranspileCommand>("transpile").WithData(environment);
            configurator.AddCommand<VerifyCommand>("verify").WithData(environment);
        });
        return tester;
    }

    private static IEnumerable<XElement> Elements(XDocument document, string localName) =>
        document.Descendants().Where(element => element.Name.LocalName == localName);

    private static string? Element(XDocument document, string localName) =>
        Elements(document, localName).LastOrDefault()?.Value;

    private static string? Attribute(XDocument document, string elementName, string attributeName) =>
        Attribute(Elements(document, elementName).Last(), attributeName);

    private static string? Attribute(XElement element, string attributeName) =>
        element.Attribute(attributeName)?.Value;

    private sealed class StubProjectRestorer(int exitCode) : IProjectRestorer
    {
        public string? ProjectPath { get; private set; }

        public Task<int> RestoreAsync(
            string projectPath,
            TextWriter output,
            TextWriter error,
            CancellationToken cancellationToken)
        {
            ProjectPath = projectPath;
            return Task.FromResult(exitCode);
        }
    }

    private sealed class StubSqlRunService(SqlRunResult result) : ISqlRunService
    {
        public SqlRunRequest? LastRequest { get; private set; }

        public Task<SqlRunResult> RunAsync(SqlRunRequest request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(result);
        }
    }

    private sealed class TemporaryProject : IDisposable
    {
        private TemporaryProject(string directoryPath, string projectPath)
        {
            DirectoryPath = directoryPath;
            ProjectPath = projectPath;
        }

        public string DirectoryPath { get; }
        public string ProjectPath { get; }

        public static TemporaryProject Create(string? contents = null)
        {
            var directoryPath = Path.Combine(Path.GetTempPath(), $"sharpsql-init-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directoryPath);
            var projectPath = Path.Combine(directoryPath, "Demo.csproj");
            File.WriteAllText(projectPath, contents ?? """
                <Project Sdk="Microsoft.NET.Sdk">
                  <!-- existing comment -->
                  <PropertyGroup>
                    <OutputType>Exe</OutputType>
                    <TargetFramework>net10.0</TargetFramework>
                    <ExistingProperty>keep me</ExistingProperty>
                  </PropertyGroup>
                </Project>
                """);
            return new TemporaryProject(directoryPath, projectPath);
        }

        public void Dispose() => Directory.Delete(DirectoryPath, recursive: true);
    }

    private sealed class StubParityRunner(ParityRunResult result) : IParityRunner
    {
        public ParityRunRequest? LastRequest { get; private set; }

        public Task<ParityRunResult> RunAsync(
            ParityRunRequest request,
            Action<ParityStageUpdate>? reportStage,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            reportStage?.Invoke(new ParityStageUpdate(ParityStage.Parsing));
            reportStage?.Invoke(new ParityStageUpdate(ParityStage.SqlGenerated));
            reportStage?.Invoke(new ParityStageUpdate(ParityStage.SqlGenerated, result.GeneratedSqlLineCount));
            reportStage?.Invoke(new ParityStageUpdate(ParityStage.EvaluatingCSharp));
            reportStage?.Invoke(new ParityStageUpdate(ParityStage.StartingSqlServer));
            reportStage?.Invoke(new ParityStageUpdate(ParityStage.EvaluatingSqlServer));
            return Task.FromResult(result);
        }
    }
}
