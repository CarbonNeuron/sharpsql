using System.Diagnostics;
using Microsoft.Data.SqlClient;
using SharpSql.SqlServer;

namespace SharpSql.Cli;

/// <summary>Describes a request to transpile and execute SQL.</summary>
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
    RuntimeStorageKind RuntimeStorage = RuntimeStorageKind.Ephemeral,
    bool Debug = false,
    bool Profile = false,
    string? OutputPath = null,
    string? InstallerOutputPath = null,
    bool EnableNativeKernels = false)
{
    public bool IsProject => Sql is null;
}

/// <summary>Contains timing samples collected while executing SQL.</summary>
public sealed record SqlRunProfile(
    int WarmupRuns,
    IReadOnlyList<TimeSpan> SqlServerSamples);

/// <summary>Reports the outcome of transpiling and executing SQL.</summary>
public sealed record SqlRunResult(
    bool Success,
    string SqlServer,
    IReadOnlyList<string> Messages,
    IReadOnlyList<CompilerDiagnostic> Diagnostics,
    bool ContainerKept,
    int? ErrorNumber = null,
    string? ErrorMessage = null,
    string? GeneratedSql = null,
    string? InstallerSql = null,
    SqlBatchDebugInfo? DebugInfo = null,
    SqlRunProfile? Profile = null);

/// <summary>Transpiles and executes SharpSql programs in SQL Server.</summary>
public interface ISqlRunService
{
    /// <summary>Runs the request and returns its execution result.</summary>
    Task<SqlRunResult> RunAsync(SqlRunRequest request, CancellationToken cancellationToken);

    /// <summary>Runs the request, reporting SQL messages as they are observed.</summary>
    async Task<SqlRunResult> RunAsync(
        SqlRunRequest request,
        Action<string>? reportMessage,
        CancellationToken cancellationToken)
    {
        var result = await RunAsync(request, cancellationToken);
        if (reportMessage is not null)
        {
            foreach (var message in result.Messages)
            {
                try
                {
                    reportMessage(message);
                }
                catch
                {
                    // Output observers must not replace the SQL execution result.
                }
            }
        }
        return result;
    }
}

/// <summary>Transpiles and executes SharpSql programs in SQL Server.</summary>
public sealed class SqlRunService : ISqlRunService
{
    private const int ProfileWarmupRuns = 1;
    private const int ProfileSampleRuns = 3;

    /// <inheritdoc />
    public async Task<SqlRunResult> RunAsync(SqlRunRequest request, CancellationToken cancellationToken)
        => await RunAsync(request, reportMessage: null, cancellationToken);

    /// <inheritdoc />
    public async Task<SqlRunResult> RunAsync(
        SqlRunRequest request,
        Action<string>? reportMessage,
        CancellationToken cancellationToken)
    {
        string sql;
        IReadOnlyList<CompilerDiagnostic> diagnostics;
        var transpileSucceeded = true;
        if (request.IsProject)
        {
            var transpileResult = await new SharpSqlProjectCompiler().TranspileAsync(
                request.InputPath,
                new ProjectTranspileOptions
                {
                    EntryPoint = request.EntryPoint,
                    Configuration = request.Configuration,
                    TargetFramework = request.TargetFramework,
                    CompilerOptions = new TranspileOptions
                    {
                        RuntimeStorage = request.RuntimeStorage,
                        EmitRuntimeDiagnostics = request.Debug,
                        EnableNativeKernels = request.EnableNativeKernels
                    }
                },
                cancellationToken);
            sql = transpileResult.Sql;
            diagnostics = transpileResult.Diagnostics;
            transpileSucceeded = transpileResult.Success;
        }
        else
        {
            sql = request.Sql!;
            diagnostics = [];
        }

        var installerSql = InstallerSql(request.RuntimeStorage);
        var artifactPaths = SqlOutputArtifacts.ResolvePaths(
            request.OutputPath,
            request.InstallerOutputPath,
            request.RuntimeStorage);
        await SqlOutputArtifacts.WriteAsync(
            artifactPaths,
            sql,
            installerSql,
            cancellationToken);
        if (!transpileSucceeded)
        {
            return new SqlRunResult(
                false,
                string.Empty,
                [],
                diagnostics,
                ContainerKept: false,
                GeneratedSql: sql,
                InstallerSql: installerSql);
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
        var installationMessages = Array.Empty<string>();
        if (installerSql is not null)
        {
            var installation = await SqlBatchExecutor.ExecuteAsync(
                session.Connection,
                installerSql,
                request.CommandTimeoutSeconds,
                cancellationToken);
            installationMessages = installation.Messages.ToArray();
            ReportMessages(installationMessages, reportMessage);
            if (!installation.Success)
            {
                return new SqlRunResult(
                    false,
                    session.Description,
                    installationMessages,
                    diagnostics,
                    session.KeepContainer,
                    installation.ErrorNumber,
                    installation.ErrorMessage,
                    sql,
                    installerSql,
                    installation.DebugInfo);
            }
        }

        var (execution, profile) = request.Profile
            ? await ExecuteProfileAsync(
                session.Connection,
                sql,
                request.CommandTimeoutSeconds,
                request.Debug,
                reportMessage,
                cancellationToken)
            : (await ExecuteProgramAsync(
                session.Connection,
                sql,
                request.CommandTimeoutSeconds,
                request.Debug,
                request.Debug,
                reportMessage,
                cancellationToken), null);
        return new SqlRunResult(
            execution.Success,
            session.Description,
            installationMessages.Concat(execution.Messages).ToArray(),
            diagnostics,
            session.KeepContainer,
            execution.ErrorNumber,
            execution.ErrorMessage,
            sql,
            installerSql,
            execution.DebugInfo,
            profile);
    }

    private static async Task<(SqlBatchExecutionResult Execution, SqlRunProfile Profile)> ExecuteProfileAsync(
        SqlConnection connection,
        string sql,
        int commandTimeoutSeconds,
        bool collectDebugInfo,
        Action<string>? reportMessage,
        CancellationToken cancellationToken)
    {
        for (var index = 0; index < ProfileWarmupRuns; index++)
        {
            var warmup = await ExecuteProgramAsync(
                connection,
                sql,
                commandTimeoutSeconds,
                collectDebugInfo: false,
                consumeHeapDiagnostics: collectDebugInfo,
                reportMessage,
                cancellationToken);
            if (!warmup.Success)
                return (warmup, new SqlRunProfile(ProfileWarmupRuns, []));
        }

        var samples = new List<TimeSpan>(ProfileSampleRuns);
        SqlBatchExecutionResult? canonical = null;
        for (var index = 0; index < ProfileSampleRuns; index++)
        {
            var timer = Stopwatch.StartNew();
            var sample = await ExecuteProgramAsync(
                connection,
                sql,
                commandTimeoutSeconds,
                collectDebugInfo: false,
                consumeHeapDiagnostics: collectDebugInfo,
                reportMessage: null,
                cancellationToken);
            timer.Stop();
            samples.Add(timer.Elapsed);
            canonical ??= sample;
            if (!sample.Success)
                return (sample, new SqlRunProfile(ProfileWarmupRuns, samples));
        }

        if (collectDebugInfo)
        {
            var debug = await ExecuteProgramAsync(
                connection,
                sql,
                commandTimeoutSeconds,
                collectDebugInfo: true,
                consumeHeapDiagnostics: true,
                reportMessage: null,
                cancellationToken);
            if (!debug.Success)
                return (debug, new SqlRunProfile(ProfileWarmupRuns, samples));
            canonical = canonical! with { DebugInfo = debug.DebugInfo };
        }

        return (canonical!, new SqlRunProfile(ProfileWarmupRuns, samples));
    }

    private static Task<SqlBatchExecutionResult> ExecuteProgramAsync(
        SqlConnection connection,
        string sql,
        int commandTimeoutSeconds,
        bool collectDebugInfo,
        bool consumeHeapDiagnostics,
        Action<string>? reportMessage,
        CancellationToken cancellationToken) =>
        SqlBatchExecutor.ExecuteAsync(
            connection,
            sql,
            commandTimeoutSeconds,
            new SqlBatchExecutionOptions(collectDebugInfo, reportMessage, consumeHeapDiagnostics),
            cancellationToken);

    private static void ReportMessages(
        IReadOnlyList<string> messages,
        Action<string>? reportMessage)
    {
        if (reportMessage is null)
            return;
        foreach (var message in messages)
        {
            try
            {
                reportMessage(message);
            }
            catch
            {
                // Output observers must not replace the SQL execution result.
            }
        }
    }

    private static string? InstallerSql(RuntimeStorageKind runtimeStorage) => runtimeStorage switch
    {
        RuntimeStorageKind.MemoryOptimized => SharpSqlMemoryOptimizedRuntime.GenerateProvisioningSql(),
        RuntimeStorageKind.ServiceBroker => SharpSqlServiceBrokerRuntime.GenerateProvisioningSql(),
        _ => null
    };
}
