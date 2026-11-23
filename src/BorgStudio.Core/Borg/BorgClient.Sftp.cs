using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using BorgStudio.Core.Processes;
using BorgStudio.Plugins;

namespace BorgStudio.Core.Borg;

public sealed partial class BorgClient
{
    /// <summary>How long an SFTP folder stays mounted after borg is done with it (the next call reuses it).</summary>
    public TimeSpan SftpLinger { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>How long sshfs may take to log in and mount.</summary>
    public TimeSpan SftpMountTimeout { get; init; } = TimeSpan.FromSeconds(45);

    /// <summary>Printed by the mount script when sshfs is not installed where borg runs.</summary>
    internal const string SshfsMissingMarker = "BORGSTUDIO_SSHFS_MISSING";

    /// <summary>Printed (exit code 97) when borg was about to run on an SFTP folder that is not mounted.</summary>
    internal const string NotMountedMarker = "BORGSTUDIO_NOT_MOUNTED";

    private readonly SemaphoreSlim _sftpGate = new(1, 1);
    private readonly Dictionary<string, SftpMount> _sftpMounts = [];

    private sealed class SftpMount(string mountPoint, BorgInstallation borg, Task<ProcessResult?>? sshfs, CancellationTokenSource? kill)
    {
        /// <summary>Where the folder is mounted, as borg sees it.</summary>
        public string MountPoint { get; } = mountPoint;

        public BorgInstallation Borg { get; } = borg;

        /// <summary>sshfs in the foreground (it ends when the folder is unmounted); <c>null</c> for a mount found in place.</summary>
        public Task<ProcessResult?>? Sshfs { get; } = sshfs;

        public CancellationTokenSource? Kill { get; } = kill;

        public int Users { get; set; }

        /// <summary>Unmounts after <see cref="SftpLinger"/> unless it is used again.</summary>
        public CancellationTokenSource? Linger { get; set; }
    }

    /// <summary>
    /// Mounts the folder holding the repository with sshfs (where borg runs: inside WSL or natively) – or reuses the mount –
    /// and returns the repository as a local path plus the mount point borg must check before it starts. The parent folder
    /// is mounted, so a repository that <c>borg init</c> is about to create needs no folder yet. sshfs runs in the
    /// foreground, held by BorgStudio: started in the background inside WSL, it would end with the call that started it.
    /// Release the use when borg is done; the mount then stays for <see cref="SftpLinger"/>.
    /// </summary>
    private async Task<(string? Repository, string? MountPoint, BorgError? Error, IAsyncDisposable? Use)> UseSftpMountAsync(
        BorgInstallation borg, SshEndpoint endpoint, SshFiles files, CancellationToken cancellationToken)
    {
        var path = endpoint.RepositoryPath.Trim('/');
        var slash = path.LastIndexOf('/');
        var parent = slash < 0 ? "" : path[..slash];
        var leaf = slash < 0 ? path : path[(slash + 1)..];
        if (leaf.Length == 0)
            return (null, null, new BorgError(BorgErrorKind.UnsupportedPath, endpoint.RepositoryPath), null);
        // Absolute paths stay absolute; others are relative to the login's home folder.
        var remote = endpoint.RepositoryPath.StartsWith('/') ? "/" + parent : parent;
        var key = $"{endpoint.User}@{endpoint.Host}:{endpoint.Port}:{remote}";

        await _sftpGate.WaitAsync(cancellationToken);
        try
        {
            if (!_sftpMounts.TryGetValue(key, out var mount) || mount.Sshfs is { IsCompleted: true })
            {
                var (mounted, error) = await MountSftpAsync(borg, endpoint, files, remote, key, cancellationToken);
                if (error is not null)
                    return (null, null, error, null);
                mount = mounted!;
                _sftpMounts[key] = mount;
            }
            mount.Linger?.Cancel();
            mount.Linger = null;
            mount.Users++;
            return ($"{mount.MountPoint}/{leaf}", mount.MountPoint, null, new SftpUse(this, key));
        }
        finally
        {
            _sftpGate.Release();
        }
    }

    private async Task<(SftpMount? Mount, BorgError? Error)> MountSftpAsync(BorgInstallation borg, SshEndpoint endpoint, SshFiles files,
        string remote, string key, CancellationToken cancellationToken)
    {
        var name = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16].ToLowerInvariant();
        var prepared = await ShellAsync(borg, PrepareSftpScript, [name], cancellationToken);
        var lines = prepared?.StandardOutput.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries) ?? [];
        if (prepared is null)
            return (null, new BorgError(BorgErrorKind.NotStartable, "sshfs"));
        if (lines.Contains(SshfsMissingMarker))
            return (null, new BorgError(BorgErrorKind.SshfsMissing, "sshfs"));
        if (prepared.ExitCode != 0 || lines.Length == 0 || lines[^1].Split(' ', 2) is not [var state, var mountPoint])
            return (null, new BorgError(BorgErrorKind.Other, prepared.StandardError.Trim()));
        // Still mounted from an earlier run and working: used as it is.
        if (state == "MOUNTED")
            return (new SftpMount(mountPoint, borg, null, null), null);

        List<string> sshfs =
        [
            "sshfs", "-f", "-p", endpoint.Port.ToString(CultureInfo.InvariantCulture),
            "-o", $"IdentityFile={files.PrivateKeyFile}", "-o", $"UserKnownHostsFile={files.KnownHostsFile}",
            "-o", "StrictHostKeyChecking=yes", "-o", "BatchMode=yes", "-o", "IdentitiesOnly=yes",
            "-o", "ServerAliveInterval=15", "-o", "ServerAliveCountMax=8",
            $"{endpoint.User}@{endpoint.Host}:{remote}", mountPoint,
        ];
        var (fileName, arguments) = borg.Runtime == BorgRuntime.Wsl ? (wslExecutable!, (IReadOnlyList<string>)["-e", .. sshfs]) : ("sshfs", sshfs[1..]);
        var kill = new CancellationTokenSource();
        var run = Task.Run(async () =>
        {
            try
            {
                return await processRunner.RunAsync(fileName, arguments, Timeout.InfiniteTimeSpan, cancellationToken: kill.Token);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
        }, CancellationToken.None);

        // Ready once the folder is a mount point; sshfs ending before that is the error.
        var started = DateTime.UtcNow;
        while (true)
        {
            if (run.IsCompleted)
                return (null, SshfsError((await run)?.StandardError ?? ""));
            if (await ShellAsync(borg, IsMountedFunction + "is_mounted \"$1\"", [mountPoint]) is { ExitCode: 0 })
                return (new SftpMount(mountPoint, borg, run, kill), null);
            if (cancellationToken.IsCancellationRequested || DateTime.UtcNow - started > SftpMountTimeout)
            {
                await kill.CancelAsync();
                return (null, new BorgError(cancellationToken.IsCancellationRequested ? BorgErrorKind.Cancelled : BorgErrorKind.Timeout));
            }
            await Task.WhenAny(run, Task.Delay(MountPollInterval, CancellationToken.None));
        }
    }

    /// <summary>What sshfs said when it could not mount.</summary>
    private static BorgError SshfsError(string output)
    {
        var text = output.Trim();
        var kind = text.Contains("Host key verification failed", StringComparison.OrdinalIgnoreCase)
            || text.Contains("REMOTE HOST IDENTIFICATION HAS CHANGED", StringComparison.OrdinalIgnoreCase) ? BorgErrorKind.SshHostKeyFailed
            : text.Contains("Permission denied", StringComparison.OrdinalIgnoreCase) ? BorgErrorKind.SshAuthenticationFailed
            : BorgErrorKind.ConnectionFailed;
        return new BorgError(kind, text);
    }

    private async Task ReleaseSftpMountAsync(string key)
    {
        await _sftpGate.WaitAsync();
        try
        {
            if (!_sftpMounts.TryGetValue(key, out var mount) || --mount.Users > 0)
                return;
            var linger = new CancellationTokenSource();
            mount.Linger = linger;
            _ = UnmountLaterAsync(key, mount, linger.Token);
        }
        finally
        {
            _sftpGate.Release();
        }
    }

    private async Task UnmountLaterAsync(string key, SftpMount mount, CancellationToken stillUsed)
    {
        try
        {
            await Task.Delay(SftpLinger, stillUsed);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        await UnmountSftpAsync(key, mount, retry: true);
    }

    /// <summary>Unmounts unless borg still works there (e.g. an archive mounted from it): then it is tried again later.</summary>
    private async Task UnmountSftpAsync(string key, SftpMount mount, bool retry)
    {
        await _sftpGate.WaitAsync();
        try
        {
            if (mount.Users > 0 || !_sftpMounts.TryGetValue(key, out var current) || current != mount)
                return;
            if (mount.Sshfs is { IsCompleted: true } || await ShellAsync(mount.Borg, UnmountSftpScript, [mount.MountPoint]) is { ExitCode: 0 })
            {
                _sftpMounts.Remove(key);
                // sshfs ends with the mount; stopped only if it does not.
                if (mount.Sshfs is { } sshfs && await Task.WhenAny(sshfs, Task.Delay(UnmountWait)) != sshfs)
                    await mount.Kill!.CancelAsync();
                return;
            }
            if (retry)
            {
                var linger = new CancellationTokenSource();
                mount.Linger = linger;
                _ = UnmountLaterAsync(key, mount, linger.Token);
            }
        }
        finally
        {
            _sftpGate.Release();
        }
    }

    /// <summary>Unmounts every SFTP folder no borg call uses now (when the app or a scheduled run ends).</summary>
    public async Task CloseSftpMountsAsync()
    {
        List<(string Key, SftpMount Mount)> mounts;
        await _sftpGate.WaitAsync();
        try
        {
            mounts = _sftpMounts.Select(pair => (pair.Key, pair.Value)).ToList();
            foreach (var (_, mount) in mounts)
                mount.Linger?.Cancel();
        }
        finally
        {
            _sftpGate.Release();
        }
        foreach (var (key, mount) in mounts)
            await UnmountSftpAsync(key, mount, retry: false);
    }

    /// <summary>
    /// $1 name of the mount point. Prints "MOUNTED &lt;path&gt;" for a working mount (also one left by an earlier run),
    /// otherwise detaches a broken one and prints "READY &lt;path&gt;" – or the marker if sshfs is missing.
    /// </summary>
    internal const string PrepareSftpScript = IsMountedFunction + ForceUnmountFunction
        + "m=\"$HOME/.borgstudio/sftp/$1\"; mkdir -p \"$m\" || exit 1; "
        + "if is_mounted \"$m\"; then if ls \"$m\" >/dev/null 2>&1; then echo \"MOUNTED $m\"; exit 0; fi; force_unmount \"$m\"; fi; "
        + "command -v sshfs >/dev/null 2>&1 || { echo " + SshfsMissingMarker + "; exit 3; }; "
        + "echo \"READY $m\"";

    /// <summary>$1 the mount point.</summary>
    internal const string UnmountSftpScript =
        "fusermount -u \"$1\" 2>/dev/null || fusermount3 -u \"$1\" 2>/dev/null || umount \"$1\"";

    /// <summary>
    /// Runs before borg ($0 the mount point, then borg's command line): borg only starts on a mounted SFTP folder – never
    /// on the empty local folder beneath it.
    /// </summary>
    internal const string MountGuard = IsMountedFunction
        + "is_mounted \"$0\" || { echo " + NotMountedMarker + " >&2; exit 97; }; exec \"$@\"";

    private sealed class SftpUse(BorgClient client, string key) : IAsyncDisposable
    {
        private int _released;

        public ValueTask DisposeAsync() =>
            Interlocked.Exchange(ref _released, 1) == 0 ? new ValueTask(client.ReleaseSftpMountAsync(key)) : ValueTask.CompletedTask;
    }
}
