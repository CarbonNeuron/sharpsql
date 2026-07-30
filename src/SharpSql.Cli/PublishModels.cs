using SharpSql.SqlServer;

namespace SharpSql.Cli;

/// <summary>Describes a SharpSql application publication request.</summary>
public sealed record PublishRequest(
    string InputPath,
    string SchemaName,
    string ApplicationName,
    string Version,
    string? EntryPoint,
    string Configuration,
    string? TargetFramework,
    string? ConnectionName,
    string? ConnectionStringEnvironmentVariable,
    int CommandTimeoutSeconds,
    bool MemoryOptimized,
    bool EnableNativeKernels);

/// <summary>Reports the outcome of publishing a SharpSql application.</summary>
public sealed record PublishResult(
    bool Success,
    string SqlServer,
    IReadOnlyList<CompilerDiagnostic> Diagnostics,
    int? ErrorNumber = null,
    string? ErrorMessage = null);

/// <summary>Publishes compiled SharpSql applications to SQL Server.</summary>
public interface IPublishService
{
    /// <summary>Publishes the requested application and returns its result.</summary>
    Task<PublishResult> PublishAsync(PublishRequest request, CancellationToken cancellationToken);
}

/// <summary>Compiles and publishes SharpSql applications to SQL Server.</summary>
public sealed class PublishService : IPublishService
{
    /// <inheritdoc />
    public async Task<PublishResult> PublishAsync(
        PublishRequest request,
        CancellationToken cancellationToken)
    {
        var runtimeStorage = request.MemoryOptimized
            ? RuntimeStorageKind.MemoryOptimized
            : RuntimeStorageKind.Ephemeral;
        var options = new TranspileOptions
        {
            RuntimeStorage = runtimeStorage,
            EnableNativeKernels = request.EnableNativeKernels,
            ApplicationSchema = request.SchemaName
        };
        TranspileResult compilation;
        if (request.InputPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            compilation = await new SharpSqlProjectCompiler().TranspileAsync(
                request.InputPath,
                new ProjectTranspileOptions
                {
                    EntryPoint = request.EntryPoint,
                    Configuration = request.Configuration,
                    TargetFramework = request.TargetFramework,
                    CompilerOptions = options
                },
                cancellationToken);
        }
        else
        {
            var source = await File.ReadAllTextAsync(request.InputPath, cancellationToken);
            compilation = new SharpSqlCompiler().Transpile(source, options);
        }

        if (!compilation.Success)
            return new PublishResult(false, string.Empty, compilation.Diagnostics);

        var package = new SharpSqlApplicationPackage(
            request.SchemaName,
            request.ApplicationName,
            request.Version,
            compilation.Sql)
        {
            EntryProcedureName = "Run",
            RuntimeStorage = runtimeStorage,
            EnableNativeKernels = request.EnableNativeKernels
        };
        var connectionString = SqlServerConnectionResolver.Resolve(
            request.InputPath,
            request.ConnectionName,
            request.ConnectionStringEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "Publishing requires a persistent SQL Server connection. Configure --connection, " +
                "--connection-string-env, or SHARPSQL_CONNECTION_STRING.");
        }

        await using var session = await SqlServerSessionFactory.OpenAsync(
            new SqlServerSessionOptions(request.InputPath, connectionString),
            cancellationToken);
        var execution = await SqlBatchExecutor.ExecuteAsync(
            session.Connection,
            package.GenerateInstallSql(),
            request.CommandTimeoutSeconds,
            cancellationToken);
        return new PublishResult(
            execution.Success,
            session.Description,
            compilation.Diagnostics,
            execution.ErrorNumber,
            execution.ErrorMessage);
    }
}
