using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace BorgStudio.Core.Processes;

public sealed class ProcessRunner : IProcessRunner
{
    public async Task<ProcessResult?> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        IReadOnlyDictionary<string, string?>? environment = null,
        CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            // Closed right after the start: nothing may wait for input. Started from a GUI app (no console),
            // wsl.exe otherwise blocks until the timeout instead of failing fast.
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        foreach (var (name, value) in environment ?? new Dictionary<string, string?>())
        {
            if (value is null)
                startInfo.Environment.Remove(name);
            else
                startInfo.Environment[name] = value;
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Win32Exception)
        {
            return null;
        }
        process.StandardInput.Close();

        var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Exited on its own in the meantime.
            }
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException($"'{fileName}' did not exit within {timeout.TotalSeconds:0.#} s.");
        }

        return new ProcessResult(process.ExitCode, await standardOutput, await standardError);
    }
}
