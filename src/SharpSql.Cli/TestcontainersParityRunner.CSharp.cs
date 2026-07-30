using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using SharpSql.SqlServer;

namespace SharpSql.Cli;

/// <inheritdoc />
public sealed partial class TestcontainersParityRunner
{
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

    internal static async Task<ParityOutcome> ExecuteProjectCSharpForTestingAsync(
        CSharpCompilation compilation,
        string sourcePath,
        string? requestedEntryPoint)
    {
        var prepared = PrepareCSharp(compilation, sourcePath, requestedEntryPoint);
        return prepared.Failure ?? await prepared.Program!.ExecuteAsync();
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
                return new ParityOutcome(SqlBatchOutput.Normalize(output.ToString()), null);
            }
            catch (Exception exception)
            {
                exception = Unwrap(exception);
                return new ParityOutcome(
                    SqlBatchOutput.Normalize(output.ToString()),
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

        /// <summary>Executes the prepared C# program and captures its observable outcome.</summary>
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

            // Project compilations normally reference the targeting pack under
            // packs/Microsoft.NETCore.App.Ref. Those files are metadata-only and
            // throw BadImageFormatException when loaded for execution. Resolve
            // framework identities through the default runtime context first.
            try
            {
                return Default.LoadFromAssemblyName(assemblyName);
            }
            catch (FileNotFoundException)
            {
                // Fall through to the compilation reference for project and
                // package assemblies that are not part of the shared runtime.
            }
            catch (FileLoadException)
            {
                // The exact requested version may differ from the active shared
                // framework. Its trusted-platform path is the next best match.
            }

            if (assemblyName.Name is not null &&
                RuntimeAssemblyPaths.TryGetValue(assemblyName.Name, out var runtimePath))
                return Default.LoadFromAssemblyPath(runtimePath);

            return assemblyName.Name is not null && _referencePaths.TryGetValue(assemblyName.Name, out var path)
                ? LoadFromAssemblyPath(path)
                : null;
        }
    }
}
