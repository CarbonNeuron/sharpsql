using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using SharpSql.SqlServer;

namespace SharpSql.Cli;

/// <summary>Compares local C# execution with generated SQL running in a SQL Server Testcontainer.</summary>
public sealed partial class TestcontainersParityRunner : IParityRunner
{
    private const int ProfileWarmupRuns = 1;
    private const int ProfileSampleRuns = 3;
    private const string GlobalUsings =
        "global using System; global using System.Collections.Generic; global using System.Linq;";
    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Preview);
    private static readonly IReadOnlyDictionary<string, string> RuntimeAssemblyPaths =
        (((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")) ?? string.Empty)
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        .Select(path => (Name: Path.GetFileNameWithoutExtension(path)!, Path: path))
        .GroupBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(group => group.Key, group => group.First().Path, StringComparer.OrdinalIgnoreCase);
    private static readonly MetadataReference[] References = RuntimeAssemblyPaths.Values
        .Select(path => MetadataReference.CreateFromFile(path))
        .ToArray();

    /// <inheritdoc />
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

        reportStage?.Invoke(new ParityStageUpdate(ParityStage.StartingSqlServer));
        await using var session = await SqlServerSessionFactory.OpenAsync(
            new SqlServerSessionOptions(
                request.InputPath,
                ConnectionString: request.ConnectionString,
                Image: request.SqlServerImage,
                KeepContainer: request.KeepContainer),
            cancellationToken);
        reportStage?.Invoke(new ParityStageUpdate(ParityStage.EvaluatingSqlServer));
        var connection = session.Connection;

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

    private static async Task<ProjectCompilationResult> LoadCompilationAsync(
        ParityRunRequest request,
        CancellationToken cancellationToken)
    {
        if (!request.IsProject)
        {
            if (request.SourcePaths is { Count: > 0 })
            {
                var sources = await Task.WhenAll(request.SourcePaths.Select(async path =>
                    (Path: path, Source: await File.ReadAllTextAsync(path, cancellationToken))));
                return new ProjectCompilationResult(CreateCompilation(sources), []);
            }
            return new ProjectCompilationResult(
                CreateCompilation([(request.InputPath, request.Source!)]),
                []);
        }

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

    private static CSharpCompilation CreateCompilation(IReadOnlyList<(string Path, string Source)> sources)
    {
        var syntaxTrees = sources
            .Select(source => CSharpSyntaxTree.ParseText(
                SourceText.From(source.Source, Encoding.UTF8),
                ParseOptions,
                source.Path))
            .Append(CSharpSyntaxTree.ParseText(
                SourceText.From(GlobalUsings, Encoding.UTF8),
                ParseOptions,
                "GlobalUsings.g.cs"))
            .ToArray();
        return CSharpCompilation.Create(
            $"SharpSqlVerify_{Guid.NewGuid():N}",
            syntaxTrees,
            References,
            new CSharpCompilationOptions(OutputKind.ConsoleApplication, optimizationLevel: OptimizationLevel.Release));
    }

}
