using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Docker.DotNet.Models;
using DotNet.Testcontainers.Configurations;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Testcontainers.MsSql;

namespace SharpSql.SqlServer;

public sealed record SqlServerSessionOptions(
    string ScopePath,
    string? ConnectionString = null,
    string Image = "mcr.microsoft.com/mssql/server:2022-latest",
    string DatabaseName = "SharpSql",
    bool KeepContainer = false);

public sealed class SqlServerSession : IAsyncDisposable
{
    private readonly string? _containerId;
    private readonly bool _keepContainer;

    internal SqlServerSession(
        SqlConnection connection,
        string description,
        bool isContainer,
        string? containerId,
        bool keepContainer)
    {
        Connection = connection;
        Description = description;
        IsContainer = isContainer;
        _containerId = containerId;
        _keepContainer = keepContainer;
    }

    public SqlConnection Connection { get; }
    public string Description { get; }
    public bool IsContainer { get; }
    public bool KeepContainer => IsContainer && _keepContainer;

    public async ValueTask DisposeAsync()
    {
        try
        {
            await Connection.DisposeAsync();
        }
        finally
        {
            if (!_keepContainer && _containerId is not null)
                await RemoveContainerAsync(_containerId, CancellationToken.None);
        }
    }

    private static async Task RemoveContainerAsync(string containerId, CancellationToken cancellationToken)
    {
        using var dockerClient = TestcontainersSettings.OS.DockerEndpointAuthConfig
            .GetDockerClientBuilder(Guid.Empty)
            .Build();
        await dockerClient.Containers.RemoveContainerAsync(
            containerId,
            new ContainerRemoveParameters { Force = true, RemoveVolumes = true },
            cancellationToken);
    }
}

public static partial class SqlServerSessionFactory
{
    private const string ReusableLabel = "io.sharpsql.sqlserver.reusable";
    private const string ScopeLabel = "io.sharpsql.sqlserver.scope";

    public static async Task<SqlServerSession> OpenAsync(
        SqlServerSessionOptions options,
        CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            var connection = new SqlConnection(options.ConnectionString)
            {
                FireInfoMessageEventOnUserErrors = true
            };
            await connection.OpenAsync(cancellationToken);
            var details = new SqlConnectionStringBuilder(options.ConnectionString);
            var database = string.IsNullOrWhiteSpace(details.InitialCatalog) ? connection.Database : details.InitialCatalog;
            return new SqlServerSession(
                connection,
                $"{details.DataSource}/{database}",
                isContainer: false,
                containerId: null,
                keepContainer: false);
        }

        ValidateDatabaseName(options.DatabaseName);
        var scope = ScopeId(options.ScopePath);
        var container = new MsSqlBuilder(options.Image)
            .WithLogger(NullLogger.Instance)
            .WithReuse(true)
            .WithLabel(ReusableLabel, "true")
            .WithLabel(ScopeLabel, scope)
            .WithLabel("io.sharpsql.sqlserver.database", options.DatabaseName)
            .Build();
        try
        {
            await container.StartAsync(cancellationToken);

            var masterConnectionString = container.GetConnectionString();
            await EnsureDatabaseAsync(masterConnectionString, options.DatabaseName, cancellationToken);
            var connectionString = new SqlConnectionStringBuilder(masterConnectionString)
            {
                InitialCatalog = options.DatabaseName
            }.ConnectionString;
            var sqlConnection = new SqlConnection(connectionString)
            {
                FireInfoMessageEventOnUserErrors = true
            };
            await sqlConnection.OpenAsync(cancellationToken);
            var source = new SqlConnectionStringBuilder(connectionString).DataSource;
            return new SqlServerSession(
                sqlConnection,
                $"container {container.Id[..Math.Min(12, container.Id.Length)]} ({source}/{options.DatabaseName})",
                isContainer: true,
                container.Id,
                options.KeepContainer);
        }
        catch
        {
            if (!options.KeepContainer && !string.IsNullOrWhiteSpace(container.Id))
                await RemoveContainerAfterFailureAsync(container.Id);
            throw;
        }
    }

    private static async Task EnsureDatabaseAsync(
        string connectionString,
        string databaseName,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"IF DB_ID(N'{databaseName}') IS NULL CREATE DATABASE [{databaseName}];";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void ValidateDatabaseName(string databaseName)
    {
        if (string.IsNullOrWhiteSpace(databaseName) || !DatabaseNamePattern().IsMatch(databaseName))
            throw new InvalidOperationException(
                "The container database name must contain only letters, numbers, underscores, dashes, or periods.");
    }

    private static async Task RemoveContainerAfterFailureAsync(string containerId)
    {
        try
        {
            using var dockerClient = TestcontainersSettings.OS.DockerEndpointAuthConfig
                .GetDockerClientBuilder(Guid.Empty)
                .Build();
            await dockerClient.Containers.RemoveContainerAsync(
                containerId,
                new ContainerRemoveParameters { Force = true, RemoveVolumes = true },
                CancellationToken.None);
        }
        catch
        {
            // Preserve the startup or connection failure that triggered cleanup.
        }
    }

    private static string ScopeId(string path)
    {
        var normalized = Path.GetFullPath(path);
        if (OperatingSystem.IsWindows())
            normalized = normalized.ToUpperInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(hash.AsSpan(0, 12)).ToLowerInvariant();
    }

    [GeneratedRegex("^[A-Za-z0-9_.-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex DatabaseNamePattern();
}
