using Xunit;

namespace SharpSql.Tests;

public sealed class BackendArchitectureTests
{
    private static readonly string[] BackendSourceFiles =
    [
        "SharpSqlCompiler.IrBackend.cs",
        "SharpSqlCompiler.Vm.cs",
        "SharpSqlCompiler.Vm.Expressions.cs",
        "SharpSqlCompiler.Vm.Statements.cs",
        "SharpSqlCompiler.Vm.Storage.cs"
    ];

    private static readonly string[] ForbiddenRoslynDependencies =
    [
        "Microsoft.CodeAnalysis.CSharp.Syntax",
        "_csharpSourceNodes",
        "CSharpSyntax<",
        "SemanticModelFor"
    ];

    // Removal-only migration debt. Exact source lines keep the exception narrow and
    // make the test fail when an adapter moves, changes, or can be deleted.
    private static readonly string[] TemporaryRoslynDependencyAllowlist =
    [
        "SharpSqlCompiler.Vm.cs|using Microsoft.CodeAnalysis.CSharp.Syntax;",
        "SharpSqlCompiler.Vm.Expressions.cs|using Microsoft.CodeAnalysis.CSharp.Syntax;",
        "SharpSqlCompiler.Vm.Statements.cs|using Microsoft.CodeAnalysis.CSharp.Syntax;"
    ];

    [Fact]
    public void BackendBoundaryInventoryCoversSqlAndVmSourceFiles()
    {
        var actual = Directory.EnumerateFiles(SharpSqlSourceDirectory, "*.cs")
            .Select(Path.GetFileName)
            .Where(file =>
                file == "SharpSqlCompiler.IrBackend.cs" ||
                file!.StartsWith("SharpSqlCompiler.IrBackend.", StringComparison.Ordinal) ||
                file == "SharpSqlCompiler.Vm.cs" ||
                file.StartsWith("SharpSqlCompiler.Vm.", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal);

        Assert.Equal(BackendSourceFiles.Order(StringComparer.Ordinal), actual);
    }

    [Fact]
    public void SqlAndVmBackendsDoNotAcquireNewRoslynDependencies()
    {
        var remainingAllowlist = TemporaryRoslynDependencyAllowlist.ToHashSet(StringComparer.Ordinal);
        var violations = new List<string>();

        foreach (var file in BackendSourceFiles)
        {
            var lines = File.ReadAllLines(Path.Combine(SharpSqlSourceDirectory, file));
            for (var index = 0; index < lines.Length; index++)
            {
                var line = lines[index].Trim();
                if (!ForbiddenRoslynDependencies.Any(line.Contains))
                    continue;

                if (!remainingAllowlist.Remove($"{file}|{line}"))
                    violations.Add($"{file}:{index + 1}: {line}");
            }
        }

        Assert.True(
            violations.Count == 0,
            "SQL/VM backend sources must depend only on compiler IR, not Roslyn syntax or semantic state:" +
            Environment.NewLine + string.Join(Environment.NewLine, violations));
        Assert.True(
            remainingAllowlist.Count == 0,
            "Remove stale temporary Roslyn dependency allowlist entries:" +
            Environment.NewLine + string.Join(Environment.NewLine, remainingAllowlist.Order(StringComparer.Ordinal)));
    }

    private static string SharpSqlSourceDirectory =>
        Path.Combine(FindRepositoryRoot(), "src", "SharpSql");

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "SharpSql.slnx")))
                return directory.FullName;
        }

        throw new DirectoryNotFoundException(
            $"Could not find the SharpSql repository above '{AppContext.BaseDirectory}'.");
    }
}
