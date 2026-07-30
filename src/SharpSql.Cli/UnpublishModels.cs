using SharpSql.SqlServer;

namespace SharpSql.Cli;

/// <summary>Describes removal of an installed SharpSql application.</summary>
public sealed record UnpublishRequest(
    string SchemaName,
    string ApplicationName,
    string? ConnectionName,
    string? ConnectionStringEnvironmentVariable,
    int CommandTimeoutSeconds);

/// <summary>Reports the outcome of removing an installed application.</summary>
public sealed record UnpublishResult(
    bool Success,
    string SqlServer,
    int? ErrorNumber = null,
    string? ErrorMessage = null);

/// <summary>Removes installed SharpSql applications from SQL Server.</summary>
public interface IUnpublishService
{
    /// <summary>Removes the requested application and returns its result.</summary>
    Task<UnpublishResult> UnpublishAsync(UnpublishRequest request, CancellationToken cancellationToken);
}

/// <summary>Removes package-owned objects while retaining the application schema.</summary>
public sealed class UnpublishService : IUnpublishService
{
    /// <inheritdoc />
    public async Task<UnpublishResult> UnpublishAsync(
        UnpublishRequest request,
        CancellationToken cancellationToken)
    {
        var scopePath = Path.Combine(Environment.CurrentDirectory, "sharpsql.unpublish");
        var connectionString = SqlServerConnectionResolver.Resolve(
            scopePath,
            request.ConnectionName,
            request.ConnectionStringEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "Unpublishing requires a persistent SQL Server connection. Configure --connection, " +
                "--connection-string-env, or SHARPSQL_CONNECTION_STRING.");
        }

        await using var session = await SqlServerSessionFactory.OpenAsync(
            new SqlServerSessionOptions(scopePath, connectionString),
            cancellationToken);
        var execution = await SqlBatchExecutor.ExecuteAsync(
            session.Connection,
            SharpSqlApplicationPackage.GenerateUninstallSql(request.SchemaName, request.ApplicationName),
            request.CommandTimeoutSeconds,
            cancellationToken);
        return new UnpublishResult(
            execution.Success,
            session.Description,
            execution.ErrorNumber,
            execution.ErrorMessage);
    }
}
