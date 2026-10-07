namespace BorgStudio.Core.Processes;

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

/// <summary>Starts external programs (borg, wsl.exe); abstracted so callers can be tested without them.</summary>
public interface IProcessRunner
{
    /// <summary>
    /// Runs <paramref name="fileName"/> and waits for it to exit.
    /// Returns <c>null</c> if the program cannot be started at all (e.g. it does not exist).
    /// </summary>
    /// <param name="environment">
    /// Additional environment variables (e.g. secrets that must not appear on the command line);
    /// a <c>null</c> value removes the variable.
    /// </param>
    /// <param name="standardInput">Written to the program's standard input (UTF-8), which is then closed.</param>
    /// <exception cref="TimeoutException">The program did not exit within <paramref name="timeout"/>; it has been killed.</exception>
    Task<ProcessResult?> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        IReadOnlyDictionary<string, string?>? environment = null,
        string? standardInput = null,
        CancellationToken cancellationToken = default);
}
