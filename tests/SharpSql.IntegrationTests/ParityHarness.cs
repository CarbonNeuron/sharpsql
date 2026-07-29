using System.Globalization;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Data.SqlClient;

namespace SharpSql.IntegrationTests;

public enum FailureCategory
{
    Compilation,
    Transpilation,
    Runtime
}

public sealed record ExecutionFailure(FailureCategory Category, string Type, string Message, int? Code = null);

public sealed record ExecutionOutcome(string StandardOutput, string? ReturnValue, ExecutionFailure? Failure);

public sealed record SqlExecutionOutcome(ExecutionOutcome Outcome, string GeneratedSql);

public static class ParityHarness
{
    private const string GlobalUsings = "global using System; global using System.Collections.Generic;";
    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Preview);
    private static readonly SemaphoreSlim ConsoleCaptureGate = new(1, 1);
    private static readonly MetadataReference[] References = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))!
        .Split(Path.PathSeparator)
        .Select(path => MetadataReference.CreateFromFile(path))
        .ToArray();

    public static IReadOnlyList<Diagnostic> GetCSharpCompilationErrors(ParityCase testCase) =>
        CreateCompilation(testCase).GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ToArray();

    public static async Task<ExecutionOutcome> ExecuteCSharpAsync(ParityCase testCase)
    {
        var compilation = CreateCompilation(testCase);
        await using var assemblyStream = new MemoryStream();
        await using var symbolsStream = new MemoryStream();
        var emitResult = compilation.Emit(
            assemblyStream,
            symbolsStream,
            options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb));
        if (!emitResult.Success)
        {
            var errors = emitResult.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToArray();
            return new ExecutionOutcome(
                string.Empty,
                null,
                new ExecutionFailure(
                    FailureCategory.Compilation,
                    string.Join(",", errors.Select(diagnostic => diagnostic.Id).Distinct(StringComparer.Ordinal)),
                    string.Join(Environment.NewLine, errors.Select(diagnostic => diagnostic.ToString()))));
        }

        assemblyStream.Position = 0;
        symbolsStream.Position = 0;
        var loadContext = new AssemblyLoadContext($"SharpSql parity: {testCase.RelativePath}", isCollectible: true);
        try
        {
            var assembly = loadContext.LoadFromStream(assemblyStream, symbolsStream);
            var entryPoint = assembly.EntryPoint ?? throw new InvalidOperationException($"{testCase.RelativePath} has no entry point.");
            return await CaptureConsoleAsync(() => InvokeEntryPointAsync(entryPoint));
        }
        finally
        {
            loadContext.Unload();
        }
    }

    public static async Task<SqlExecutionOutcome> ExecuteSqlAsync(
        ParityCase testCase,
        string connectionString,
        CancellationToken cancellationToken)
    {
        var transpileResult = new SharpSqlCompiler().Transpile(testCase.Source);
        if (!transpileResult.Success)
        {
            var failure = new ExecutionFailure(
                FailureCategory.Transpilation,
                string.Join(",", transpileResult.Diagnostics.Select(diagnostic => diagnostic.Code).Distinct(StringComparer.Ordinal)),
                string.Join(Environment.NewLine, transpileResult.Diagnostics.Select(diagnostic => diagnostic.ToString())));
            return new SqlExecutionOutcome(new ExecutionOutcome(string.Empty, null, failure), transpileResult.Sql);
        }

        using var output = new StringWriter(CultureInfo.InvariantCulture);
        var errors = new List<SqlFailure>();
        await using var connection = new SqlConnection(connectionString)
        {
            FireInfoMessageEventOnUserErrors = true
        };
        connection.InfoMessage += (_, args) =>
        {
            foreach (SqlError error in args.Errors)
            {
                if (error.Class == 0)
                    output.WriteLine(error.Message);
                else
                    errors.Add(new SqlFailure(error.Number, error.Message));
            }
        };

        await connection.OpenAsync(cancellationToken);
        ExecutionFailure? executionFailure = null;
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = transpileResult.Sql;
            command.CommandTimeout = 60;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (SqlException exception)
        {
            var error = exception.Errors.Cast<SqlError>()
                .FirstOrDefault(candidate => RuntimeErrorCatalog.IsSharpSqlRuntimeError(candidate.Number))
                ?? exception.Errors.Cast<SqlError>().First();
            executionFailure = NormalizeSqlFailure(new SqlFailure(error.Number, error.Message));
        }

        executionFailure ??= errors.Count > 0 ? NormalizeSqlFailure(errors[0]) : null;
        return new SqlExecutionOutcome(
            new ExecutionOutcome(NormalizeOutput(output.ToString()), null, executionFailure),
            transpileResult.Sql);
    }

    public static string FormatComparisonFailure(
        ParityCase testCase,
        ExecutionOutcome csharp,
        SqlExecutionOutcome sql,
        string? expectedException = null)
    {
        var report = new StringBuilder()
            .AppendLine($"Parity case failed: {testCase.RelativePath}");
        if (expectedException is not null)
            report.AppendLine($"Expected exception: {expectedException}");
        report.AppendLine($"C# outcome: {FormatOutcome(csharp)}")
            .AppendLine($"SQL outcome: {FormatOutcome(sql.Outcome)}")
            .AppendLine("Generated SQL:")
            .AppendLine(sql.GeneratedSql)
            .AppendLine("Source:")
            .Append(testCase.Source);
        return report.ToString();
    }

    public static string FormatDiagnosticFailure(
        ParityCase testCase,
        IReadOnlyList<string> expectedCodes,
        IReadOnlyList<string> actualCodes,
        IReadOnlyList<Diagnostic> csharpDiagnostics,
        IReadOnlyList<CompilerDiagnostic> sharpSqlDiagnostics) =>
        $"Diagnostic case failed: {testCase.RelativePath}{Environment.NewLine}" +
        $"Expected SharpSql codes: {string.Join(", ", expectedCodes)}{Environment.NewLine}" +
        $"Actual SharpSql codes: {string.Join(", ", actualCodes)}{Environment.NewLine}" +
        $"C# errors: {string.Join(Environment.NewLine, csharpDiagnostics)}{Environment.NewLine}" +
        $"SharpSql diagnostics: {string.Join(Environment.NewLine, sharpSqlDiagnostics)}{Environment.NewLine}" +
        $"Source:{Environment.NewLine}{testCase.Source}";

    private static CSharpCompilation CreateCompilation(ParityCase testCase)
    {
        var syntaxTrees = new[]
        {
            CSharpSyntaxTree.ParseText(SourceText.From(testCase.Source, Encoding.UTF8), ParseOptions, testCase.RelativePath),
            CSharpSyntaxTree.ParseText(SourceText.From(GlobalUsings, Encoding.UTF8), ParseOptions, "GlobalUsings.g.cs")
        };
        return CSharpCompilation.Create(
            $"SharpSqlParity_{Guid.NewGuid():N}",
            syntaxTrees,
            References,
            new CSharpCompilationOptions(OutputKind.ConsoleApplication, optimizationLevel: OptimizationLevel.Release));
    }

    private static async Task<ExecutionOutcome> CaptureConsoleAsync(Func<Task<string?>> action)
    {
        await ConsoleCaptureGate.WaitAsync();
        var previousOutput = Console.Out;
        var previousCulture = CultureInfo.CurrentCulture;
        var previousUiCulture = CultureInfo.CurrentUICulture;
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        try
        {
            Console.SetOut(output);
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
            try
            {
                var returnValue = await action();
                return new ExecutionOutcome(NormalizeOutput(output.ToString()), returnValue, null);
            }
            catch (Exception exception)
            {
                exception = Unwrap(exception);
                return new ExecutionOutcome(
                    NormalizeOutput(output.ToString()),
                    null,
                    new ExecutionFailure(FailureCategory.Runtime, exception.GetType().Name, exception.Message));
            }
        }
        finally
        {
            Console.SetOut(previousOutput);
            CultureInfo.CurrentCulture = previousCulture;
            CultureInfo.CurrentUICulture = previousUiCulture;
            ConsoleCaptureGate.Release();
        }
    }

    private static async Task<string?> InvokeEntryPointAsync(MethodInfo entryPoint)
    {
        var arguments = entryPoint.GetParameters().Length == 0 ? null : new object?[] { Array.Empty<string>() };
        var result = entryPoint.Invoke(null, arguments);
        if (result is not Task task)
            return result is null ? null : Convert.ToString(result, CultureInfo.InvariantCulture);

        await task;
        var taskResult = task.GetType().GetProperty("Result")?.GetValue(task);
        return taskResult is null ? null : Convert.ToString(taskResult, CultureInfo.InvariantCulture);
    }

    private static Exception Unwrap(Exception exception)
    {
        while (exception is TargetInvocationException { InnerException: not null } or TypeInitializationException { InnerException: not null })
            exception = exception.InnerException!;
        return exception;
    }

    private static ExecutionFailure NormalizeSqlFailure(SqlFailure failure)
    {
        var type = RuntimeErrorCatalog.ExceptionTypeName(failure.Number) ?? nameof(SqlException);
        return new ExecutionFailure(FailureCategory.Runtime, type, failure.Message, failure.Number);
    }

    private static string FormatOutcome(ExecutionOutcome outcome) =>
        outcome.Failure is null
            ? $"success; stdout={Quote(outcome.StandardOutput)}; return={Quote(outcome.ReturnValue)}"
            : $"{outcome.Failure.Category}/{outcome.Failure.Type}" +
              (outcome.Failure.Code is null ? string.Empty : $" ({outcome.Failure.Code})") +
              $"; message={Quote(outcome.Failure.Message)}; stdout={Quote(outcome.StandardOutput)}";

    private static string Quote(string? value) => value is null ? "<null>" : $"\"{value.Replace("\n", "\\n", StringComparison.Ordinal)}\"";

    private static string NormalizeOutput(string output) =>
        output.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\r', '\n');

    private sealed record SqlFailure(int Number, string Message);
}
