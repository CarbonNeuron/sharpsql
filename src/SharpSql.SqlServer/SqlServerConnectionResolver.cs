using System.Text.Json;
using System.Xml.Linq;

namespace SharpSql.SqlServer;

/// <summary>Resolves named SQL Server connections from standard .NET configuration sources.</summary>
public static class SqlServerConnectionResolver
{
    /// <summary>Resolves the connection string configured for a project.</summary>
    /// <param name="projectPath">The project file or project-scoped path.</param>
    /// <param name="connectionName">The optional named connection.</param>
    /// <param name="connectionStringEnvironmentVariable">An optional environment variable override.</param>
    /// <returns>The resolved connection string, or <see langword="null"/> when no unnamed connection is configured.</returns>
    public static string? Resolve(
        string projectPath,
        string? connectionName,
        string? connectionStringEnvironmentVariable = null)
    {
        if (!string.IsNullOrWhiteSpace(connectionStringEnvironmentVariable))
        {
            return Environment.GetEnvironmentVariable(connectionStringEnvironmentVariable) ??
                   throw new InvalidOperationException(
                       $"Environment variable '{connectionStringEnvironmentVariable}' does not contain a connection string.");
        }

        if (string.IsNullOrWhiteSpace(connectionName))
            return Environment.GetEnvironmentVariable("SHARPSQL_CONNECTION_STRING");

        var projectDirectory = Path.GetDirectoryName(Path.GetFullPath(projectPath))!;
        var value = ReadConnectionString(Path.Combine(projectDirectory, "appsettings.json"), connectionName);
        var environmentName = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ??
                              Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        if (!string.IsNullOrWhiteSpace(environmentName))
        {
            value = ReadConnectionString(
                        Path.Combine(projectDirectory, $"appsettings.{environmentName}.json"),
                        connectionName) ??
                    value;
        }

        if (projectPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            value = ReadUserSecretsConnectionString(projectPath, connectionName) ?? value;
        value = Environment.GetEnvironmentVariable($"ConnectionStrings__{connectionName}") ?? value;
        return value ?? throw new InvalidOperationException(
            $"Connection string '{connectionName}' was not found. Set ConnectionStrings__{connectionName}, " +
            "configure appsettings, or remove SharpSqlConnectionName to use Testcontainers.");
    }

    private static string? ReadUserSecretsConnectionString(string projectPath, string connectionName)
    {
        var project = XDocument.Load(projectPath);
        var userSecretsId = project.Descendants()
            .LastOrDefault(element => element.Name.LocalName == "UserSecretsId")?
            .Value.Trim();
        if (string.IsNullOrWhiteSpace(userSecretsId))
            return null;

        string secretsPath;
        if (OperatingSystem.IsWindows())
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            secretsPath = Path.Combine(appData, "Microsoft", "UserSecrets", userSecretsId, "secrets.json");
        }
        else
        {
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            secretsPath = Path.Combine(profile, ".microsoft", "usersecrets", userSecretsId, "secrets.json");
        }
        return ReadConnectionString(secretsPath, connectionName);
    }

    private static string? ReadConnectionString(string path, string connectionName)
    {
        if (!File.Exists(path))
            return null;
        try
        {
            using var document = JsonDocument.Parse(
                File.ReadAllText(path),
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip
                });
            if (TryGetProperty(document.RootElement, $"ConnectionStrings:{connectionName}", out var flatValue))
                return flatValue.GetString();
            if (TryGetProperty(document.RootElement, "ConnectionStrings", out var connectionStrings) &&
                connectionStrings.ValueKind == JsonValueKind.Object &&
                TryGetProperty(connectionStrings, connectionName, out var nestedValue))
                return nestedValue.GetString();
            return null;
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException($"Could not read connection strings from '{path}': {exception.Message}", exception);
        }
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }
        value = default;
        return false;
    }
}
