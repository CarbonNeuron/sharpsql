using System.Globalization;
using System.Xml.Linq;
using Microsoft.Data.SqlClient;

namespace SharpSql.SqlServer;

/// <summary>Summarizes SQL Server query-plan and SharpSql heap diagnostics for a batch.</summary>
/// <param name="PlanStatementCount">The number of statements found in collected query plans.</param>
/// <param name="PlanOperatorCount">The number of relational operators found in collected query plans.</param>
/// <param name="MaximumPlanDepth">The greatest operator nesting depth.</param>
/// <param name="EstimatedSubtreeCost">The combined estimated subtree cost.</param>
/// <param name="CompileTimeMilliseconds">The combined SQL compilation time.</param>
/// <param name="CompileMemoryKilobytes">The combined SQL compilation memory.</param>
/// <param name="HeapObjectsAllocated">The number of SharpSql heap objects allocated.</param>
/// <param name="IndexedItemsAllocated">The number of SharpSql indexed collection items allocated.</param>
/// <param name="DictionaryEntriesAllocated">The number of SharpSql dictionary entries allocated.</param>
/// <param name="HeapDiagnosticsObserved">Whether the batch emitted heap diagnostic data.</param>
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

/// <summary>Controls SQL batch execution and diagnostic collection.</summary>
/// <param name="CollectDebugInfo">Whether to collect SQL query plans and SharpSql heap counters.</param>
/// <param name="MessageReceived">An optional observer invoked for SQL informational messages.</param>
/// <param name="ConsumeHeapDiagnostics">Whether heap diagnostic messages are consumed instead of returned.</param>
public sealed record SqlBatchExecutionOptions(
    bool CollectDebugInfo = false,
    Action<string>? MessageReceived = null,
    bool ConsumeHeapDiagnostics = false)
{
    /// <summary>Gets an optional predicate used to prefer one error when SQL Server reports several.</summary>
    public Func<int, bool>? PreferredErrorNumber { get; init; }
}

/// <summary>Contains the outcome of executing a SQL batch.</summary>
/// <param name="Success">Whether execution completed without a SQL error.</param>
/// <param name="Messages">The informational messages emitted by SQL Server.</param>
/// <param name="ErrorNumber">The SQL Server error number, when execution failed.</param>
/// <param name="ErrorMessage">The SQL Server error message, when execution failed.</param>
/// <param name="RowsAffected">The number of affected rows reported by the command.</param>
/// <param name="DebugInfo">Collected diagnostics, when requested.</param>
public sealed record SqlBatchExecutionResult(
    bool Success,
    IReadOnlyList<string> Messages,
    int? ErrorNumber = null,
    string? ErrorMessage = null,
    int RowsAffected = -1,
    SqlBatchDebugInfo? DebugInfo = null)
{
    /// <summary>Gets informational messages as normalized standard output.</summary>
    public string StandardOutput => SqlBatchOutput.FromMessages(Messages);
}

/// <summary>Normalizes captured program output for C# and SQL execution parity.</summary>
public static class SqlBatchOutput
{
    /// <summary>Normalizes line endings and removes trailing line terminators.</summary>
    public static string Normalize(string output) =>
        output.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\r', '\n');

    /// <summary>Formats SQL informational messages as normalized standard output.</summary>
    public static string FromMessages(IEnumerable<string> messages) =>
        Normalize(string.Join("\n", messages));
}

/// <summary>Executes generated SharpSql batches over an open SQL Server connection.</summary>
public static class SqlBatchExecutor
{
    private const string HeapDebugPrefix = "__SHARPSQL_DEBUG_HEAP__|";

    /// <summary>Executes a SQL batch.</summary>
    /// <param name="connection">An open SQL Server connection.</param>
    /// <param name="sql">The batch text.</param>
    /// <param name="commandTimeoutSeconds">The command timeout in seconds.</param>
    /// <param name="cancellationToken">A token that can cancel execution.</param>
    /// <returns>The execution outcome.</returns>
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

    /// <summary>Executes a SQL batch with diagnostic and message options.</summary>
    /// <param name="connection">An open SQL Server connection.</param>
    /// <param name="sql">The batch text.</param>
    /// <param name="commandTimeoutSeconds">The command timeout in seconds.</param>
    /// <param name="options">Optional execution settings.</param>
    /// <param name="cancellationToken">A token that can cancel execution.</param>
    /// <returns>The execution outcome.</returns>
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
            var error = SelectError(exception.Errors, options?.PreferredErrorNumber);
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

    private static SqlError SelectError(SqlErrorCollection errors, Func<int, bool>? preferredErrorNumber) =>
        preferredErrorNumber is null
            ? errors.Cast<SqlError>().First()
            : errors.Cast<SqlError>().FirstOrDefault(error => preferredErrorNumber(error.Number))
              ?? errors.Cast<SqlError>().First();

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
