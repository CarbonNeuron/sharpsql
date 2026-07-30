using System.Text.Json;
using System.Text.Json.Nodes;

namespace SharpSql.Cli;

internal static class LaunchProfileInstaller
{
    private const string ProfileName = "SharpSql (SQL Server)";

    /// <summary>Adds or replaces the SharpSql SQL Server launch profile.</summary>
    public static void Install(string projectPath)
    {
        var projectDirectory = Path.GetDirectoryName(projectPath)!;
        var propertiesDirectory = Path.Combine(projectDirectory, "Properties");
        var launchSettingsPath = Path.Combine(propertiesDirectory, "launchSettings.json");
        var root = ReadLaunchSettings(launchSettingsPath);

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

        WriteAtomically(
            launchSettingsPath,
            root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
    }

    private static JsonObject ReadLaunchSettings(string launchSettingsPath)
    {
        try
        {
            return File.Exists(launchSettingsPath)
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
    }

    private static void WriteAtomically(string path, string contents)
    {
        var tempPath = Path.Combine(
            Path.GetDirectoryName(path)!,
            $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
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
