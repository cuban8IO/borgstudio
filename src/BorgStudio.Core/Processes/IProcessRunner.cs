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
    /// <param name="standardErrorLine">
    /// Called with every line on standard error as soon as it arrives (e.g. borg's progress); the lines are still part
    /// of <see cref="ProcessResult.StandardError"/>.
    /// </param>
    /// <param name="standardOutputLine">
    /// Called with every line on standard output as it arrives; the lines are then NOT kept in
    /// <see cref="ProcessResult.StandardOutput"/> (for large outputs, e.g. the contents of an archive).
    /// </param>
    /// <exception cref="TimeoutException">The program did not exit within <paramref name="timeout"/>; it has been killed.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled; the program has been killed.</exception>
    Task<ProcessResult?> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        IReadOnlyDictionary<string, string?>? environment = null,
        string? standardInput = null,
        CancellationToken cancellationToken = default,
        Action<string>? standardErrorLine = null,
        Action<string>? standardOutputLine = null);
}
