using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using SharpSql;

if (args is ["--delay", var delayValue, "--started", var startedPath, .. var delayedSourcePaths] &&
    int.TryParse(delayValue, out var delayMilliseconds))
{
    await File.WriteAllTextAsync(startedPath, Environment.ProcessId.ToString());
    await Task.Delay(delayMilliseconds);
    args = delayedSourcePaths;
}

if (args.Length == 0)
{
    await Console.Error.WriteLineAsync("Expected at least one C# source path.");
    return 2;
}

try
{
    var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
    var trees = await Task.WhenAll(args.Select(async sourcePath =>
        CSharpSyntaxTree.ParseText(
            await File.ReadAllTextAsync(sourcePath),
            parseOptions,
            sourcePath)));
    var globalUsings = CSharpSyntaxTree.ParseText(
        "global using System; global using System.Collections.Generic; global using System.Linq;",
        parseOptions,
        "SharpSql.GlobalUsings.g.cs");
    var trustedAssemblies = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))?
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries) ?? [];
    var compilation = CSharpCompilation.Create(
        "SharpSqlConformanceInput",
        [.. trees, globalUsings],
        trustedAssemblies.Select(path => MetadataReference.CreateFromFile(path)),
        new CSharpCompilationOptions(OutputKind.ConsoleApplication));
    var clrDiagnostics = compilation.GetDiagnostics()
        .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
        .ToArray();
    if (clrDiagnostics.Length > 0)
    {
        var invalidResponse = new WorkerResponse(
            false,
            false,
            clrDiagnostics.Select(diagnostic => new WorkerDiagnostic(
                diagnostic.Id,
                diagnostic.ToString())).ToArray());
        await Console.Out.WriteAsync(JsonSerializer.Serialize(invalidResponse, WorkerJsonContext.Default.WorkerResponse));
        return 0;
    }

    var result = new SharpSqlCompiler().Transpile(compilation);
    var response = new WorkerResponse(
        true,
        result.Success,
        result.Diagnostics.Select(diagnostic => new WorkerDiagnostic(
            diagnostic.Code,
            diagnostic.ToString())).ToArray());
    await Console.Out.WriteAsync(JsonSerializer.Serialize(response, WorkerJsonContext.Default.WorkerResponse));
    return 0;
}
catch (Exception exception)
{
    await Console.Error.WriteLineAsync(exception.ToString());
    return 1;
}

internal sealed record WorkerResponse(
    bool ClrCompiled,
    bool Transpiled,
    IReadOnlyList<WorkerDiagnostic> Diagnostics);

internal sealed record WorkerDiagnostic(string Code, string Message);

[System.Text.Json.Serialization.JsonSourceGenerationOptions(
    PropertyNamingPolicy = System.Text.Json.Serialization.JsonKnownNamingPolicy.CamelCase)]
[System.Text.Json.Serialization.JsonSerializable(typeof(WorkerResponse))]
internal sealed partial class WorkerJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
