namespace SharpSql.Cli;

internal sealed record SqlOutputArtifactPaths(
    string? ProgramPath,
    string? InstallerPath);

internal static class SqlOutputArtifacts
{
    public static SqlOutputArtifactPaths ResolvePaths(
        string? outputPath,
        string? installerOutputPath,
        RuntimeStorageKind runtimeStorage)
    {
        if (runtimeStorage != RuntimeStorageKind.ServiceBroker)
            return new SqlOutputArtifactPaths(outputPath, null);
        return new SqlOutputArtifactPaths(
            outputPath,
            installerOutputPath ?? (outputPath is null ? null : DefaultInstallerPath(outputPath)));
    }

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
}
