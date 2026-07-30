using System.Globalization;
using System.Xml.Linq;

namespace SharpSql.Cli;

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
    /// <summary>Resolves an explicit project path or discovers one in a directory.</summary>
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

    /// <summary>Installs and configures the SharpSql SDK package in a project file.</summary>
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
        var document = LoadProject(projectPath);
        var root = document.Root;
        if (root is null || root.Name.LocalName != "Project" || !IsSdkStyle(root))
            throw new ProjectInitializationException("SharpSql init requires an SDK-style .NET project.");

        ValidateOutputType(root);
        InstallPackageReference(projectPath, root, sdkVersion);

        var existingOutputPath = Descendants(root, "SharpSqlOutputPath").LastOrDefault()?.Value.Trim();
        var outputPath = requestedOutputPath ?? existingOutputPath ?? InitCommand.DefaultOutputPath;
        ConfigureBuild(
            root,
            requestedOutputPath,
            existingOutputPath,
            entryPoint,
            analyzerOnly,
            noAnalyzer);

        var existingConnectionName = Descendants(root, "SharpSqlConnectionName").LastOrDefault()?.Value.Trim();
        var existingConnectionEnvironment = Descendants(root, "SharpSqlConnectionStringEnvironment")
            .LastOrDefault()?.Value.Trim();
        ConfigureSqlServer(
            root,
            connectionName,
            connectionStringEnvironmentVariable,
            useContainer,
            keepContainer,
            sqlServerImage,
            databaseName,
            commandTimeoutSeconds);

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

    private static XDocument LoadProject(string projectPath)
    {
        try
        {
            return XDocument.Load(projectPath, LoadOptions.None);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            throw new ProjectInitializationException($"Could not read project '{projectPath}': {exception.Message}");
        }
    }

    private static void ValidateOutputType(XElement root)
    {
        var outputType = Descendants(root, "OutputType").LastOrDefault()?.Value.Trim();
        if (!string.Equals(outputType, "Exe", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(outputType, "WinExe", StringComparison.OrdinalIgnoreCase))
        {
            throw new ProjectInitializationException(
                "SharpSql init currently supports console projects (OutputType Exe or WinExe).");
        }
    }

    private static void InstallPackageReference(string projectPath, XElement root, string sdkVersion)
    {
        var centrallyManaged = UsesCentralPackageManagement(projectPath, root);
        var packageReference = Descendants(root, "PackageReference").FirstOrDefault(IsSharpSqlSdkReference);
        if (packageReference is null)
        {
            var itemGroup = new XElement(
                root.Name.Namespace + "ItemGroup",
                new XAttribute("Label", "SharpSql"));
            packageReference = new XElement(
                root.Name.Namespace + "PackageReference",
                new XAttribute("Include", "SharpSql.Sdk"),
                new XAttribute(centrallyManaged ? "VersionOverride" : "Version", sdkVersion),
                new XElement(root.Name.Namespace + "PrivateAssets", "all"));
            itemGroup.Add(packageReference);
            root.Add(itemGroup);
            return;
        }

        SetPackageVersion(packageReference, sdkVersion, centrallyManaged);
        SetPackagePrivateAssets(packageReference);
    }

    private static void ConfigureBuild(
        XElement root,
        string? requestedOutputPath,
        string? existingOutputPath,
        string? entryPoint,
        bool analyzerOnly,
        bool noAnalyzer)
    {
        SetProperty(root, "SharpSqlEnabled", "true");
        if (requestedOutputPath is not null)
            SetProperty(root, "SharpSqlOutputPath", requestedOutputPath);
        else if (existingOutputPath is null)
            SetProperty(root, "SharpSqlOutputLocation", "BuildOutput");
        SetProperty(root, "SharpSqlGenerateOnBuild", analyzerOnly ? "false" : "true");
        SetProperty(root, "SharpSqlEnableAnalyzer", noAnalyzer ? "false" : "true");
        if (entryPoint is not null)
            SetProperty(root, "SharpSqlEntryPoint", entryPoint);
    }

    private static void ConfigureSqlServer(
        XElement root,
        string? connectionName,
        string? connectionStringEnvironmentVariable,
        bool useContainer,
        bool keepContainer,
        string sqlServerImage,
        string databaseName,
        int commandTimeoutSeconds)
    {
        SetProperty(root, "SharpSqlKeepContainer", keepContainer ? "true" : "false");
        SetProperty(root, "SharpSqlContainerImage", sqlServerImage);
        SetProperty(root, "SharpSqlContainerDatabase", databaseName);
        SetProperty(
            root,
            "SharpSqlCommandTimeoutSeconds",
            commandTimeoutSeconds.ToString(CultureInfo.InvariantCulture));
        if (useContainer)
        {
            RemoveProperty(root, "SharpSqlConnectionName");
            RemoveProperty(root, "SharpSqlConnectionStringEnvironment");
        }
        else if (connectionName is not null)
        {
            SetProperty(root, "SharpSqlConnectionName", connectionName);
        }
        if (connectionStringEnvironmentVariable is not null)
            SetProperty(root, "SharpSqlConnectionStringEnvironment", connectionStringEnvironmentVariable);
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
                return CentralPackageManagementEnabled(centralFile);
            directory = Directory.GetParent(directory)?.FullName;
        }
        return false;
    }

    private static bool CentralPackageManagementEnabled(string centralFile)
    {
        try
        {
            return XDocument.Load(centralFile).Descendants()
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
