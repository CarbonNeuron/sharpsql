namespace SharpSql.Cli;

internal sealed record SqlOutputArtifactPaths(
    string? ProgramPath,
    string? InstallerPath);

internal static class SqlOutputArtifacts
{
    /// <summary>Resolves program and installer output paths for the selected runtime storage.</summary>
    public static SqlOutputArtifactPaths ResolvePaths(
        string? outputPath,
        string? installerOutputPath,
        RuntimeConfiguration runtime)
    {
        if (!RequiresInstaller(runtime))
            return new SqlOutputArtifactPaths(outputPath, null);
        return new SqlOutputArtifactPaths(
            outputPath,
            installerOutputPath ?? (outputPath is null ? null : DefaultInstallerPath(outputPath)));
    }

    /// <summary>Writes requested SQL artifacts to disk.</summary>
    public static async Task WriteAsync(
        SqlOutputArtifactPaths paths,
        string? programSql,
        string? installerSql,
        CancellationToken cancellationToken)
    {
        if (paths.ProgramPath is not null && programSql is not null)
            await File.WriteAllTextAsync(paths.ProgramPath, programSql, cancellationToken);
        if (paths.InstallerPath is not null && installerSql is not null)
            await File.WriteAllTextAsync(paths.InstallerPath, installerSql, cancellationToken);
    }

    internal static string DefaultInstallerPath(string outputPath)
    {
        var fullPath = Path.GetFullPath(outputPath);
        var directory = Path.GetDirectoryName(fullPath)!;
        var extension = Path.GetExtension(fullPath);
        var fileName = Path.GetFileNameWithoutExtension(fullPath);
        return Path.Combine(
            directory,
            extension.Length == 0
                ? $"{fileName}.installer.sql"
                : $"{fileName}.installer{extension}");
    }

    internal static bool RequiresInstaller(RuntimeConfiguration runtime) =>
        runtime.UseMemoryOptimizedTables || runtime.Execution == RuntimeExecutionKind.ServiceBroker;

    internal static bool MayRequireInstaller(RuntimeConfiguration runtime) =>
        runtime.UseMemoryOptimizedTables ||
        runtime.Execution is RuntimeExecutionKind.Auto or RuntimeExecutionKind.ServiceBroker;

    internal static string? InstallerSql(RuntimeConfiguration runtime)
    {
        var installers = new List<string>(2);
        if (runtime.UseMemoryOptimizedTables)
            installers.Add(SharpSqlMemoryOptimizedRuntime.GenerateProvisioningSql(runtime));
        if (runtime.Execution == RuntimeExecutionKind.ServiceBroker)
            installers.Add(SharpSqlServiceBrokerRuntime.GenerateProvisioningSql());
        return installers.Count == 0
            ? null
            : string.Join(Environment.NewLine, installers);
    }
}
