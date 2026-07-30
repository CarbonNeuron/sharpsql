using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.MSBuild;

namespace SharpSql;

/// <summary>Loads C# projects through MSBuild and transpiles them with SharpSql.</summary>
public sealed class SharpSqlProjectCompiler
{
    private static readonly object RegistrationLock = new();

    /// <summary>Loads and transpiles a C# project.</summary>
    /// <param name="projectPath">The project file path.</param>
    /// <param name="options">Optional project-loading and compiler settings.</param>
    /// <param name="cancellationToken">A token that can cancel project loading.</param>
    /// <returns>The generated SQL and any diagnostics.</returns>
    public async Task<TranspileResult> TranspileAsync(
        string projectPath,
        ProjectTranspileOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new ProjectTranspileOptions();
        var loaded = await LoadCompilationAsync(projectPath, options, cancellationToken);
        if (!loaded.Success)
            return new TranspileResult(string.Empty, loaded.Diagnostics);

        return new SharpSqlCompiler().Transpile(
            loaded.Compilation!,
            options.EntryPoint,
            options.CompilerOptions);
    }

    /// <summary>Loads a C# compilation from an MSBuild project.</summary>
    /// <param name="projectPath">The project file path.</param>
    /// <param name="options">Optional project-loading settings.</param>
    /// <param name="cancellationToken">A token that can cancel project loading.</param>
    /// <returns>The loaded compilation and any diagnostics.</returns>
    public async Task<ProjectCompilationResult> LoadCompilationAsync(
        string projectPath,
        ProjectTranspileOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        options ??= new ProjectTranspileOptions();
        projectPath = Path.GetFullPath(projectPath);
        if (!File.Exists(projectPath))
            return CompilationFailure("SSP0001", $"Project file was not found: {projectPath}");

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
                return CompilationFailure("SSP0003", $"Project '{projectPath}' did not produce a C# compilation.", workspaceFailures);

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
                return new ProjectCompilationResult(null, compilationErrors.Concat(workspaceFailures).ToArray());

            return new ProjectCompilationResult(compilation, workspaceFailures.Distinct().ToArray());
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return CompilationFailure("SSP0003", $"Could not load project '{projectPath}': {exception.Message}", workspaceFailures);
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

    private static ProjectCompilationResult CompilationFailure(
        string code,
        string message,
        IEnumerable<CompilerDiagnostic>? additionalDiagnostics = null)
    {
        var failure = Failure(code, message, additionalDiagnostics);
        return new ProjectCompilationResult(null, failure.Diagnostics);
    }
}
