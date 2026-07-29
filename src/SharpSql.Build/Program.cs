using System.Text;

namespace SharpSql.Build;

public static class Program
{
    public static Task<int> Main(string[] args) => RunAsync(args);

    public static async Task<int> RunAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken = default)
    {
        if (!BuildArguments.TryParse(args, out var parsed, out var error))
        {
            Console.Error.WriteLine($"error SSB0001: {error}");
            return 2;
        }

        var result = await new SharpSqlProjectCompiler().TranspileAsync(
            parsed.ProjectPath,
            new ProjectTranspileOptions
            {
                EntryPoint = parsed.EntryPoint,
                Configuration = parsed.Configuration,
                TargetFramework = parsed.TargetFramework
            },
            cancellationToken);
        if (!result.Success)
        {
            foreach (var diagnostic in result.Diagnostics)
                WriteDiagnostic(diagnostic);
            return 1;
        }

        var outputPath = Path.GetFullPath(parsed.OutputPath);
        var outputDirectory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(outputDirectory))
            Directory.CreateDirectory(outputDirectory);
        await File.WriteAllTextAsync(
            outputPath,
            result.Sql,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            cancellationToken);
        Console.WriteLine($"SharpSql generated {outputPath}");
        return 0;
    }

    private static void WriteDiagnostic(CompilerDiagnostic diagnostic)
    {
        var location = string.IsNullOrWhiteSpace(diagnostic.FilePath)
            ? string.Empty
            : $"{diagnostic.FilePath}({diagnostic.Line},{diagnostic.Column}): ";
        Console.Error.WriteLine($"{location}error {diagnostic.Code}: {diagnostic.Message}");
    }

    private sealed record BuildArguments(
        string ProjectPath,
        string OutputPath,
        string Configuration,
        string? TargetFramework,
        string? EntryPoint)
    {
        public static bool TryParse(
            IReadOnlyList<string> args,
            out BuildArguments parsed,
            out string error)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < args.Count; index += 2)
            {
                if (index + 1 >= args.Count || !args[index].StartsWith("--", StringComparison.Ordinal))
                {
                    parsed = null!;
                    error = "Expected pairs of --option and value arguments.";
                    return false;
                }
                values[args[index][2..]] = args[index + 1];
            }

            if (!Required("project", values, out var project, out error) ||
                !Required("output", values, out var output, out error))
            {
                parsed = null!;
                return false;
            }

            parsed = new BuildArguments(
                project,
                output,
                Value(values, "configuration") ?? "Release",
                Value(values, "framework"),
                Value(values, "entry"));
            return true;
        }

        private static bool Required(
            string name,
            IReadOnlyDictionary<string, string> values,
            out string value,
            out string error)
        {
            value = Value(values, name) ?? string.Empty;
            error = string.Empty;
            if (!string.IsNullOrWhiteSpace(value))
                return true;
            error = $"--{name} is required.";
            return false;
        }

        private static string? Value(IReadOnlyDictionary<string, string> values, string name) =>
            values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;
    }
}
