using System.Diagnostics;

namespace SharpSql.Cli;

/// <summary>Restores a configured .NET project.</summary>
public interface IProjectRestorer
{
    /// <summary>Runs restore for the specified project and returns the process exit code.</summary>
    Task<int> RestoreAsync(
        string projectPath,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken);
}

internal sealed class DotNetProjectRestorer : IProjectRestorer
{
    /// <inheritdoc />
    public async Task<int> RestoreAsync(
        string projectPath,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("dotnet")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            }
        };
        process.StartInfo.ArgumentList.Add("restore");
        process.StartInfo.ArgumentList.Add(projectPath);
        process.Start();

        var outputTask = CopyAsync(process.StandardOutput, output, cancellationToken);
        var errorTask = CopyAsync(process.StandardError, error, cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            throw;
        }
        await Task.WhenAll(outputTask, errorTask);
        return process.ExitCode;
    }

    private static async Task CopyAsync(
        TextReader reader,
        TextWriter writer,
        CancellationToken cancellationToken)
    {
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await writer.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
            await writer.FlushAsync(cancellationToken);
        }
    }
}
