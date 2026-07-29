using System.Globalization;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Testcontainers.MsSql;

namespace SharpSql.Cli;

public sealed class TestcontainersParityRunner : IParityRunner
{
    private const string GlobalUsings =
        "global using System; global using System.Collections.Generic; global using System.Linq;";
    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Preview);
    private static readonly MetadataReference[] References =
        (((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")) ?? string.Empty)
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        .Select(path => MetadataReference.CreateFromFile(path))
        .ToArray();

    public async Task<ParityRunResult> RunAsync(
        ParityRunRequest request,
        Action<ParityStageUpdate>? reportStage,
        CancellationToken cancellationToken)
    {
        reportStage?.Invoke(new ParityStageUpdate(ParityStage.Parsing));
        var compilationResult = await LoadCompilationAsync(request, cancellationToken);
        if (!compilationResult.Success)
        {
            var failure = CompilationFailure(compilationResult.Diagnostics);
            return new ParityRunResult(
                new ParityOutcome(string.Empty, failure),
                new ParityOutcome(string.Empty, failure with { Category = ParityFailureCategory.Transpilation }),
                string.Empty);
        }

        var compilation = compilationResult.Compilation!;
        reportStage?.Invoke(new ParityStageUpdate(ParityStage.SqlGenerated));
        var transpileResult = new SharpSqlCompiler().Transpile(compilation, request.EntryPoint);
        reportStage?.Invoke(new ParityStageUpdate(
            ParityStage.SqlGenerated,
            ParityRunResult.CountLines(transpileResult.Sql)));
        reportStage?.Invoke(new ParityStageUpdate(ParityStage.EvaluatingCSharp));
        var csharp = await ExecuteCSharpAsync(compilation, request.InputPath, request.EntryPoint);
        if (!transpileResult.Success)
        {
            var failure = new ParityFailure(
                ParityFailureCategory.Transpilation,
                string.Join(",", transpileResult.Diagnostics.Select(item => item.Code).Distinct(StringComparer.Ordinal)),
                string.Join(Environment.NewLine, transpileResult.Diagnostics));
            return new ParityRunResult(csharp, new ParityOutcome(string.Empty, failure), transpileResult.Sql);
        }

        if (csharp.Failure is { Category: ParityFailureCategory.Compilation })
            return new ParityRunResult(csharp, new ParityOutcome(string.Empty, null), transpileResult.Sql);

        await using var container = new MsSqlBuilder(request.SqlServerImage)
            .WithLogger(NullLogger.Instance)
            .Build();
        reportStage?.Invoke(new ParityStageUpdate(ParityStage.StartingSqlServer));
        await container.StartAsync(cancellationToken);
        reportStage?.Invoke(new ParityStageUpdate(ParityStage.EvaluatingSqlServer));
        var sqlServer = await ExecuteSqlAsync(
            transpileResult.Sql,
            container.GetConnectionString(),
            request.CommandTimeoutSeconds,
            cancellationToken);
        return new ParityRunResult(csharp, sqlServer, transpileResult.Sql);
    }

    private static async Task<ProjectCompilationResult> LoadCompilationAsync(
        ParityRunRequest request,
        CancellationToken cancellationToken)
    {
        if (!request.IsProject)
            return new ProjectCompilationResult(
                CreateCompilation(request.Source!, request.InputPath),
                []);

        return await new SharpSqlProjectCompiler().LoadCompilationAsync(
            request.InputPath,
            new ProjectTranspileOptions
            {
                EntryPoint = request.EntryPoint,
                Configuration = request.Configuration,
                TargetFramework = request.TargetFramework
            },
            cancellationToken);
    }

    private static ParityFailure CompilationFailure(IReadOnlyList<CompilerDiagnostic> diagnostics) => new(
        ParityFailureCategory.Compilation,
        string.Join(",", diagnostics.Select(item => item.Code).Distinct(StringComparer.Ordinal)),
        string.Join(Environment.NewLine, diagnostics));

    private static CSharpCompilation CreateCompilation(string source, string sourcePath)
    {
        var syntaxTrees = new[]
        {
            CSharpSyntaxTree.ParseText(SourceText.From(source, Encoding.UTF8), ParseOptions, sourcePath),
            CSharpSyntaxTree.ParseText(SourceText.From(GlobalUsings, Encoding.UTF8), ParseOptions, "GlobalUsings.g.cs")
        };
        return CSharpCompilation.Create(
            $"SharpSqlVerify_{Guid.NewGuid():N}",
            syntaxTrees,
            References,
            new CSharpCompilationOptions(OutputKind.ConsoleApplication, optimizationLevel: OptimizationLevel.Release));
    }

    private static async Task<ParityOutcome> ExecuteCSharpAsync(
        CSharpCompilation compilation,
        string sourcePath,
        string? requestedEntryPoint)
    {
        await using var assemblyStream = new MemoryStream();
        await using var symbolsStream = new MemoryStream();
        var emitResult = compilation.Emit(
            assemblyStream,
            symbolsStream,
            options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb));
        if (!emitResult.Success)
        {
            var errors = emitResult.Diagnostics.Where(item => item.Severity == DiagnosticSeverity.Error).ToArray();
            return new ParityOutcome(
                string.Empty,
                new ParityFailure(
                    ParityFailureCategory.Compilation,
                    string.Join(",", errors.Select(item => item.Id).Distinct(StringComparer.Ordinal)),
                    string.Join(Environment.NewLine, errors.Select(item => item.ToString()))));
        }

        assemblyStream.Position = 0;
        symbolsStream.Position = 0;
        var loadContext = new VerificationLoadContext(sourcePath, compilation.References);
        try
        {
            var assembly = loadContext.LoadFromStream(assemblyStream, symbolsStream);
            return await CaptureConsoleAsync(async () =>
            {
                var entryPoint = ResolveEntryPoint(assembly, requestedEntryPoint) ??
                                 throw new InvalidOperationException($"{sourcePath} has no matching entry point.");
                await InvokeEntryPointAsync(entryPoint);
            });
        }
        finally
        {
            loadContext.Unload();
        }
    }

    private static MethodInfo? ResolveEntryPoint(Assembly assembly, string? requestedEntryPoint)
    {
        if (string.IsNullOrWhiteSpace(requestedEntryPoint))
            return assembly.EntryPoint;

        var requested = requestedEntryPoint.Trim();
        return assembly.GetTypes()
            .SelectMany(type => type.GetMethods(
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            .Where(method => method.GetParameters().Length == 0)
            .Where(method =>
            {
                var typeName = method.DeclaringType?.FullName?.Replace('+', '.');
                return string.Equals(method.Name, requested, StringComparison.Ordinal) ||
                       string.Equals($"{typeName}.{method.Name}", requested, StringComparison.Ordinal) ||
                       string.Equals($"{typeName}::{method.Name}", requested, StringComparison.Ordinal);
            })
            .SingleOrDefault();
    }

    private static async Task<ParityOutcome> ExecuteSqlAsync(
        string sql,
        string connectionString,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        var reportedErrors = new List<SqlErrorInfo>();
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
                    reportedErrors.Add(new SqlErrorInfo(error.Number, error.Message));
            }
        };

        await connection.OpenAsync(cancellationToken);
        ParityFailure? failure = null;
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.CommandTimeout = commandTimeoutSeconds;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (SqlException exception)
        {
            var error = exception.Errors.Cast<SqlError>()
                .FirstOrDefault(item => item.Number is >= 51000 and <= 51999)
                ?? exception.Errors.Cast<SqlError>().First();
            failure = NormalizeSqlFailure(new SqlErrorInfo(error.Number, error.Message));
        }

        failure ??= reportedErrors.Count > 0 ? NormalizeSqlFailure(reportedErrors[0]) : null;
        return new ParityOutcome(NormalizeOutput(output.ToString()), failure);
    }

    private static async Task<ParityOutcome> CaptureConsoleAsync(Func<Task> action)
    {
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
                await action();
                return new ParityOutcome(NormalizeOutput(output.ToString()), null);
            }
            catch (Exception exception)
            {
                exception = Unwrap(exception);
                return new ParityOutcome(
                    NormalizeOutput(output.ToString()),
                    new ParityFailure(
                        ParityFailureCategory.Runtime,
                        exception.GetType().Name,
                        exception.Message));
            }
        }
        finally
        {
            Console.SetOut(previousOutput);
            CultureInfo.CurrentCulture = previousCulture;
            CultureInfo.CurrentUICulture = previousUiCulture;
        }
    }

    private static async Task InvokeEntryPointAsync(MethodInfo entryPoint)
    {
        var arguments = entryPoint.GetParameters().Length == 0 ? null : new object?[] { Array.Empty<string>() };
        if (entryPoint.Invoke(null, arguments) is Task task)
            await task;
    }

    private static Exception Unwrap(Exception exception)
    {
        while (exception is TargetInvocationException { InnerException: not null } or
               TypeInitializationException { InnerException: not null })
            exception = exception.InnerException!;
        return exception;
    }

    private static ParityFailure NormalizeSqlFailure(SqlErrorInfo failure)
    {
        var type = failure.Number switch
        {
            51001 => nameof(ArgumentException),
            51002 or 51004 or 51005 or 51006 or 51009 => nameof(ArgumentOutOfRangeException),
            51003 => nameof(IndexOutOfRangeException),
            51007 or 51008 => nameof(InvalidOperationException),
            51010 => nameof(KeyNotFoundException),
            _ => nameof(SqlException)
        };
        return new ParityFailure(ParityFailureCategory.Runtime, type, failure.Message, failure.Number);
    }

    private static string NormalizeOutput(string output) =>
        output.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\r', '\n');

    private sealed class VerificationLoadContext(
        string sourcePath,
        IEnumerable<MetadataReference> references)
        : AssemblyLoadContext($"SharpSql verify: {sourcePath}", isCollectible: true)
    {
        private readonly IReadOnlyDictionary<string, string> _referencePaths = references
            .OfType<PortableExecutableReference>()
            .Select(reference => reference.FilePath)
            .Where(path => path is not null && File.Exists(path))
            .Select(path => path!)
            .Select(path => (Name: Path.GetFileNameWithoutExtension(path), Path: path))
            .Where(item => !string.IsNullOrEmpty(item.Name))
            .GroupBy(item => item.Name!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Path, StringComparer.OrdinalIgnoreCase);

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            var loaded = Default.Assemblies.FirstOrDefault(assembly =>
                AssemblyName.ReferenceMatchesDefinition(assembly.GetName(), assemblyName));
            if (loaded is not null)
                return loaded;

            return assemblyName.Name is not null && _referencePaths.TryGetValue(assemblyName.Name, out var path)
                ? LoadFromAssemblyPath(path)
                : null;
        }
    }

    private sealed record SqlErrorInfo(int Number, string Message);
}
