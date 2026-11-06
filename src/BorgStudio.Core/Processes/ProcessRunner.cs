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
        string? standardInput = null,
        CancellationToken cancellationToken = default,
        Action<string>? standardErrorLine = null,
        Action<string>? standardOutputLine = null)
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
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
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
        if (standardInput is not null)
            await process.StandardInput.WriteAsync(standardInput.AsMemory(), cancellationToken);
        process.StandardInput.Close();

        var standardOutput = standardOutputLine is null
            ? process.StandardOutput.ReadToEndAsync(cancellationToken)
            : ReadLinesAsync(process.StandardOutput, standardOutputLine, cancellationToken, keep: false);
        var standardError = standardErrorLine is null
            ? process.StandardError.ReadToEndAsync(cancellationToken)
            : ReadLinesAsync(process.StandardError, standardErrorLine, cancellationToken);

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

    /// <summary>Reads to the end, passing every line on as it arrives; returns everything read.</summary>
    private static async Task<string> ReadLinesAsync(StreamReader reader, Action<string> line, CancellationToken cancellationToken,
        bool keep = true)
    {
        var all = new StringBuilder();
        while (await reader.ReadLineAsync(cancellationToken) is { } next)
        {
            all.Append(next).Append('\n');
            line(next);
        }
        return all.ToString();
    }
}
