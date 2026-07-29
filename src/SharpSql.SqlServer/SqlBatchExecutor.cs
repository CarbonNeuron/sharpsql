using System.Globalization;
using System.Xml.Linq;
using Microsoft.Data.SqlClient;

namespace SharpSql.SqlServer;

public sealed record SqlBatchDebugInfo(
    int PlanStatementCount,
    int PlanOperatorCount,
    int MaximumPlanDepth,
    double EstimatedSubtreeCost,
    long CompileTimeMilliseconds,
    long CompileMemoryKilobytes,
    long HeapObjectsAllocated,
    long IndexedItemsAllocated,
    long DictionaryEntriesAllocated,
    bool HeapDiagnosticsObserved = false);

public sealed record SqlBatchExecutionOptions(
    bool CollectDebugInfo = false,
    Action<string>? MessageReceived = null,
    bool ConsumeHeapDiagnostics = false);

public sealed record SqlBatchExecutionResult(
    bool Success,
    IReadOnlyList<string> Messages,
    int? ErrorNumber = null,
    string? ErrorMessage = null,
    int RowsAffected = -1,
    SqlBatchDebugInfo? DebugInfo = null);

public static class SqlBatchExecutor
{
    private const string HeapDebugPrefix = "__SHARPSQL_DEBUG_HEAP__|";

    public static async Task<SqlBatchExecutionResult> ExecuteAsync(
        SqlConnection connection,
        string sql,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken = default)
        => await ExecuteAsync(
            connection,
            sql,
            commandTimeoutSeconds,
            options: null,
            cancellationToken);

    public static async Task<SqlBatchExecutionResult> ExecuteAsync(
        SqlConnection connection,
        string sql,
        int commandTimeoutSeconds,
        SqlBatchExecutionOptions? options,
        CancellationToken cancellationToken = default)
    {
        var messages = new List<string>();
        SqlError? reportedError = null;
        var messageGate = new object();
        var plans = new PlanAccumulator();
        long heapObjects = 0;
        long indexedItems = 0;
        long dictionaryEntries = 0;
        var heapDiagnosticsObserved = false;
        void HandleInfoMessage(object sender, SqlInfoMessageEventArgs args)
        {
            foreach (SqlError error in args.Errors)
            {
                if (error.Class == 0)
                {
                    bool isHeapDiagnostic;
                    lock (messageGate)
                    {
                        isHeapDiagnostic =
                            (options?.CollectDebugInfo == true || options?.ConsumeHeapDiagnostics == true) &&
                            TryParseHeapDiagnostics(
                                error.Message,
                                ref heapObjects,
                                ref indexedItems,
                                ref dictionaryEntries);
                        heapDiagnosticsObserved |= isHeapDiagnostic;
                        if (!isHeapDiagnostic)
                            messages.Add(error.Message);
                        else
                            continue;
                        try
                        {
                            options?.MessageReceived?.Invoke(error.Message);
                        }
                        catch
                        {
                            // Output observers must not replace the SQL execution result.
                        }
                    }
                }
                else
                {
                    lock (messageGate)
                        reportedError ??= error;
                }
            }
        }

        connection.InfoMessage += HandleInfoMessage;
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = options?.CollectDebugInfo == true
                ? $"SET STATISTICS XML ON;{Environment.NewLine}{sql}{Environment.NewLine}SET STATISTICS XML OFF;"
                : sql;
            command.CommandTimeout = commandTimeoutSeconds;
            int rowsAffected;
            if (options?.CollectDebugInfo == true)
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
                rowsAffected = reader.RecordsAffected;
            }
            else
            {
                rowsAffected = await command.ExecuteNonQueryAsync(cancellationToken);
            }
            string[] messageSnapshot;
            SqlError? errorSnapshot;
            long heapObjectSnapshot;
            long indexedItemSnapshot;
            long dictionaryEntrySnapshot;
            bool heapDiagnosticsSnapshot;
            lock (messageGate)
            {
                messageSnapshot = messages.ToArray();
                errorSnapshot = reportedError;
                heapObjectSnapshot = heapObjects;
                indexedItemSnapshot = indexedItems;
                dictionaryEntrySnapshot = dictionaryEntries;
                heapDiagnosticsSnapshot = heapDiagnosticsObserved;
            }
            var debugInfo = options?.CollectDebugInfo == true
                ? plans.ToDebugInfo(
                    heapObjectSnapshot,
                    indexedItemSnapshot,
                    dictionaryEntrySnapshot,
                    heapDiagnosticsSnapshot)
                : null;
            if (errorSnapshot is not null)
            {
                return new SqlBatchExecutionResult(
                    false,
                    messageSnapshot,
                    errorSnapshot.Number,
                    errorSnapshot.Message,
                    rowsAffected,
                    debugInfo);
            }
            return new SqlBatchExecutionResult(
                true,
                messageSnapshot,
                RowsAffected: rowsAffected,
                DebugInfo: debugInfo);
        }
        catch (SqlException exception)
        {
            var error = exception.Errors.Cast<SqlError>().First();
            string[] messageSnapshot;
            long heapObjectSnapshot;
            long indexedItemSnapshot;
            long dictionaryEntrySnapshot;
            bool heapDiagnosticsSnapshot;
            lock (messageGate)
            {
                messageSnapshot = messages.ToArray();
                heapObjectSnapshot = heapObjects;
                indexedItemSnapshot = indexedItems;
                dictionaryEntrySnapshot = dictionaryEntries;
                heapDiagnosticsSnapshot = heapDiagnosticsObserved;
            }
            var debugInfo = options?.CollectDebugInfo == true
                ? plans.ToDebugInfo(
                    heapObjectSnapshot,
                    indexedItemSnapshot,
                    dictionaryEntrySnapshot,
                    heapDiagnosticsSnapshot)
                : null;
            return new SqlBatchExecutionResult(
                false,
                messageSnapshot,
                error.Number,
                error.Message,
                DebugInfo: debugInfo);
        }
        finally
        {
            connection.InfoMessage -= HandleInfoMessage;
        }
    }

    private static bool TryParseHeapDiagnostics(
        string message,
        ref long heapObjects,
        ref long indexedItems,
        ref long dictionaryEntries)
    {
        if (!message.StartsWith(HeapDebugPrefix, StringComparison.Ordinal))
            return false;

        foreach (var item in message[HeapDebugPrefix.Length..].Split('|'))
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

    private sealed class PlanAccumulator
    {
        private readonly HashSet<string> _seenStatements = new(StringComparer.Ordinal);
        private int _statementCount;
        private int _operatorCount;
        private int _maximumDepth;
        private double _estimatedCost;
        private long _compileTimeMilliseconds;
        private long _compileMemoryKilobytes;

        public void Add(string xml)
        {
            XDocument document;
            try
            {
                document = XDocument.Parse(xml);
            }
            catch
            {
                return;
            }

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
                foreach (var queryPlan in statement.Descendants()
                             .Where(element => element.Name.LocalName == "QueryPlan"))
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

        public SqlBatchDebugInfo ToDebugInfo(
            long heapObjects,
            long indexedItems,
            long dictionaryEntries,
            bool heapDiagnosticsObserved) => new(
            _statementCount,
            _operatorCount,
            _maximumDepth,
            _estimatedCost,
            _compileTimeMilliseconds,
            _compileMemoryKilobytes,
            heapObjects,
            indexedItems,
            dictionaryEntries,
            heapDiagnosticsObserved);
    }
}
