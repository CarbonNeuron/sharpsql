using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Spectre.Console;
using Spectre.Console.Cli;

namespace SharpSql.Cli;

[Description("Install and configure SharpSql.Sdk in a console project.")]
public sealed class InitCommand : AsyncCommand<InitCommand.Settings>
{
    internal const string DefaultOutputPath = "$(OutputPath)$(AssemblyName).sql";

    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "[PROJECT]")]
        [Description("A console .csproj or a directory containing one. Uses the current directory when omitted.")]
        public string? ProjectPath { get; init; }

        [CommandOption("-o|--output <PATH>")]
        [Description("MSBuild path for generated SQL. Defaults beside the compiled application.")]
        public string? OutputPath { get; init; }

        [CommandOption("--entry <METHOD>")]
        [Description("Entry method in Namespace.Type::Method form. The console entry point is used by default.")]
        public string? EntryPoint { get; init; }

        [CommandOption("--sdk-version <VERSION>")]
        [Description("SharpSql.Sdk package version. Defaults to the installed tool version.")]
        public string? SdkVersion { get; init; }

        [CommandOption("--analyzer-only")]
        [Description("Enable live diagnostics without generating SQL during normal builds.")]
        public bool AnalyzerOnly { get; init; }

        [CommandOption("--no-analyzer")]
        [Description("Disable live Roslyn compatibility diagnostics.")]
        public bool NoAnalyzer { get; init; }

        [CommandOption("--no-restore")]
        [Description("Update the project without running dotnet restore.")]
        public bool NoRestore { get; init; }

        [CommandOption("--connection <NAME>")]
        [Description("Connection string name. Without one, SQL runs use Testcontainers.")]
        public string? ConnectionName { get; init; }

        [CommandOption("--connection-string-env <VARIABLE>")]
        [Description("Environment variable containing the connection string.")]
        public string? ConnectionStringEnvironmentVariable { get; init; }

        [CommandOption("--container")]
        [Description("Use Testcontainers instead of a configured connection.")]
        public bool UseContainer { get; init; }

        [CommandOption("--keep-container")]
        [Description("Keep and reuse the fallback SQL Server Testcontainer.")]
        public bool KeepContainer { get; init; }

        [CommandOption("--database <DATABASE>")]
        [Description("Database created inside the fallback Testcontainer.")]
        [DefaultValue(RunCommand.DefaultDatabase)]
        public string DatabaseName { get; init; } = RunCommand.DefaultDatabase;

        [CommandOption("--image <IMAGE>")]
        [Description("Fallback SQL Server Testcontainer image.")]
        [DefaultValue(RunCommand.DefaultImage)]
        public string SqlServerImage { get; init; } = RunCommand.DefaultImage;

        [CommandOption("--timeout <SECONDS>")]
        [Description("SQL command timeout in seconds.")]
        [DefaultValue(60)]
        public int CommandTimeoutSeconds { get; init; } = 60;

        [CommandOption("--no-launch-profile")]
        [Description("Do not add the SharpSql (SQL Server) IDE launch profile.")]
        public bool NoLaunchProfile { get; init; }

        public override ValidationResult Validate()
        {
            if (string.IsNullOrWhiteSpace(OutputPath) && OutputPath is not null)
                return ValidationResult.Error("--output cannot be empty.");
            if (string.IsNullOrWhiteSpace(EntryPoint) && EntryPoint is not null)
                return ValidationResult.Error("--entry cannot be empty.");
            if (string.IsNullOrWhiteSpace(SdkVersion) && SdkVersion is not null)
                return ValidationResult.Error("--sdk-version cannot be empty.");
            if (string.IsNullOrWhiteSpace(ConnectionName) && ConnectionName is not null)
                return ValidationResult.Error("--connection cannot be empty.");
            if (string.IsNullOrWhiteSpace(ConnectionStringEnvironmentVariable) &&
                ConnectionStringEnvironmentVariable is not null)
                return ValidationResult.Error("--connection-string-env cannot be empty.");
            if (string.IsNullOrWhiteSpace(DatabaseName))
                return ValidationResult.Error("--database cannot be empty.");
            if (string.IsNullOrWhiteSpace(SqlServerImage))
                return ValidationResult.Error("--image cannot be empty.");
            if (SdkVersion is not null && !Regex.IsMatch(
                    SdkVersion,
                    "^[0-9]+\\.[0-9]+\\.[0-9]+(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?$",
                    RegexOptions.CultureInvariant))
                return ValidationResult.Error("--sdk-version must be a semantic version such as 1.2.3 or 1.2.3-preview.1.");
            if (CommandTimeoutSeconds <= 0)
                return ValidationResult.Error("--timeout must be greater than zero.");
            if (UseContainer && (ConnectionName is not null || ConnectionStringEnvironmentVariable is not null))
                return ValidationResult.Error("--container cannot be combined with connection options.");
            return ValidationResult.Success();
        }
    }

    protected override async Task<int> ExecuteAsync(
        CommandContext context,
        Settings settings,
        CancellationToken cancellationToken)
    {
        var environment = context.Data as CliExecutionEnvironment ??
                          new CliExecutionEnvironment(AnsiConsole.Console, Console.In, Console.Out, Console.Error);

        string projectPath;
        try
        {
            projectPath = ProjectSdkInstaller.ResolveProject(settings.ProjectPath, Environment.CurrentDirectory);
        }
        catch (ProjectInitializationException exception)
        {
            await WriteErrorAsync(environment, exception.Message, cancellationToken);
            return 1;
        }

        var version = settings.SdkVersion ?? GetToolVersion();
        ProjectSdkInstallation installation;
        try
        {
            installation = ProjectSdkInstaller.Install(
                projectPath,
                version,
                settings.OutputPath,
                settings.EntryPoint,
                settings.AnalyzerOnly,
                settings.NoAnalyzer,
                settings.ConnectionName,
                settings.ConnectionStringEnvironmentVariable,
                settings.UseContainer,
                settings.KeepContainer,
                settings.SqlServerImage,
                settings.DatabaseName,
                settings.CommandTimeoutSeconds,
                addLaunchProfile: !settings.NoLaunchProfile);
        }
        catch (ProjectInitializationException exception)
        {
            await WriteErrorAsync(environment, exception.Message, cancellationToken);
            return 1;
        }

        await WriteOutputAsync(
            environment,
            $"Configured {installation.ProjectPath}{Environment.NewLine}" +
            $"  SDK: SharpSql.Sdk {installation.SdkVersion}{Environment.NewLine}" +
            $"  SQL: {installation.OutputPath}{Environment.NewLine}" +
            $"  Build generation: {(installation.GenerateOnBuild ? "enabled" : "disabled")}{Environment.NewLine}" +
            $"  Live diagnostics: {(installation.EnableAnalyzer ? "enabled" : "disabled")}{Environment.NewLine}" +
            $"  SQL Server: {installation.SqlServerConfiguration}{Environment.NewLine}" +
            $"  IDE profile: {(installation.LaunchProfileAdded ? "SharpSql (SQL Server)" : "not changed")}{Environment.NewLine}",
            cancellationToken);

        if (settings.NoRestore)
            return 0;

        var restorer = environment.ProjectRestorer ?? new DotNetProjectRestorer();
        var restoreExitCode = await restorer.RestoreAsync(
            projectPath,
            environment.Output ?? Console.Out,
            environment.Error ?? Console.Error,
            cancellationToken);
        if (restoreExitCode == 0)
        {
            await WriteOutputAsync(environment, "Restore completed." + Environment.NewLine, cancellationToken);
            return 0;
        }

        await WriteErrorAsync(
            environment,
            $"dotnet restore failed with exit code {restoreExitCode}; the project remains configured.",
            cancellationToken);
        return restoreExitCode;
    }

    internal static string GetToolVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                   .InformationalVersion.Split('+', 2)[0] ??
               assembly.GetName().Version?.ToString(3) ??
               throw new InvalidOperationException("The SharpSql tool version could not be determined.");
    }

    private static Task WriteOutputAsync(
        CliExecutionEnvironment environment,
        string message,
        CancellationToken cancellationToken)
    {
        if (environment.Output is not null)
            return environment.Output.WriteAsync(message.AsMemory(), cancellationToken);
        environment.Console.Write(new Text(message));
        return Task.CompletedTask;
    }

    private static Task WriteErrorAsync(
        CliExecutionEnvironment environment,
        string message,
        CancellationToken cancellationToken)
    {
        if (environment.Error is not null)
            return environment.Error.WriteLineAsync(message.AsMemory(), cancellationToken);
        environment.Console.MarkupLine($"[red]{Markup.Escape(message)}[/]");
        return Task.CompletedTask;
    }
}

internal sealed record ProjectSdkInstallation(
    string ProjectPath,
    string SdkVersion,
    string OutputPath,
    bool GenerateOnBuild,
    bool EnableAnalyzer,
    string SqlServerConfiguration,
    bool LaunchProfileAdded);

internal sealed class ProjectInitializationException(string message) : Exception(message);

internal static class ProjectSdkInstaller
{
    public static string ResolveProject(string? requestedPath, string currentDirectory)
    {
        var candidate = requestedPath is null
            ? currentDirectory
            : Path.GetFullPath(requestedPath, currentDirectory);

        if (Directory.Exists(candidate))
        {
            var projects = Directory.GetFiles(candidate, "*.csproj", SearchOption.TopDirectoryOnly);
            return projects.Length switch
            {
                0 => throw new ProjectInitializationException($"No .csproj file was found in {candidate}."),
                1 => Path.GetFullPath(projects[0]),
                _ => throw new ProjectInitializationException(
                    $"Multiple .csproj files were found in {candidate}; specify the project explicitly.")
            };
        }

        if (!candidate.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            throw new ProjectInitializationException($"Expected a .csproj file or project directory: {candidate}");
        if (!File.Exists(candidate))
            throw new ProjectInitializationException($"Project file was not found: {candidate}");
        return candidate;
    }

    public static ProjectSdkInstallation Install(
        string projectPath,
        string sdkVersion,
        string? requestedOutputPath,
        string? entryPoint,
        bool analyzerOnly,
        bool noAnalyzer,
        string? connectionName = null,
        string? connectionStringEnvironmentVariable = null,
        bool useContainer = false,
        bool keepContainer = false,
        string sqlServerImage = RunCommand.DefaultImage,
        string databaseName = RunCommand.DefaultDatabase,
        int commandTimeoutSeconds = 60,
        bool addLaunchProfile = true)
    {
        XDocument document;
        try
        {
            document = XDocument.Load(projectPath, LoadOptions.None);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            throw new ProjectInitializationException($"Could not read project '{projectPath}': {exception.Message}");
        }

        var root = document.Root;
        if (root is null || root.Name.LocalName != "Project" || !IsSdkStyle(root))
            throw new ProjectInitializationException("SharpSql init requires an SDK-style .NET project.");

        var outputType = Descendants(root, "OutputType").LastOrDefault()?.Value.Trim();
        if (!string.Equals(outputType, "Exe", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(outputType, "WinExe", StringComparison.OrdinalIgnoreCase))
        {
            throw new ProjectInitializationException(
                "SharpSql init currently supports console projects (OutputType Exe or WinExe).");
        }

        var centrallyManaged = UsesCentralPackageManagement(projectPath, root);
        var packageReference = Descendants(root, "PackageReference").FirstOrDefault(IsSharpSqlSdkReference);
        if (packageReference is null)
        {
            var itemGroup = new XElement(root.Name.Namespace + "ItemGroup",
                new XAttribute("Label", "SharpSql"));
            packageReference = new XElement(root.Name.Namespace + "PackageReference",
                new XAttribute("Include", "SharpSql.Sdk"),
                new XAttribute(centrallyManaged ? "VersionOverride" : "Version", sdkVersion),
                new XElement(root.Name.Namespace + "PrivateAssets", "all"));
            itemGroup.Add(packageReference);
            root.Add(itemGroup);
        }
        else
        {
            SetPackageVersion(packageReference, sdkVersion, centrallyManaged);
            SetPackagePrivateAssets(packageReference);
        }

        var existingOutputPath = Descendants(root, "SharpSqlOutputPath").LastOrDefault()?.Value.Trim();
        var outputPath = requestedOutputPath ?? existingOutputPath ?? InitCommand.DefaultOutputPath;
        SetProperty(root, "SharpSqlEnabled", "true");
        if (requestedOutputPath is not null)
            SetProperty(root, "SharpSqlOutputPath", requestedOutputPath);
        else if (existingOutputPath is null)
            SetProperty(root, "SharpSqlOutputLocation", "BuildOutput");
        SetProperty(root, "SharpSqlGenerateOnBuild", analyzerOnly ? "false" : "true");
        SetProperty(root, "SharpSqlEnableAnalyzer", noAnalyzer ? "false" : "true");
        SetProperty(root, "SharpSqlKeepContainer", keepContainer ? "true" : "false");
        SetProperty(root, "SharpSqlContainerImage", sqlServerImage);
        SetProperty(root, "SharpSqlContainerDatabase", databaseName);
        SetProperty(root, "SharpSqlCommandTimeoutSeconds", commandTimeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (entryPoint is not null)
            SetProperty(root, "SharpSqlEntryPoint", entryPoint);
        var existingConnectionName = Descendants(root, "SharpSqlConnectionName").LastOrDefault()?.Value.Trim();
        var existingConnectionEnvironment = Descendants(root, "SharpSqlConnectionStringEnvironment").LastOrDefault()?.Value.Trim();
        if (useContainer)
        {
            RemoveProperty(root, "SharpSqlConnectionName");
            RemoveProperty(root, "SharpSqlConnectionStringEnvironment");
        }
        else if (connectionName is not null)
            SetProperty(root, "SharpSqlConnectionName", connectionName);
        if (connectionStringEnvironmentVariable is not null)
            SetProperty(root, "SharpSqlConnectionStringEnvironment", connectionStringEnvironmentVariable);

        WriteAtomically(projectPath, document);
        if (addLaunchProfile)
            LaunchProfileInstaller.Install(projectPath);
        return new ProjectSdkInstallation(
            projectPath,
            sdkVersion,
            outputPath,
            GenerateOnBuild: !analyzerOnly,
            EnableAnalyzer: !noAnalyzer,
            SqlServerConfiguration: DescribeSqlServerConfiguration(
                useContainer,
                connectionName ?? existingConnectionName,
                connectionStringEnvironmentVariable ?? existingConnectionEnvironment),
            LaunchProfileAdded: addLaunchProfile);
    }

    private static bool IsSdkStyle(XElement root) =>
        root.Attribute("Sdk") is not null || root.Elements().Any(element => element.Name.LocalName == "Sdk");

    private static IEnumerable<XElement> Descendants(XElement root, string localName) =>
        root.Descendants().Where(element => element.Name.LocalName == localName);

    private static bool IsSharpSqlSdkReference(XElement element)
    {
        var identity = element.Attribute("Include")?.Value ?? element.Attribute("Update")?.Value;
        return string.Equals(identity, "SharpSql.Sdk", StringComparison.OrdinalIgnoreCase);
    }

    private static bool UsesCentralPackageManagement(string projectPath, XElement projectRoot)
    {
        if (Descendants(projectRoot, "ManagePackageVersionsCentrally")
            .Any(element => string.Equals(element.Value.Trim(), "true", StringComparison.OrdinalIgnoreCase)))
            return true;

        var directory = Path.GetDirectoryName(projectPath);
        while (directory is not null)
        {
            var centralFile = Path.Combine(directory, "Directory.Packages.props");
            if (File.Exists(centralFile))
            {
                try
                {
                    var centralDocument = XDocument.Load(centralFile);
                    return centralDocument.Descendants()
                        .Any(element =>
                            element.Name.LocalName == "ManagePackageVersionsCentrally" &&
                            string.Equals(element.Value.Trim(), "true", StringComparison.OrdinalIgnoreCase));
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Xml.XmlException)
                {
                    throw new ProjectInitializationException(
                        $"Could not read central package file '{centralFile}': {exception.Message}");
                }
            }
            directory = Directory.GetParent(directory)?.FullName;
        }
        return false;
    }

    private static void SetPackageVersion(XElement packageReference, string version, bool centrallyManaged)
    {
        var versionName = centrallyManaged ? "VersionOverride" : "Version";
        var obsoleteVersionName = centrallyManaged ? "Version" : "VersionOverride";
        packageReference.Attribute(obsoleteVersionName)?.Remove();
        foreach (var obsoleteElement in packageReference.Elements()
                     .Where(element => element.Name.LocalName == obsoleteVersionName).ToArray())
            obsoleteElement.Remove();

        var versionElement = packageReference.Elements()
            .FirstOrDefault(element => element.Name.LocalName == versionName);
        if (versionElement is not null)
        {
            versionElement.Value = version;
            packageReference.Attribute(versionName)?.Remove();
            return;
        }
        packageReference.SetAttributeValue(versionName, version);
    }

    private static void SetPackagePrivateAssets(XElement packageReference)
    {
        var privateAssetsElement = packageReference.Elements()
            .FirstOrDefault(element => element.Name.LocalName == "PrivateAssets");
        if (privateAssetsElement is not null)
            privateAssetsElement.Value = "all";
        else if (packageReference.Attribute("PrivateAssets") is not null)
            packageReference.SetAttributeValue("PrivateAssets", "all");
        else
            packageReference.Add(new XElement(packageReference.Name.Namespace + "PrivateAssets", "all"));
    }

    private static void SetProperty(XElement root, string name, string value)
    {
        var existing = Descendants(root, name).LastOrDefault();
        if (existing is not null)
        {
            existing.Value = value;
            return;
        }

        var propertyGroup = root.Elements()
            .FirstOrDefault(element =>
                element.Name.LocalName == "PropertyGroup" &&
                string.Equals(element.Attribute("Label")?.Value, "SharpSql", StringComparison.Ordinal));
        if (propertyGroup is null)
        {
            propertyGroup = new XElement(root.Name.Namespace + "PropertyGroup", new XAttribute("Label", "SharpSql"));
            root.Add(propertyGroup);
        }
        propertyGroup.Add(new XElement(root.Name.Namespace + name, value));
    }

    private static void RemoveProperty(XElement root, string name)
    {
        foreach (var property in Descendants(root, name).ToArray())
            property.Remove();
    }

    private static string DescribeSqlServerConfiguration(
        bool useContainer,
        string? connectionName,
        string? connectionStringEnvironmentVariable)
    {
        if (useContainer)
            return "Testcontainers fallback";
        if (!string.IsNullOrWhiteSpace(connectionStringEnvironmentVariable))
            return $"environment '{connectionStringEnvironmentVariable}'";
        return string.IsNullOrWhiteSpace(connectionName)
            ? "Testcontainers fallback"
            : $"connection '{connectionName}'";
    }

    private static void WriteAtomically(string projectPath, XDocument document)
    {
        var tempPath = Path.Combine(
            Path.GetDirectoryName(projectPath)!,
            $".{Path.GetFileName(projectPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false)))
                document.Save(writer, SaveOptions.None);
            File.Move(tempPath, projectPath, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new ProjectInitializationException($"Could not update project '{projectPath}': {exception.Message}");
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }
}

internal static class LaunchProfileInstaller
{
    private const string ProfileName = "SharpSql (SQL Server)";

    public static void Install(string projectPath)
    {
        var projectDirectory = Path.GetDirectoryName(projectPath)!;
        var propertiesDirectory = Path.Combine(projectDirectory, "Properties");
        var launchSettingsPath = Path.Combine(propertiesDirectory, "launchSettings.json");
        JsonObject root;
        try
        {
            root = File.Exists(launchSettingsPath)
                ? JsonNode.Parse(
                      File.ReadAllText(launchSettingsPath),
                      documentOptions: new JsonDocumentOptions
                      {
                          AllowTrailingCommas = true,
                          CommentHandling = JsonCommentHandling.Skip
                      }) as JsonObject ?? throw new ProjectInitializationException(
                          $"Launch settings root must be a JSON object: {launchSettingsPath}")
                : new JsonObject
                {
                    ["$schema"] = "http://json.schemastore.org/launchsettings.json"
                };
        }
        catch (JsonException exception)
        {
            throw new ProjectInitializationException(
                $"Could not read launch settings '{launchSettingsPath}': {exception.Message}");
        }

        var profiles = root["profiles"] as JsonObject;
        if (profiles is null)
        {
            profiles = new JsonObject();
            root["profiles"] = profiles;
        }
        profiles[ProfileName] = new JsonObject
        {
            ["commandName"] = "Executable",
            ["executablePath"] = "dotnet",
            ["commandLineArgs"] =
                $"msbuild \"{Path.GetFileName(projectPath)}\" -t:SharpSqlRun --tl:off -verbosity:minimal",
            ["workingDirectory"] = "."
        };

        Directory.CreateDirectory(propertiesDirectory);
        WriteAtomically(launchSettingsPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
    }

    private static void WriteAtomically(string path, string contents)
    {
        var tempPath = Path.Combine(Path.GetDirectoryName(path)!, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(tempPath, contents, new System.Text.UTF8Encoding(false));
            File.Move(tempPath, path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new ProjectInitializationException($"Could not update launch settings '{path}': {exception.Message}");
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }
}

public interface IProjectRestorer
{
    Task<int> RestoreAsync(
        string projectPath,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken);
}

internal sealed class DotNetProjectRestorer : IProjectRestorer
{
    public async Task<int> RestoreAsync(
        string projectPath,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("dotnet")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            }
        };
        process.StartInfo.ArgumentList.Add("restore");
        process.StartInfo.ArgumentList.Add(projectPath);
        process.Start();

        var outputTask = CopyAsync(process.StandardOutput, output, cancellationToken);
        var errorTask = CopyAsync(process.StandardError, error, cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            throw;
        }
        await Task.WhenAll(outputTask, errorTask);
        return process.ExitCode;
    }

    private static async Task CopyAsync(
        TextReader reader,
        TextWriter writer,
        CancellationToken cancellationToken)
    {
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await writer.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
            await writer.FlushAsync(cancellationToken);
        }
    }
}
