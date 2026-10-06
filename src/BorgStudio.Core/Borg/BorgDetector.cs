using BorgStudio.Core.Processes;

namespace BorgStudio.Core.Borg;

/// <summary>
/// Finds the installed borg: native executables first (see <see cref="BorgLocator"/>),
/// on Windows then borg inside WSL.
/// </summary>
public sealed class BorgDetector(IProcessRunner processRunner, BorgSearchEnvironment environment)
{
    private static readonly TimeSpan NativeTimeout = TimeSpan.FromSeconds(10);

    // The first WSL call may have to start the WSL VM.
    private static readonly TimeSpan WslTimeout = TimeSpan.FromSeconds(30);

    // Login shell, so a borg installed with pipx (~/.local/bin) is found as well.
    private static readonly string[] WslArguments = ["-e", "sh", "-lc", "command -v borg && borg --version"];

    public static BorgDetector CreateDefault() => new(new ProcessRunner(), BorgSearchEnvironment.Current());

    /// <summary>The first working borg, or <c>null</c> if there is none.</summary>
    public async Task<BorgInstallation?> DetectAsync(CancellationToken cancellationToken = default)
    {
        foreach (var executable in BorgLocator.FindExecutables(environment))
        {
            var output = await TryRunAsync(executable, ["--version"], NativeTimeout, cancellationToken);
            if (BorgVersion.TryParse(output, out var version))
                return new BorgInstallation(executable, version, BorgRuntime.Native);
        }

        return BorgLocator.FindWsl(environment) is { } wsl
            ? await DetectInWslAsync(wsl, cancellationToken)
            : null;
    }

    private async Task<BorgInstallation?> DetectInWslAsync(string wsl, CancellationToken cancellationToken)
    {
        // Expected output: "/usr/bin/borg" followed by "borg 1.4.5".
        var output = await TryRunAsync(wsl, WslArguments, WslTimeout, cancellationToken);
        var lines = output?.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return lines is [var path, var versionLine, ..] && BorgVersion.TryParse(versionLine, out var version)
            ? new BorgInstallation(path, version, BorgRuntime.Wsl)
            : null;
    }

    /// <summary>Standard output of a successful run, <c>null</c> if the program is missing, fails or hangs.</summary>
    private async Task<string?> TryRunAsync(
        string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            var result = await processRunner.RunAsync(fileName, arguments, timeout, cancellationToken);
            return result is { ExitCode: 0 } ? result.StandardOutput : null;
        }
        catch (TimeoutException)
        {
            return null;
        }
    }
}
