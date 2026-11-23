using System.Diagnostics;

namespace BorgStudio.Providers.NetworkDisk;

/// <summary>Runs the programs of the operating system that log in to network disks.</summary>
internal static class Commands
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    public sealed record Result(int ExitCode, string Output, string Error);

    /// <summary>The program's result; <c>null</c> if it could not start (e.g. not installed).</summary>
    public static async Task<Result?> RunAsync(string fileName, IReadOnlyList<string> arguments, string? input = null,
        CancellationToken cancellationToken = default)
    {
        var start = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = input is not null,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        try
        {
            using var process = Process.Start(start);
            if (process is null)
                return null;
            if (input is not null)
            {
                await process.StandardInput.WriteAsync(input);
                process.StandardInput.Close();
            }
            var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var error = process.StandardError.ReadToEndAsync(cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(Timeout);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                throw;
            }
            return new Result(process.ExitCode, await output, await error);
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }
}
