namespace SharpSql.IntegrationTests;

public sealed record ParityCase(string RelativePath, string Source)
{
    private const string DirectivePrefix = "// ";

    public string RequiredDirective(string name)
    {
        var prefix = $"{DirectivePrefix}{name}:";
        var value = Source.Split('\n')
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.StartsWith(prefix, StringComparison.Ordinal))?[prefix.Length..]
            .Trim();

        return !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidOperationException($"{RelativePath} must declare '{prefix} ...'.");
    }

    public static async Task<ParityCase> LoadAsync(string relativePath, CancellationToken cancellationToken)
    {
        var fullPath = Path.Combine(
            ParityCases.RootDirectory,
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        return new ParityCase(relativePath, await File.ReadAllTextAsync(fullPath, cancellationToken));
    }
}

public static class ParityCases
{
    public static string RootDirectory => Path.Combine(AppContext.BaseDirectory, "cases");

    public static IEnumerable<object[]> Discover(string category)
    {
        var directory = Path.Combine(RootDirectory, category);
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException($"Parity case directory was not copied to the test output: {directory}");

        var paths = Directory.GetFiles(directory, "*.cs", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(RootDirectory, path).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToArray();

        if (paths.Length == 0)
            throw new InvalidOperationException($"Parity category '{category}' has no cases.");

        return paths.Select(path => new object[] { path });
    }
}
