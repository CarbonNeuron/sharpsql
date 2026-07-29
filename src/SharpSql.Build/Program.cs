using System.Diagnostics;
using System.Text;
using SharpSql.SqlServer;

namespace SharpSql.Build;

public static class Program
{
    public static Task<int> Main(string[] args) => RunAsync(args);

    public static async Task<int> RunAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken = default)
    {
        if (!BuildArguments.TryParse(args, out var parsed, out var error))
        {
            Console.Error.WriteLine($"error SSB0001: {error}");
            return 2;
        }

        if (!string.Equals(parsed.Operation, "run", StringComparison.OrdinalIgnoreCase))
            return await GenerateSqlAsync(parsed, cancellationToken);
        try
        {
            return await RunSqlAsync(parsed, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Console.Error.WriteLine($"error SSB0003: {exception.Message}");
            return 2;
        }
    }

    private static async Task<int> GenerateSqlAsync(BuildArguments parsed, CancellationToken cancellationToken)
    {
        var elapsed = Stopwatch.StartNew();
        WriteProgress($"parsing and transpiling {Path.GetFileName(parsed.ProjectPath)}...");
        var result = await new SharpSqlProjectCompiler().TranspileAsync(
            parsed.ProjectPath,
            new ProjectTranspileOptions
            {
                EntryPoint = parsed.EntryPoint,
                Configuration = parsed.Configuration,
                TargetFramework = parsed.TargetFramework,
                CompilerOptions = new TranspileOptions
                {
                    RuntimeStorage = parsed.RuntimeStorage
                }
            },
            cancellationToken);
        if (!result.Success)
        {
            foreach (var diagnostic in result.Diagnostics)
                WriteDiagnostic(diagnostic);
            return 1;
        }

        var outputPath = NormalizePath(parsed.OutputPath!);
        var outputDirectory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(outputDirectory))
            Directory.CreateDirectory(outputDirectory);
        await File.WriteAllTextAsync(
            outputPath,
            result.Sql,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            cancellationToken);
        elapsed.Stop();
        WriteProgress(
            $"generated {CountLines(result.Sql)} SQL lines at {outputPath} ({FormatDuration(elapsed.Elapsed)}).");
        return 0;
    }

    private static async Task<int> RunSqlAsync(BuildArguments parsed, CancellationToken cancellationToken)
    {
        var totalTime = Stopwatch.StartNew();
        WriteProgress("resolving SQL Server configuration...");
        var connectionString = parsed.ForceContainer
            ? null
            : SqlServerConnectionResolver.Resolve(
                parsed.ProjectPath,
                parsed.ConnectionName,
                parsed.ConnectionStringEnvironmentVariable);
        if (connectionString is null)
            WriteProgress($"starting or reusing SQL Server container {parsed.SqlServerImage}...");
        else
            WriteProgress("connecting to configured SQL Server...");

        var connectionTime = Stopwatch.StartNew();
        var session = await SqlServerSessionFactory.OpenAsync(
            new SqlServerSessionOptions(
                parsed.ProjectPath,
                connectionString,
                parsed.SqlServerImage,
                parsed.DatabaseName,
                parsed.KeepContainer),
            cancellationToken);
        connectionTime.Stop();
        WriteProgress($"SQL Server ready at {session.Description} ({FormatDuration(connectionTime.Elapsed)}).");

        var result = new SqlBatchExecutionResult(true, []);
        var sql = await File.ReadAllTextAsync(NormalizePath(parsed.SqlPath!), cancellationToken);
        var executionTime = Stopwatch.StartNew();
        try
        {
            if (parsed.RuntimeStorage == RuntimeStorageKind.ServiceBroker)
            {
                WriteProgress("provisioning Service Broker runtime...");
                result = await SqlBatchExecutor.ExecuteAsync(
                    session.Connection,
                    SharpSqlServiceBrokerRuntime.GenerateProvisioningSql(),
                    parsed.CommandTimeoutSeconds,
                    new SqlBatchExecutionOptions(MessageReceived: Console.WriteLine),
                    cancellationToken);
                if (result.Success)
                    WriteProgress("Service Broker runtime ready.");
            }

            if (result.Success)
            {
                WriteProgress($"executing SQL batch ({CountLines(sql)} lines)...");
                result = await SqlBatchExecutor.ExecuteAsync(
                    session.Connection,
                    sql,
                    parsed.CommandTimeoutSeconds,
                    new SqlBatchExecutionOptions(MessageReceived: Console.WriteLine),
                    cancellationToken);
            }
        }
        finally
        {
            executionTime.Stop();
            if (session.IsContainer && !session.KeepContainer)
                WriteProgress("stopping temporary SQL Server container...");
            await session.DisposeAsync();
            if (session.IsContainer && !session.KeepContainer)
                WriteProgress("temporary SQL Server container removed.");
        }
        if (!result.Success)
        {
            Console.Error.WriteLine($"error SSB0002: SQL Server error {result.ErrorNumber}: {result.ErrorMessage}");
            return 1;
        }
        totalTime.Stop();
        WriteProgress(
            $"SQL execution completed on {session.Description} " +
            $"({FormatDuration(executionTime.Elapsed)} execution, {FormatDuration(totalTime.Elapsed)} total).");
        if (session.KeepContainer)
            WriteProgress("SQL Server container kept running for reuse.");
        return 0;
    }

    private static void WriteProgress(string message) => Console.WriteLine($"SharpSql: {message}");

    private static int CountLines(string value) =>
        value.Length == 0 ? 0 : value.Count(character => character == '\n') + (value[^1] == '\n' ? 0 : 1);

    private static string FormatDuration(TimeSpan elapsed) => elapsed.TotalSeconds >= 1
        ? $"{elapsed.TotalSeconds:0.0}s"
        : $"{elapsed.TotalMilliseconds:0}ms";

    private static void WriteDiagnostic(CompilerDiagnostic diagnostic)
    {
        var location = string.IsNullOrWhiteSpace(diagnostic.FilePath)
            ? string.Empty
            : $"{diagnostic.FilePath}({diagnostic.Line},{diagnostic.Column}): ";
        Console.Error.WriteLine($"{location}error {diagnostic.Code}: {diagnostic.Message}");
    }

    private static string NormalizePath(string path)
    {
        var platformPath = Path.DirectorySeparatorChar == '/'
            ? path.Replace('\\', '/')
            : path.Replace('/', '\\');
        return Path.GetFullPath(platformPath);
    }

    private sealed record BuildArguments(
        string Operation,
        string ProjectPath,
        string? OutputPath,
        string? SqlPath,
        string Configuration,
        string? TargetFramework,
        string? EntryPoint,
        RuntimeStorageKind RuntimeStorage,
        string? ConnectionName,
        string? ConnectionStringEnvironmentVariable,
        bool ForceContainer,
        bool KeepContainer,
        string SqlServerImage,
        string DatabaseName,
        int CommandTimeoutSeconds)
    {
        public static bool TryParse(
            IReadOnlyList<string> args,
            out BuildArguments parsed,
            out string error)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < args.Count; index += 2)
            {
                if (index + 1 >= args.Count || !args[index].StartsWith("--", StringComparison.Ordinal))
                {
                    parsed = null!;
                    error = "Expected pairs of --option and value arguments.";
                    return false;
                }
                values[args[index][2..]] = args[index + 1];
            }

            if (!Required("project", values, out var project, out error))
            {
                parsed = null!;
                return false;
            }

            var operation = Value(values, "operation") ?? "generate";
            if (!string.Equals(operation, "generate", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(operation, "run", StringComparison.OrdinalIgnoreCase))
            {
                parsed = null!;
                error = "--operation must be generate or run.";
                return false;
            }
            var requiredPathName = string.Equals(operation, "run", StringComparison.OrdinalIgnoreCase)
                ? "sql"
                : "output";
            if (!Required(requiredPathName, values, out var requiredPath, out error))
            {
                parsed = null!;
                return false;
            }
            var timeout = 60;
            if (Value(values, "timeout") is { } timeoutValue &&
                (!int.TryParse(timeoutValue, out timeout) || timeout <= 0))
            {
                parsed = null!;
                error = "--timeout must be greater than zero.";
                return false;
            }
            if (!TryRuntimeStorage(Value(values, "runtime-storage"), out var runtimeStorage))
            {
                parsed = null!;
                error = "--runtime-storage must be Ephemeral, Durable, or ServiceBroker.";
                return false;
            }

            parsed = new BuildArguments(
                operation,
                project,
                string.Equals(operation, "generate", StringComparison.OrdinalIgnoreCase) ? requiredPath : null,
                string.Equals(operation, "run", StringComparison.OrdinalIgnoreCase) ? requiredPath : null,
                Value(values, "configuration") ?? "Release",
                Value(values, "framework"),
                Value(values, "entry"),
                runtimeStorage,
                Value(values, "connection-name"),
                Value(values, "connection-string-environment"),
                BoolValue(values, "force-container"),
                BoolValue(values, "keep-container"),
                Value(values, "image") ?? "mcr.microsoft.com/mssql/server:2022-latest",
                Value(values, "database") ?? "SharpSql",
                timeout);
            return true;
        }

        private static bool Required(
            string name,
            IReadOnlyDictionary<string, string> values,
            out string value,
            out string error)
        {
            value = Value(values, name) ?? string.Empty;
            error = string.Empty;
            if (!string.IsNullOrWhiteSpace(value))
                return true;
            error = $"--{name} is required.";
            return false;
        }

        private static string? Value(IReadOnlyDictionary<string, string> values, string name) =>
            values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;

        private static bool BoolValue(IReadOnlyDictionary<string, string> values, string name) =>
            bool.TryParse(Value(values, name), out var value) && value;

        private static bool TryRuntimeStorage(string? configured, out RuntimeStorageKind runtimeStorage)
        {
            configured ??= nameof(RuntimeStorageKind.Ephemeral);
            foreach (var candidate in Enum.GetValues<RuntimeStorageKind>())
            {
                if (!string.Equals(configured, candidate.ToString(), StringComparison.OrdinalIgnoreCase))
                    continue;
                runtimeStorage = candidate;
                return true;
            }

            runtimeStorage = default;
            return false;
        }
    }
}
