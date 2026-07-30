using System.Diagnostics;
using SharpSql.Conformance;

namespace SharpSql.Cli;

internal static class MonoTestCorpus
{
    /// <summary>Finds the SharpSql repository containing the specified directory.</summary>
    public static string FindRepositoryRoot(string startDirectory)
    {
        var directory = new DirectoryInfo(Path.GetFullPath(startDirectory));
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "SharpSql.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate the SharpSql repository. Run this command from inside a SharpSql checkout.");
    }

    /// <summary>Downloads the Mono test corpus when it is not already available.</summary>
    public static async Task EnsureDownloadedAsync(
        string repositoryRoot,
        string testsDirectory,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        if (ConformanceRunner.Discover(testsDirectory).Count > 0)
            return;

        var expectedDirectory = Path.Combine(repositoryRoot, "tests", "conformance", "mono-tests");
        if (!PathsEqual(testsDirectory, expectedDirectory))
            throw new DirectoryNotFoundException($"No Mono tests were found in {testsDirectory}.");

        var scriptPath = Path.Combine(repositoryRoot, "tests", "conformance", "download-mono-tests.sh");
        if (!File.Exists(scriptPath))
            throw new FileNotFoundException("The Mono test download script was not found.", scriptPath);

        await output.WriteLineAsync("Downloading the Mono compiler test corpus...");
        var startInfo = new ProcessStartInfo("bash")
        {
            WorkingDirectory = repositoryRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add(scriptPath);

        using var process = Process.Start(startInfo) ??
                            throw new InvalidOperationException("Could not start the Mono test download script.");
        var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var outputText = await standardOutput;
        var errorText = await standardError;
        if (!string.IsNullOrWhiteSpace(outputText))
            await output.WriteAsync(outputText);
        if (!string.IsNullOrWhiteSpace(errorText))
            await error.WriteAsync(errorText);
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"Mono test download failed with exit code {process.ExitCode}.");
        if (ConformanceRunner.Discover(testsDirectory).Count == 0)
            throw new InvalidOperationException("The Mono test download completed without producing test files.");
    }

    private static bool PathsEqual(string left, string right) => string.Equals(
        Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
        Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
