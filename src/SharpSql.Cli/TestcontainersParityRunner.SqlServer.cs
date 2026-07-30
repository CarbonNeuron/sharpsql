using System.Diagnostics;
using Microsoft.Data.SqlClient;
using SharpSql.SqlServer;

namespace SharpSql.Cli;

/// <inheritdoc />
public sealed partial class TestcontainersParityRunner
{
    private static async Task<SqlExecutionResult> ExecuteSqlProfileAsync(
        SqlConnection connection,
        string sql,
        int commandTimeoutSeconds,
        List<TimeSpan> samples,
        CancellationToken cancellationToken)
    {
        SqlExecutionResult execution = new(new ParityOutcome(string.Empty, null), null);
        for (var index = 0; index < ProfileWarmupRuns; index++)
        {
            execution = await ExecuteSqlAsync(
                connection,
                sql,
                commandTimeoutSeconds,
                collectDebug: false,
                cancellationToken);
            if (execution.Outcome.Failure is not null)
                return execution;
        }

        for (var index = 0; index < ProfileSampleRuns; index++)
        {
            var timer = Stopwatch.StartNew();
            var sample = await ExecuteSqlAsync(
                connection,
                sql,
                commandTimeoutSeconds,
                collectDebug: false,
                cancellationToken);
            timer.Stop();
            samples.Add(timer.Elapsed);
            if (index == 0)
                execution = sample;
            if (sample.Outcome.Failure is not null)
                return sample;
        }
        return execution;
    }

    private static async Task<SqlExecutionResult> ExecuteSqlAsync(
        SqlConnection connection,
        string sql,
        int commandTimeoutSeconds,
        bool collectDebug,
        CancellationToken cancellationToken)
    {
        var result = await SqlBatchExecutor.ExecuteAsync(
            connection,
            sql,
            commandTimeoutSeconds,
            new SqlBatchExecutionOptions(
                CollectDebugInfo: collectDebug,
                ConsumeHeapDiagnostics: true)
            {
                PreferredErrorNumber = RuntimeErrorCatalog.IsSharpSqlRuntimeError
            },
            cancellationToken);
        var failure = result.Success
            ? null
            : NormalizeSqlFailure(result.ErrorNumber!.Value, result.ErrorMessage!);
        return new SqlExecutionResult(
            new ParityOutcome(result.StandardOutput, failure),
            ToParityDebugInfo(result.DebugInfo));
    }

    private static ParityFailure NormalizeSqlFailure(int number, string message)
    {
        var failure = RuntimeErrorCatalog.NormalizeSqlFailure(number, message);
        return new ParityFailure(ParityFailureCategory.Runtime, failure.Type, failure.Message, failure.Code);
    }

    private static ParityDebugInfo? ToParityDebugInfo(SqlBatchDebugInfo? debugInfo) => debugInfo is null
        ? null
        : new ParityDebugInfo(
            debugInfo.PlanStatementCount,
            debugInfo.PlanOperatorCount,
            debugInfo.MaximumPlanDepth,
            debugInfo.EstimatedSubtreeCost,
            debugInfo.CompileTimeMilliseconds,
            debugInfo.CompileMemoryKilobytes,
            debugInfo.HeapObjectsAllocated,
            debugInfo.IndexedItemsAllocated,
            debugInfo.DictionaryEntriesAllocated);

    private sealed record SqlExecutionResult(
        ParityOutcome Outcome,
        ParityDebugInfo? DebugInfo);
}
