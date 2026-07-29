using SharpSql.SqlServer;
using Xunit;

namespace SharpSql.Tests;

public sealed class SqlServerConnectionResolverTests
{
    [Fact]
    public void ResolvesNamedConnectionsFromTheStandardEnvironmentVariable()
    {
        var name = $"SharpSqlTest{Guid.NewGuid():N}";
        var variable = $"ConnectionStrings__{name}";
        const string connectionString = "Server=example;Database=demo;Integrated Security=true";
        Environment.SetEnvironmentVariable(variable, connectionString);
        try
        {
            var projectPath = CreateProjectDirectory(out var directory);
            try
            {
                Assert.Equal(connectionString, SqlServerConnectionResolver.Resolve(projectPath, name));
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    [Fact]
    public void ResolvesNamedConnectionsFromAppSettings()
    {
        var projectPath = CreateProjectDirectory(out var directory);
        try
        {
            File.WriteAllText(
                Path.Combine(directory, "appsettings.json"),
                """
                {
                  "ConnectionStrings": {
                    "Development": "Server=example;Database=development"
                  }
                }
                """);

            Assert.Equal(
                "Server=example;Database=development",
                SqlServerConnectionResolver.Resolve(projectPath, "Development"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ReturnsNullWithoutAConfiguredConnectionSoContainersCanBeUsed()
    {
        var previous = Environment.GetEnvironmentVariable("SHARPSQL_CONNECTION_STRING");
        Environment.SetEnvironmentVariable("SHARPSQL_CONNECTION_STRING", null);
        try
        {
            var projectPath = CreateProjectDirectory(out var directory);
            try
            {
                Assert.Null(SqlServerConnectionResolver.Resolve(projectPath, connectionName: null));
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("SHARPSQL_CONNECTION_STRING", previous);
        }
    }

    private static string CreateProjectDirectory(out string directory)
    {
        directory = Path.Combine(Path.GetTempPath(), $"sharpsql-connection-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var projectPath = Path.Combine(directory, "Demo.csproj");
        File.WriteAllText(projectPath, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        return projectPath;
    }
}
