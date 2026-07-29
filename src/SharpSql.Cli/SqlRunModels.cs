using SharpSql.SqlServer;

namespace SharpSql.Cli;

public sealed record SqlRunRequest(
    string InputPath,
    string? Sql,
    string? EntryPoint,
    string Configuration,
    string? TargetFramework,
    string? ConnectionName,
    string? ConnectionStringEnvironmentVariable,
    bool ForceContainer,
    bool KeepContainer,
    string SqlServerImage,
    string DatabaseName,
    int CommandTimeoutSeconds)
{
    public bool IsProject => Sql is null;
}

public sealed record SqlRunResult(
    bool Success,
    string SqlServer,
    IReadOnlyList<string> Messages,
    IReadOnlyList<CompilerDiagnostic> Diagnostics,
    bool ContainerKept,
    int? ErrorNumber = null,
    string? ErrorMessage = null);

public interface ISqlRunService
{
    Task<SqlRunResult> RunAsync(SqlRunRequest request, CancellationToken cancellationToken);
}

public sealed class SqlRunService : ISqlRunService
{
    public async Task<SqlRunResult> RunAsync(SqlRunRequest request, CancellationToken cancellationToken)
    {
        string sql;
        if (request.IsProject)
        {
            var transpileResult = await new SharpSqlProjectCompiler().TranspileAsync(
                request.InputPath,
                new ProjectTranspileOptions
                {
                    EntryPoint = request.EntryPoint,
                    Configuration = request.Configuration,
                    TargetFramework = request.TargetFramework
                },
                cancellationToken);
            if (!transpileResult.Success)
            {
                return new SqlRunResult(
                    false,
                    string.Empty,
                    [],
                    transpileResult.Diagnostics,
                    ContainerKept: false);
            }
            sql = transpileResult.Sql;
        }
        else
        {
            sql = request.Sql!;
        }

        var connectionString = request.ForceContainer
            ? null
            : SqlServerConnectionResolver.Resolve(
                request.InputPath,
                request.ConnectionName,
                request.ConnectionStringEnvironmentVariable);
        await using var session = await SqlServerSessionFactory.OpenAsync(
            new SqlServerSessionOptions(
                request.InputPath,
                connectionString,
                request.SqlServerImage,
                request.DatabaseName,
                request.KeepContainer),
            cancellationToken);
        var execution = await SqlBatchExecutor.ExecuteAsync(
            session.Connection,
            sql,
            request.CommandTimeoutSeconds,
            cancellationToken);
        return new SqlRunResult(
            execution.Success,
            session.Description,
            execution.Messages,
            [],
            session.KeepContainer,
            execution.ErrorNumber,
            execution.ErrorMessage);
    }
}
