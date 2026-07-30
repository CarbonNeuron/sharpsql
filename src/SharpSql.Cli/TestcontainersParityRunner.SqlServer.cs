using System.Diagnostics;
using System.Globalization;
using System.Xml.Linq;
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
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        var reportedErrors = new List<SqlErrorInfo>();
        var plans = new PlanAccumulator();
        long heapObjects = 0;
        long indexedItems = 0;
        long dictionaryEntries = 0;

        void HandleInfoMessage(object sender, SqlInfoMessageEventArgs args)
        {
            foreach (SqlError error in args.Errors)
            {
                if (error.Class == 0)
                {
                    if (!TryParseHeapDiagnostics(
                            error.Message,
                            ref heapObjects,
                            ref indexedItems,
                            ref dictionaryEntries))
                        output.WriteLine(error.Message);
                }
                else
                    reportedErrors.Add(new SqlErrorInfo(error.Number, error.Message));
            }
        }

        connection.InfoMessage += HandleInfoMessage;
        ParityFailure? failure = null;
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = collectDebug
                ? $"SET STATISTICS XML ON;{Environment.NewLine}{sql}{Environment.NewLine}SET STATISTICS XML OFF;"
                : sql;
            command.CommandTimeout = commandTimeoutSeconds;
            if (collectDebug)
            {
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                do
                {
                    while (await reader.ReadAsync(cancellationToken))
                    {
                        for (var field = 0; field < reader.FieldCount; field++)
                        {
                            if (await reader.IsDBNullAsync(field, cancellationToken))
                                continue;
                            var value = Convert.ToString(reader.GetValue(field), CultureInfo.InvariantCulture);
                            if (value?.Contains("<ShowPlanXML", StringComparison.Ordinal) == true)
                                plans.Add(value);
                        }
                    }
                } while (await reader.NextResultAsync(cancellationToken));
            }
            else
            {
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        catch (SqlException exception)
        {
            var error = exception.Errors.Cast<SqlError>()
                .FirstOrDefault(item => RuntimeErrorCatalog.IsSharpSqlRuntimeError(item.Number))
                ?? exception.Errors.Cast<SqlError>().First();
            failure = NormalizeSqlFailure(new SqlErrorInfo(error.Number, error.Message));
        }
        finally
        {
            connection.InfoMessage -= HandleInfoMessage;
        }

        failure ??= reportedErrors.Count > 0 ? NormalizeSqlFailure(reportedErrors[0]) : null;
        var debugInfo = collectDebug
            ? plans.ToDebugInfo(heapObjects, indexedItems, dictionaryEntries)
            : null;
        return new SqlExecutionResult(
            new ParityOutcome(NormalizeOutput(output.ToString()), failure),
            debugInfo);
    }

    private static bool TryParseHeapDiagnostics(
        string message,
        ref long heapObjects,
        ref long indexedItems,
        ref long dictionaryEntries)
    {
        var marker = message.IndexOf(HeapDebugPrefix, StringComparison.Ordinal);
        if (marker < 0)
            return false;

        foreach (var item in message[(marker + HeapDebugPrefix.Length)..].Split('|'))
        {
            var parts = item.Split('=', 2);
            if (parts.Length != 2 ||
                !long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                continue;
            switch (parts[0])
            {
                case "objects":
                    heapObjects = value;
                    break;
                case "indexed_items":
                    indexedItems = value;
                    break;
                case "dictionary_entries":
                    dictionaryEntries = value;
                    break;
            }
        }
        return true;
    }

    private static ParityFailure NormalizeSqlFailure(SqlErrorInfo failure)
    {
        var type = RuntimeErrorCatalog.ExceptionTypeName(failure.Number) ?? nameof(SqlException);
        return new ParityFailure(ParityFailureCategory.Runtime, type, failure.Message, failure.Number);
    }

    private static string NormalizeOutput(string output) =>
        output.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\r', '\n');

    private sealed class PlanAccumulator
    {
        private readonly HashSet<string> _seenStatements = new(StringComparer.Ordinal);
        private int _statementCount;
        private int _operatorCount;
        private int _maximumDepth;
        private double _estimatedCost;
        private long _compileTimeMilliseconds;
        private long _compileMemoryKilobytes;

        /// <summary>Adds a SQL Server showplan document to the accumulated diagnostics.</summary>
        public void Add(string xml)
        {
            var document = XDocument.Parse(xml);
            foreach (var statement in document.Descendants().Where(element =>
                         element.Name.LocalName.StartsWith("Stmt", StringComparison.Ordinal) &&
                         element.Attribute("StatementId") is not null))
            {
                var operators = statement.Descendants()
                    .Where(element => element.Name.LocalName == "RelOp")
                    .ToArray();
                var signature = (statement.Attribute("StatementText")?.Value ?? statement.Name.LocalName) + "|" +
                    string.Join(",", operators.Select(item =>
                        $"{item.Attribute("LogicalOp")?.Value}/{item.Attribute("PhysicalOp")?.Value}"));
                if (!_seenStatements.Add(signature))
                    continue;

                _statementCount++;
                _operatorCount += operators.Length;
                _maximumDepth = Math.Max(
                    _maximumDepth,
                    operators.Select(element =>
                            element.Ancestors()
                                .TakeWhile(ancestor => !ReferenceEquals(ancestor, statement))
                                .Count(ancestor => ancestor.Name.LocalName == "RelOp") + 1)
                        .DefaultIfEmpty(0)
                        .Max());
                if (double.TryParse(
                        statement.Attribute("StatementSubTreeCost")?.Value,
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out var cost))
                    _estimatedCost += cost;
                foreach (var queryPlan in statement.Descendants().Where(element => element.Name.LocalName == "QueryPlan"))
                {
                    if (long.TryParse(
                            queryPlan.Attribute("CompileTime")?.Value,
                            NumberStyles.Integer,
                            CultureInfo.InvariantCulture,
                            out var compileTime))
                        _compileTimeMilliseconds += compileTime;
                    if (long.TryParse(
                            queryPlan.Attribute("CompileMemory")?.Value,
                            NumberStyles.Integer,
                            CultureInfo.InvariantCulture,
                            out var compileMemory))
                        _compileMemoryKilobytes += compileMemory;
                }
            }
        }

        /// <summary>Creates debug information from the accumulated plans and heap counters.</summary>
        public ParityDebugInfo ToDebugInfo(
            long heapObjects,
            long indexedItems,
            long dictionaryEntries) => new(
            _statementCount,
            _operatorCount,
            _maximumDepth,
            _estimatedCost,
            _compileTimeMilliseconds,
            _compileMemoryKilobytes,
            heapObjects,
            indexedItems,
            dictionaryEntries);
    }

    private sealed record SqlExecutionResult(
        ParityOutcome Outcome,
        ParityDebugInfo? DebugInfo);

    private sealed record SqlErrorInfo(int Number, string Message);
}
