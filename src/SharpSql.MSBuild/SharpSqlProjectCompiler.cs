using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.MSBuild;

namespace SharpSql;

public sealed class SharpSqlProjectCompiler
{
    private static readonly object RegistrationLock = new();

    public async Task<TranspileResult> TranspileAsync(
        string projectPath,
        ProjectTranspileOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        options ??= new ProjectTranspileOptions();
        projectPath = Path.GetFullPath(projectPath);
        if (!File.Exists(projectPath))
            return Failure("SSP0001", $"Project file was not found: {projectPath}");

        EnsureMSBuildRegistered();
        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Configuration"] = options.Configuration
        };
        if (!string.IsNullOrWhiteSpace(options.TargetFramework))
            properties["TargetFramework"] = options.TargetFramework;

        using var workspace = MSBuildWorkspace.Create(properties);
        var workspaceFailures = new ConcurrentQueue<CompilerDiagnostic>();
        workspace.RegisterWorkspaceFailedHandler(args =>
        {
            if (args.Diagnostic.Kind == WorkspaceDiagnosticKind.Failure)
                workspaceFailures.Enqueue(new CompilerDiagnostic("SSP0002", args.Diagnostic.Message, 0, 0));
        });

        try
        {
            var project = await workspace.OpenProjectAsync(projectPath, cancellationToken: cancellationToken);
            var compilation = await project.GetCompilationAsync(cancellationToken) as CSharpCompilation;
            if (compilation is null)
                return Failure("SSP0003", $"Project '{projectPath}' did not produce a C# compilation.", workspaceFailures);

            var compilationErrors = compilation.GetDiagnostics(cancellationToken)
                .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
                .Select(diagnostic =>
                {
                    var line = diagnostic.Location.GetLineSpan();
                    return new CompilerDiagnostic(
                        diagnostic.Id,
                        diagnostic.GetMessage(CultureInfo.InvariantCulture),
                        line.StartLinePosition.Line + 1,
                        line.StartLinePosition.Character + 1,
                        line.Path);
                })
                .ToArray();
            if (compilationErrors.Length > 0)
                return new TranspileResult(string.Empty, compilationErrors.Concat(workspaceFailures).ToArray());

            var result = new SharpSqlCompiler().Transpile(compilation, options.EntryPoint, options.CompilerOptions);
            if (workspaceFailures.Count == 0)
                return result;
            return result with
            {
                Diagnostics = result.Diagnostics.Concat(workspaceFailures).Distinct().ToArray()
            };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Failure("SSP0003", $"Could not load project '{projectPath}': {exception.Message}", workspaceFailures);
        }
    }

    private static void EnsureMSBuildRegistered()
    {
        if (MSBuildLocator.IsRegistered)
            return;
        lock (RegistrationLock)
        {
            if (!MSBuildLocator.IsRegistered)
                MSBuildLocator.RegisterDefaults();
        }
    }

    private static TranspileResult Failure(
        string code,
        string message,
        IEnumerable<CompilerDiagnostic>? additionalDiagnostics = null)
    {
        var diagnostics = new List<CompilerDiagnostic> { new(code, message, 0, 0) };
        if (additionalDiagnostics is not null)
            diagnostics.AddRange(additionalDiagnostics);
        return new TranspileResult(string.Empty, diagnostics);
    }
}
