using System.Text.RegularExpressions;

namespace BorgStudio.Core.Borg;

public sealed partial class BorgClient
{
    private readonly SemaphoreSlim _wslDrivesGate = new(1, 1);
    private readonly Dictionary<char, int> _wslDriveUsers = [];
    private readonly HashSet<char> _wslDrivesMounted = [];

    /// <summary>
    /// WSL mounts the Windows drives only when it starts: a drive attached later (a USB disk, a network drive connected
    /// afterwards) has no /mnt/&lt;letter&gt;. Before borg uses such a path, BorgStudio mounts the drive (as root, like WSL
    /// does) and unmounts it again afterwards – only the drives it mounted itself, so ejecting the drive is possible.
    /// </summary>
    /// <param name="arguments">borg's arguments, with paths already as WSL sees them ("/mnt/e/…").</param>
    /// <param name="workingDirectory">A Windows folder borg works in, if any.</param>
    private async Task<IAsyncDisposable?> UseWslDrivesAsync(IEnumerable<string> arguments, string? workingDirectory,
        CancellationToken cancellationToken)
    {
        var letters = arguments.Select(argument => WslDrive().Match(argument)).Where(match => match.Success)
            .Select(match => match.Groups["drive"].Value[0])
            .Concat(workingDirectory is not null && ToWslPath(workingDirectory) is { } translated ? [translated[5]] : [])
            .Where(letter => letter != 'c' && letter != SystemDriveLetter)
            .Distinct()
            .Order()
            .ToList();
        if (letters.Count == 0 || wslExecutable is null)
            return null;

        await _wslDrivesGate.WaitAsync(cancellationToken);
        try
        {
            var first = letters.Where(letter => _wslDriveUsers.GetValueOrDefault(letter) == 0).ToList();
            if (first.Count > 0)
            {
                var result = await processRunner.RunAsync(wslExecutable, ["-u", "root", "-e", "sh", "-c", MountDrivesScript(first)], CopyTimeout,
                    cancellationToken: cancellationToken);
                foreach (var line in (result?.StandardOutput ?? "").Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                    if (line.Length == 1 && first.Contains(line[0]))
                        _wslDrivesMounted.Add(line[0]);
            }
            foreach (var letter in letters)
                _wslDriveUsers[letter] = _wslDriveUsers.GetValueOrDefault(letter) + 1;
        }
        catch (TimeoutException)
        {
            // Not mounted: borg reports the missing folder.
            return null;
        }
        finally
        {
            _wslDrivesGate.Release();
        }
        return new WslDriveUse(this, letters);
    }

    private async Task ReleaseWslDrivesAsync(IReadOnlyList<char> letters)
    {
        await _wslDrivesGate.WaitAsync();
        try
        {
            var last = new List<char>();
            foreach (var letter in letters)
            {
                var users = _wslDriveUsers.GetValueOrDefault(letter) - 1;
                if (users > 0)
                {
                    _wslDriveUsers[letter] = users;
                    continue;
                }
                _wslDriveUsers.Remove(letter);
                if (_wslDrivesMounted.Remove(letter))
                    last.Add(letter);
            }
            if (last.Count > 0)
                await processRunner.RunAsync(wslExecutable!, ["-u", "root", "-e", "sh", "-c", UnmountDrivesScript(last)], CopyTimeout);
        }
        catch (TimeoutException)
        {
            // Stays mounted – harmless; the next use finds it mounted.
        }
        finally
        {
            _wslDrivesGate.Release();
        }
    }

    /// <summary>
    /// For each drive: nothing if /mnt/&lt;letter&gt; is a working mount; otherwise (also after the drive was pulled and
    /// attached again) mounts it like WSL's automount, owned by the default user, and prints the letter.
    /// </summary>
    internal static string MountDrivesScript(IEnumerable<char> letters)
    {
        var system = $"/mnt/{(SystemDriveLetter is { } letter ? letter : 'c')}";
        return $"U=$(stat -c %u {system}); G=$(stat -c %g {system}); for d in {string.Join(' ', letters)}; do "
            + "if mountpoint -q /mnt/$d && ls /mnt/$d >/dev/null 2>&1; then continue; fi; "
            + "umount -l /mnt/$d 2>/dev/null; mkdir -p /mnt/$d; "
            + "if mount -t drvfs \"$(echo $d | tr a-z A-Z):\" /mnt/$d -o uid=$U,gid=$G 2>/dev/null; then echo $d; else rmdir /mnt/$d 2>/dev/null; fi; "
            + "done; true";
    }

    internal static string UnmountDrivesScript(IEnumerable<char> letters) =>
        $"for d in {string.Join(' ', letters)}; do umount /mnt/$d 2>/dev/null && rmdir /mnt/$d 2>/dev/null; done; true";

    /// <summary>The Windows system drive (always mounted in WSL); <c>null</c> elsewhere.</summary>
    private static char? SystemDriveLetter =>
        OperatingSystem.IsWindows() && Path.GetPathRoot(Environment.SystemDirectory) is { Length: > 0 } root
            ? char.ToLowerInvariant(root[0])
            : null;

    [GeneratedRegex("^/mnt/(?<drive>[a-z])(/|$)")]
    private static partial Regex WslDrive();

    private sealed class WslDriveUse(BorgClient client, IReadOnlyList<char> letters) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => new(client.ReleaseWslDrivesAsync(letters));
    }
}
