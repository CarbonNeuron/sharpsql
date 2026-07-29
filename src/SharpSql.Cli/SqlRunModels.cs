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
    int CommandTimeoutSeconds,
    RuntimeStorageKind RuntimeStorage = RuntimeStorageKind.Ephemeral)
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
                    TargetFramework = request.TargetFramework,
                    CompilerOptions = new TranspileOptions { RuntimeStorage = request.RuntimeStorage }
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
        var messages = new List<string>();
        foreach (var batch in CreateExecutionBatches(request.RuntimeStorage, sql))
        {
            var execution = await SqlBatchExecutor.ExecuteAsync(
                session.Connection,
                batch,
                request.CommandTimeoutSeconds,
                cancellationToken);
            messages.AddRange(execution.Messages);
            if (!execution.Success)
            {
                return new SqlRunResult(
                    false,
                    session.Description,
                    messages,
                    [],
                    session.KeepContainer,
                    execution.ErrorNumber,
                    execution.ErrorMessage);
            }
        }

        return new SqlRunResult(true, session.Description, messages, [], session.KeepContainer);
    }

    internal static IReadOnlyList<string> CreateExecutionBatches(RuntimeStorageKind runtimeStorage, string sql) =>
        runtimeStorage == RuntimeStorageKind.ServiceBroker
            ? [SharpSqlServiceBrokerRuntime.GenerateProvisioningSql(), sql]
            : [sql];
}
