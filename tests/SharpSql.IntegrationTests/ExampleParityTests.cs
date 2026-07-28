using System.Globalization;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;
using Xunit;

namespace SharpSql.IntegrationTests;

public sealed class ExampleParityTests
{
    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Preview);

    [Fact]
    public async Task ExamplesProduceTheSameOutputInCSharpAndSqlServer()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var sqlServer = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest")
            .Build();
        await sqlServer.StartAsync(cancellationToken);

        var examplesDirectory = Path.Combine(AppContext.BaseDirectory, "examples");
        var examplePaths = Directory.GetFiles(examplesDirectory, "*.cs").Order(StringComparer.Ordinal).ToArray();
        Assert.NotEmpty(examplePaths);

        foreach (var examplePath in examplePaths)
        {
            var source = await File.ReadAllTextAsync(examplePath, cancellationToken);
            var csharpOutput = await ExecuteCSharpAsync(source, Path.GetFileName(examplePath));
            var sqlOutput = await ExecuteSqlAsync(source, sqlServer.GetConnectionString(), cancellationToken);

            Assert.True(
                string.Equals(csharpOutput, sqlOutput, StringComparison.Ordinal),
                $"Output differed for {Path.GetFileName(examplePath)}.{Environment.NewLine}" +
                $"C#:{Environment.NewLine}{csharpOutput}{Environment.NewLine}" +
                $"SQL:{Environment.NewLine}{sqlOutput}");
        }
    }

    private static async Task<string> ExecuteCSharpAsync(string source, string exampleName)
    {
        const string globalUsings = "global using System; global using System.Collections.Generic;";
        var syntaxTrees = new[]
        {
            CSharpSyntaxTree.ParseText(source, ParseOptions, exampleName),
            CSharpSyntaxTree.ParseText(globalUsings, ParseOptions, "GlobalUsings.g.cs")
        };
        var references = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))!
            .Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create(
            $"SharpSqlExample_{Guid.NewGuid():N}",
            syntaxTrees,
            references,
            new CSharpCompilationOptions(OutputKind.ConsoleApplication, optimizationLevel: OptimizationLevel.Release));

        await using var assemblyStream = new MemoryStream();
        var emitResult = compilation.Emit(assemblyStream);
        Assert.True(
            emitResult.Success,
            $"C# compilation failed for {exampleName}:{Environment.NewLine}" +
            string.Join(Environment.NewLine, emitResult.Diagnostics));

        assemblyStream.Position = 0;
        var loadContext = new AssemblyLoadContext($"SharpSql example: {exampleName}", isCollectible: true);
        try
        {
            var assembly = loadContext.LoadFromStream(assemblyStream);
            var entryPoint = assembly.EntryPoint ?? throw new InvalidOperationException($"{exampleName} has no entry point.");
            return await CaptureConsoleAsync(() => InvokeEntryPointAsync(entryPoint));
        }
        finally
        {
            loadContext.Unload();
        }
    }

    private static async Task InvokeEntryPointAsync(MethodInfo entryPoint)
    {
        var arguments = entryPoint.GetParameters().Length == 0 ? null : new object?[] { Array.Empty<string>() };
        var result = entryPoint.Invoke(null, arguments);
        if (result is Task task)
            await task;
    }

    private static async Task<string> CaptureConsoleAsync(Func<Task> action)
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
            await action();
            return NormalizeOutput(output.ToString());
        }
        finally
        {
            Console.SetOut(previousOutput);
            CultureInfo.CurrentCulture = previousCulture;
            CultureInfo.CurrentUICulture = previousUiCulture;
        }
    }

    private static async Task<string> ExecuteSqlAsync(
        string source,
        string connectionString,
        CancellationToken cancellationToken)
    {
        var transpileResult = new SharpSqlCompiler().Transpile(source);
        Assert.True(
            transpileResult.Success,
            "Transpilation failed:" + Environment.NewLine + string.Join(Environment.NewLine, transpileResult.Diagnostics));

        using var output = new StringWriter(CultureInfo.InvariantCulture);
        var sqlErrors = new List<string>();
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
                    sqlErrors.Add($"SQL error {error.Number} at line {error.LineNumber}: {error.Message}");
            }
        };
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = transpileResult.Sql;
        command.CommandTimeout = 60;
        await command.ExecuteNonQueryAsync(cancellationToken);
        Assert.Empty(sqlErrors);
        return NormalizeOutput(output.ToString());
    }

    private static string NormalizeOutput(string output) =>
        output.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\r', '\n');
}
