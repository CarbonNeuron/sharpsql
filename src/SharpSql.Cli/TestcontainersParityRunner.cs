using System.Globalization;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Xml.Linq;
using Docker.DotNet.Models;
using DotNet.Testcontainers.Configurations;
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
    private const int ProfileWarmupRuns = 1;
    private const int ProfileSampleRuns = 3;
    private const string HeapDebugPrefix = "__SHARPSQL_DEBUG_HEAP__|";
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
        var transpileResult = new SharpSqlCompiler().Transpile(
            compilation,
            request.EntryPoint,
            new TranspileOptions { EmitRuntimeDiagnostics = request.Debug });
        reportStage?.Invoke(new ParityStageUpdate(
            ParityStage.SqlGenerated,
            ParityRunResult.CountLines(transpileResult.Sql)));
        reportStage?.Invoke(new ParityStageUpdate(ParityStage.EvaluatingCSharp));
        var preparedCSharp = PrepareCSharp(compilation, request.InputPath, request.EntryPoint);
        var csharpSamples = new List<TimeSpan>();
        ParityOutcome csharp;
        if (preparedCSharp.Failure is not null)
        {
            csharp = preparedCSharp.Failure;
        }
        else if (request.Profile)
        {
            csharp = await ExecuteCSharpProfileAsync(
                preparedCSharp.Program!,
                csharpSamples,
                cancellationToken);
        }
        else
        {
            csharp = await preparedCSharp.Program!.ExecuteAsync();
        }
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

        var containerBuilder = new MsSqlBuilder(request.SqlServerImage)
            .WithLogger(NullLogger.Instance)
            .WithReuse(true)
            .WithLabel("io.sharpsql.verify.reusable", "true");

        var container = containerBuilder.Build();
        string? containerId = null;
        try
        {
            reportStage?.Invoke(new ParityStageUpdate(ParityStage.StartingSqlServer));
            await container.StartAsync(cancellationToken);
            containerId = container.Id;
            reportStage?.Invoke(new ParityStageUpdate(ParityStage.EvaluatingSqlServer));
            await using var connection = new SqlConnection(container.GetConnectionString())
            {
                FireInfoMessageEventOnUserErrors = true
            };
            await connection.OpenAsync(cancellationToken);

            var sqlSamples = new List<TimeSpan>();
            SqlExecutionResult sqlExecution;
            ParityDebugInfo? debugInfo = null;
            if (request.Profile)
            {
                sqlExecution = await ExecuteSqlProfileAsync(
                    connection,
                    transpileResult.Sql,
                    request.CommandTimeoutSeconds,
                    sqlSamples,
                    cancellationToken);
                if (request.Debug && sqlExecution.Outcome.Failure is null)
                {
                    var debugExecution = await ExecuteSqlAsync(
                        connection,
                        transpileResult.Sql,
                        request.CommandTimeoutSeconds,
                        collectDebug: true,
                        cancellationToken);
                    debugInfo = debugExecution.DebugInfo;
                }
            }
            else
            {
                sqlExecution = await ExecuteSqlAsync(
                    connection,
                    transpileResult.Sql,
                    request.CommandTimeoutSeconds,
                    request.Debug,
                    cancellationToken);
                debugInfo = sqlExecution.DebugInfo;
            }

            var profile = request.Profile
                ? new ParityProfile(ProfileWarmupRuns, csharpSamples, sqlSamples)
                : null;
            return new ParityRunResult(
                csharp,
                sqlExecution.Outcome,
                transpileResult.Sql,
                debugInfo,
                profile);
        }
        finally
        {
            if (!request.KeepContainer && containerId is not null)
                await RemoveContainerAsync(containerId, CancellationToken.None);
        }
    }

    private static async Task RemoveContainerAsync(string containerId, CancellationToken cancellationToken)
    {
        using var dockerClient = TestcontainersSettings.OS.DockerEndpointAuthConfig
            .GetDockerClientBuilder(Guid.Empty)
            .Build();
        await dockerClient.Containers.RemoveContainerAsync(
            containerId,
            new ContainerRemoveParameters { Force = true, RemoveVolumes = true },
            cancellationToken);
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

    private static CSharpPreparation PrepareCSharp(
        CSharpCompilation compilation,
        string sourcePath,
        string? requestedEntryPoint)
    {
        using var assemblyStream = new MemoryStream();
        using var symbolsStream = new MemoryStream();
        var emitResult = compilation.Emit(
            assemblyStream,
            symbolsStream,
            options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb));
        if (!emitResult.Success)
        {
            var errors = emitResult.Diagnostics.Where(item => item.Severity == DiagnosticSeverity.Error).ToArray();
            return new CSharpPreparation(
                null,
                new ParityOutcome(
                    string.Empty,
                    new ParityFailure(
                        ParityFailureCategory.Compilation,
                        string.Join(",", errors.Select(item => item.Id).Distinct(StringComparer.Ordinal)),
                        string.Join(Environment.NewLine, errors.Select(item => item.ToString())))));
        }

        return new CSharpPreparation(
            new PreparedCSharpProgram(
                assemblyStream.ToArray(),
                symbolsStream.ToArray(),
                sourcePath,
                requestedEntryPoint,
                compilation.References),
            null);
    }

    private static async Task<ParityOutcome> ExecuteCSharpProfileAsync(
        PreparedCSharpProgram program,
        List<TimeSpan> samples,
        CancellationToken cancellationToken)
    {
        ParityOutcome outcome = new(string.Empty, null);
        for (var index = 0; index < ProfileWarmupRuns; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            outcome = await program.ExecuteAsync();
            if (outcome.Failure is not null)
                return outcome;
        }

        for (var index = 0; index < ProfileSampleRuns; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var timer = Stopwatch.StartNew();
            var sampleOutcome = await program.ExecuteAsync();
            timer.Stop();
            samples.Add(timer.Elapsed);
            if (index == 0)
                outcome = sampleOutcome;
            if (sampleOutcome.Failure is not null)
                return sampleOutcome;
        }
        return outcome;
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
                .FirstOrDefault(item => item.Number is >= 51000 and <= 51999)
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
            if (parts.Length != 2 || !long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
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

    private sealed record CSharpPreparation(
        PreparedCSharpProgram? Program,
        ParityOutcome? Failure);

    private sealed class PreparedCSharpProgram(
        byte[] assemblyBytes,
        byte[] symbolBytes,
        string sourcePath,
        string? requestedEntryPoint,
        IEnumerable<MetadataReference> references)
    {
        private readonly MetadataReference[] _references = references.ToArray();

        public async Task<ParityOutcome> ExecuteAsync()
        {
            await using var assemblyStream = new MemoryStream(assemblyBytes, writable: false);
            await using var symbolsStream = new MemoryStream(symbolBytes, writable: false);
            var loadContext = new VerificationLoadContext(sourcePath, _references);
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
    }

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
