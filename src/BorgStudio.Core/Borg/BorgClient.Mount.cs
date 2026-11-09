using BorgStudio.Core.Processes;
using BorgStudio.Plugins;

namespace BorgStudio.Core.Borg;

/// <summary>Whether archives can be mounted so that the file manager can open them.</summary>
public enum MountSetup
{
    Ready,

    /// <summary>
    /// borg runs inside WSL, where Windows reaches a mount only with <c>-o allow_other</c> – which FUSE allows only with
    /// <c>user_allow_other</c> in /etc/fuse.conf (see <see cref="BorgClient.EnableAllowOtherAsync"/>).
    /// </summary>
    AllowOtherMissing,

    /// <summary>borg runs natively on Windows: there is no FUSE.</summary>
    Unsupported,
}

/// <summary>A running <c>borg mount</c>: the archive (or repository) is readable below <see cref="Path"/> until unmounted.</summary>
public sealed class BorgMount
{
    private readonly Func<bool, Task<bool>> _unmount;

    internal BorgMount(string path, string? windowsPath, Task<BorgError?> ended, Func<bool, Task<bool>> unmount)
    {
        Path = path;
        WindowsPath = windowsPath;
        Ended = ended;
        _unmount = unmount;
    }

    /// <summary>Where it is mounted, as borg sees it (inside WSL when borg runs there).</summary>
    public string Path { get; }

    /// <summary>The same folder for Windows programs (\\wsl.localhost\…), when borg runs inside WSL.</summary>
    public string? WindowsPath { get; }

    /// <summary>What the file manager opens.</summary>
    public string OpenPath => WindowsPath ?? Path;

    /// <summary>Completes when borg has stopped: <c>null</c> after a normal unmount, otherwise why it stopped.</summary>
    public Task<BorgError?> Ended { get; }

    /// <summary>
    /// Unmounts and waits for borg to stop; <c>false</c> if the folder is still in use (a program has a file open).
    /// <paramref name="force"/> detaches it anyway (lazily: it goes away once no longer in use) – for closing the app.
    /// </summary>
    public Task<bool> UnmountAsync(bool force = false) => _unmount(force);
}

/// <summary>
/// <c>borg mount</c>: an archive or a whole repository as a read-only folder, below ~/borgstudio-mounts – on Windows
/// inside WSL, where Explorer reaches it through \\wsl.localhost. borg runs in the foreground (<c>-f</c>) for as long as
/// it is mounted; it stops when the folder is unmounted (not on SIGINT). While mounted, borg holds the repository lock:
/// reading works, creating archives does not.
/// </summary>
public sealed partial class BorgClient
{
    /// <summary>The folder (in the home folder) all mounts go to.</summary>
    public const string MountRoot = "borgstudio-mounts";

    private static readonly TimeSpan MountPollInterval = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan UnmountWait = TimeSpan.FromSeconds(15);

    /// <summary>The line that enables <c>allow_other</c> for normal users.</summary>
    public const string AllowOtherLine = "user_allow_other";

    /// <summary>What the user can run in WSL instead of letting the app do it.</summary>
    public const string AllowOtherCommand = "echo " + AllowOtherLine + " | sudo tee -a /etc/fuse.conf";

    private const string HasAllowOther = "grep -Eq '^[[:space:]]*" + AllowOtherLine + "' /etc/fuse.conf 2>/dev/null";

    /// <summary>Removes the empty mount folder and the repository's folder above it (if nothing else is mounted there).</summary>
    private const string RemoveFolders = "rmdir -- \"$1\" 2>/dev/null; rmdir -- \"$(dirname -- \"$1\")\" 2>/dev/null; true";

    /// <summary>Whether <paramref name="path"/> is a mount point (no <c>mountpoint</c> on macOS).</summary>
    private const string IsMountedFunction =
        "is_mounted() { if command -v mountpoint >/dev/null 2>&1; then mountpoint -q \"$1\"; "
        + "else mount | grep -qF \" on $1 (\"; fi; }; ";

    /// <summary>Detaches a mount at once, even if busy or left behind by a borg that is gone.</summary>
    private const string ForceUnmountFunction =
        "force_unmount() { fusermount -u -z \"$1\" 2>/dev/null || fusermount3 -u -z \"$1\" 2>/dev/null "
        + "|| umount -l \"$1\" 2>/dev/null || umount -f \"$1\" 2>/dev/null; }; ";

    /// <summary>Whether a native borg runs on a system with FUSE (not Windows); tests pretend so everywhere.</summary>
    internal bool NativeIsPosix { get; init; } = !OperatingSystem.IsWindows();

    /// <summary>Whether mounting is possible as borg is installed.</summary>
    public async Task<MountSetup> CheckMountSetupAsync(BorgInstallation borg, CancellationToken cancellationToken = default)
    {
        if (borg.Runtime == BorgRuntime.Native)
            return NativeIsPosix ? MountSetup.Ready : MountSetup.Unsupported;

        var result = await ShellAsync(borg, HasAllowOther, [], cancellationToken);
        return result is { ExitCode: 0 } ? MountSetup.Ready : MountSetup.AllowOtherMissing;
    }

    /// <summary>
    /// Adds <c>user_allow_other</c> to /etc/fuse.conf in WSL, as root (<c>wsl -u root</c> needs no password); <c>false</c>
    /// if that failed.
    /// </summary>
    public async Task<bool> EnableAllowOtherAsync(CancellationToken cancellationToken = default)
    {
        if (wslExecutable is null)
            return false;
        try
        {
            var result = await processRunner.RunAsync(wslExecutable,
                ["-u", "root", "-e", "sh", "-c", $"{HasAllowOther} || echo {AllowOtherLine} >> /etc/fuse.conf"], CopyTimeout,
                cancellationToken: cancellationToken);
            return result is { ExitCode: 0 };
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    /// <summary>
    /// Mounts <paramref name="archive"/> (<c>null</c>: the whole repository, one folder per archive) at
    /// ~/borgstudio-mounts/<paramref name="folder"/> and returns once it is readable there. Cancelling stops borg.
    /// </summary>
    /// <param name="folder">Below <see cref="MountRoot"/>, '/'-separated; see <see cref="MountFolder"/>.</param>
    public async Task<BorgResult<BorgMount>> MountAsync(
        BorgInstallation borg, RepositoryLocation location, string? archive, string folder, string? passphrase,
        SshAccess? ssh = null, CancellationToken cancellationToken = default)
    {
        var wsl = borg.Runtime == BorgRuntime.Wsl;
        if (!wsl && !NativeIsPosix)
            return BorgResult<BorgMount>.Failure(new BorgError(BorgErrorKind.MountUnavailable));

        // The folder, created fresh (a mount left behind by a crash is detached first); its owner for the files.
        const string prepare = ForceUnmountFunction + IsMountedFunction
            + "p=\"$HOME/$1\"; if is_mounted \"$p\" || ! ls \"$p\" >/dev/null 2>&1; then force_unmount \"$p\"; fi; "
            + "mkdir -p -- \"$p\" && printf '%s\\n' \"$p\" && id -u && id -g "
            + "&& { command -v wslpath >/dev/null 2>&1 && wslpath -w \"$p\" || true; }";
        var prepared = await ShellAsync(borg, prepare, [MountRoot + "/" + folder], cancellationToken);
        if (cancellationToken.IsCancellationRequested)
            return BorgResult<BorgMount>.Failure(new BorgError(BorgErrorKind.Cancelled));
        var lines = prepared?.StandardOutput.Split('\n', StringSplitOptions.TrimEntries) ?? [];
        if (prepared is not { ExitCode: 0 } || lines.Length < 3)
            return BorgResult<BorgMount>.Failure(new BorgError(BorgErrorKind.Other, prepared?.StandardError.Trim()));
        var path = lines[0];
        var windowsPath = wsl && lines.Length > 3 && lines[3].Length > 0 ? lines[3] : null;

        // Every file belongs to the user (archives from other systems may hold files of other users), and in WSL,
        // Windows may read it (it reaches WSL as another user).
        var options = $"uid={lines[1]},gid={lines[2]}";
        if (wsl)
            options = "allow_other," + options;

        // Only for a last resort: killing borg leaves the mount behind (detached when mounting there again).
        var kill = new CancellationTokenSource();
        var run = Task.Run(() => RunAsync(borg, location, ssh, ["mount"], ["-f", "-o", options], [path], passphrase,
            Timeout.InfiniteTimeSpan, kill.Token, archive));
        var ended = EndedAsync(run, kill);

        async Task<bool> UnmountAsync(bool force)
        {
            if (!ended.IsCompleted)
            {
                // borg's own umount: fails while a program uses the folder.
                var unmounted = await RunUnmountAsync(borg, path);
                if (!unmounted && force)
                    await ShellAsync(borg, ForceUnmountFunction + "force_unmount \"$1\"", [path]);
                if (!unmounted && !force)
                    return false;
                if (await Task.WhenAny(ended, Task.Delay(UnmountWait)) != ended)
                    await kill.CancelAsync();
                await ended;
            }
            // The empty folder (and the repository's, if no other archive is mounted there) goes.
            await ShellAsync(borg, RemoveFolders, [path]);
            return true;
        }

        // Ready once the folder is a mount point; borg stopping before that is the error.
        var check = IsMountedFunction + "is_mounted \"$1\"";
        while (true)
        {
            if (ended.IsCompleted)
            {
                await ShellAsync(borg, RemoveFolders, [path]);
                return BorgResult<BorgMount>.Failure(await ended ?? new BorgError(BorgErrorKind.Other));
            }
            if (cancellationToken.IsCancellationRequested)
            {
                await UnmountAsync(force: true);
                return BorgResult<BorgMount>.Failure(new BorgError(BorgErrorKind.Cancelled));
            }
            if (await ShellAsync(borg, check, [path]) is { ExitCode: 0 })
                return BorgResult<BorgMount>.Success(new BorgMount(path, windowsPath, ended, UnmountAsync));

            try
            {
                await Task.WhenAny(ended, Task.Delay(MountPollInterval, cancellationToken));
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    /// <summary>
    /// Detaches all mounts below ~/borgstudio-mounts and removes the empty folders – after a crash, borg is gone but
    /// its mounts stay (and Explorer shows broken folders). Only call this when the app itself has nothing mounted.
    /// </summary>
    public async Task CleanUpMountsAsync(BorgInstallation borg, CancellationToken cancellationToken = default)
    {
        if (borg.Runtime == BorgRuntime.Native && !NativeIsPosix)
            return;

        // /proc/mounts escapes blanks as \040 (printf %b turns them back); macOS has none, its mounts end with borg.
        const string cleanUp = ForceUnmountFunction
            + "d=\"$HOME/$1\"; [ -d \"$d\" ] || exit 0; "
            + "if [ -r /proc/mounts ]; then while read -r _ m _; do m=$(printf '%b' \"$m\"); "
            + "case \"$m\" in \"$d\"/*) force_unmount \"$m\";; esac; done < /proc/mounts; fi; "
            + "find \"$d\" -mindepth 1 -depth -type d -empty -delete 2>/dev/null; true";
        await ShellAsync(borg, cleanUp, [MountRoot], cancellationToken);
    }

    /// <summary>
    /// The folder below <see cref="MountRoot"/> for an archive (<c>null</c>: the whole repository, in
    /// <paramref name="allArchivesName"/>): "&lt;repository&gt;/&lt;archive&gt;", with characters Windows can't show replaced.
    /// </summary>
    public static string MountFolder(string repositoryName, string? archive, string allArchivesName) =>
        SafeName(repositoryName) + "/" + SafeName(archive ?? allArchivesName);

    private static string SafeName(string name)
    {
        var safe = new string([.. name.Select(character =>
            character < ' ' || "<>:\"/\\|?*".Contains(character, StringComparison.Ordinal) ? '_' : character)]).Trim().TrimEnd('.');
        return safe.Length == 0 || safe.StartsWith('.') ? "_" + safe : safe;
    }

    private static async Task<BorgError?> EndedAsync(Task<BorgResult<string>> run, CancellationTokenSource kill)
    {
        try
        {
            var result = await run;
            return result.Error is { Kind: BorgErrorKind.Cancelled } ? null : result.Error;
        }
        finally
        {
            kill.Dispose();
        }
    }

    private async Task<bool> RunUnmountAsync(BorgInstallation borg, string path)
    {
        List<string> arguments = ["umount", path];
        string fileName = borg.Path;
        if (borg.Runtime == BorgRuntime.Wsl)
        {
            fileName = wslExecutable!;
            arguments.InsertRange(0, ["-e", borg.Path]);
        }
        try
        {
            return await processRunner.RunAsync(fileName, arguments, CopyTimeout) is { ExitCode: 0 };
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    /// <summary>
    /// Runs a POSIX shell <paramref name="script"/> where borg runs (inside WSL or natively) with
    /// <paramref name="arguments"/> as $1…; <c>null</c> if it could not run.
    /// </summary>
    private async Task<ProcessResult?> ShellAsync(BorgInstallation borg, string script, IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default)
    {
        List<string> command = ["-c", script, "sh", .. arguments];
        var fileName = "sh";
        if (borg.Runtime == BorgRuntime.Wsl)
        {
            if (wslExecutable is null)
                return null;
            fileName = wslExecutable;
            command.InsertRange(0, ["-e", "sh"]);
        }
        try
        {
            return await processRunner.RunAsync(fileName, command, CopyTimeout, cancellationToken: cancellationToken);
        }
        catch (TimeoutException)
        {
            return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }
}
