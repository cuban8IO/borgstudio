using System.Globalization;
using BorgStudio.Plugins;
using BorgStudio.Providers.NetworkDisk.Resources;

namespace BorgStudio.Providers.NetworkDisk;

/// <summary>
/// Linux: iSCSI with open-iscsi (<c>iscsiadm</c>), NVMe/TCP with nvme-cli. Setting up (with pkexec, asked once) adds a
/// sudoers rule that allows exactly this target's login and logout without a password; the volumes are then mounted with
/// udisks (<c>udisksctl</c>), as the desktop does for USB drives.
/// </summary>
public sealed class LinuxNetworkDisks : INetworkDiskSystem
{
    private static readonly TimeSpan DeviceWait = TimeSpan.FromSeconds(30);

    /// <summary>The block devices mounted for a target, to unmount before logging out.</summary>
    private readonly Dictionary<string, List<string>> _mounted = [];

    public async Task SetUpAsync(NetworkDiskTarget target, string id, string? chapSecret, CancellationToken cancellationToken)
    {
        // Without carriage returns, whatever line endings the source was built with.
        var result = await Commands.RunAsync("pkexec", ["/bin/sh", "-c", SetupScript.ReplaceLineEndings("\n"), "sh", Environment.UserName, id,
                target.Protocol == NetworkDiskProtocol.Iscsi ? "iscsi" : "nvme-tcp", target.Host, target.Port.ToString(CultureInfo.InvariantCulture),
                target.Name, target.ChapUser ?? ""],
            input: (chapSecret ?? "") + "\n", cancellationToken: cancellationToken);
        if (result is null)
            throw new RepositoryUnavailableException(Strings.PkexecMissing);
        if (result.ExitCode is 126 or 127 && result.Error.Length == 0)
            throw new RepositoryUnavailableException(Strings.SetupDeclined);
        if (result.ExitCode != 0)
            throw new RepositoryUnavailableException(string.Format(CultureInfo.CurrentCulture, Strings.SetupFailedBecause, result.Error.Trim()));
    }

    public async Task ConnectAsync(NetworkDiskTarget target, string id, CancellationToken cancellationToken)
    {
        var before = await BlockDevicesAsync(cancellationToken);
        var login = await Commands.RunAsync("sudo", ["-n", .. LoginCommand(target)], cancellationToken: cancellationToken);
        // 15: iscsiadm's "already logged in".
        if (login is null || (login.ExitCode != 0 && !(target.Protocol == NetworkDiskProtocol.Iscsi && login.ExitCode == 15)))
            throw new RepositoryUnavailableException(login?.Error.Contains("password", StringComparison.OrdinalIgnoreCase) == true
                ? Strings.NotSetUp
                : string.Format(CultureInfo.CurrentCulture, Strings.ConnectFailed, login?.Error.Trim() ?? "sudo"));

        // Its volumes: new block devices with a file system, mounted like the desktop mounts USB drives.
        var mounted = new List<string>();
        var started = DateTime.UtcNow;
        while (mounted.Count == 0 && DateTime.UtcNow - started < DeviceWait)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            foreach (var (device, _) in (await BlockDevicesAsync(cancellationToken)).Where(entry => !before.ContainsKey(entry.Key) && entry.Value.FileSystem && !entry.Value.Mounted))
                if (await Commands.RunAsync("udisksctl", ["mount", "--no-user-interaction", "-b", device], cancellationToken: cancellationToken) is { ExitCode: 0 })
                    mounted.Add(device);
        }
        _mounted[id] = mounted;
    }

    public async Task<string?> DisconnectAsync(NetworkDiskTarget target, string id, CancellationToken cancellationToken)
    {
        foreach (var device in _mounted.GetValueOrDefault(id) ?? [])
            if (await Commands.RunAsync("udisksctl", ["unmount", "--no-user-interaction", "-b", device], cancellationToken: cancellationToken) is not { ExitCode: 0 })
                return string.Format(CultureInfo.CurrentCulture, Strings.DisconnectFailed, target.Name, device);
        _mounted.Remove(id);
        var logout = await Commands.RunAsync("sudo", ["-n", .. LogoutCommand(target)], cancellationToken: cancellationToken);
        return logout is { ExitCode: 0 } ? null : string.Format(CultureInfo.CurrentCulture, Strings.DisconnectFailed, target.Name, logout?.Error.Trim() ?? "sudo");
    }

    /// <summary>Exactly as allowed in the sudoers rule.</summary>
    public static IReadOnlyList<string> LoginCommand(NetworkDiskTarget target) => target.Protocol == NetworkDiskProtocol.Iscsi
        ? ["iscsiadm", "-m", "node", "-T", target.Name, "-p", $"{target.Host}:{target.Port}", "--login"]
        : ["nvme", "connect", "-t", "tcp", "-a", target.Host, "-s", target.Port.ToString(CultureInfo.InvariantCulture), "-n", target.Name];

    public static IReadOnlyList<string> LogoutCommand(NetworkDiskTarget target) => target.Protocol == NetworkDiskProtocol.Iscsi
        ? ["iscsiadm", "-m", "node", "-T", target.Name, "-p", $"{target.Host}:{target.Port}", "--logout"]
        : ["nvme", "disconnect", "-n", target.Name];

    /// <summary>name → (has a file system, is mounted), from lsblk.</summary>
    private static async Task<Dictionary<string, (bool FileSystem, bool Mounted)>> BlockDevicesAsync(CancellationToken cancellationToken)
    {
        var devices = new Dictionary<string, (bool, bool)>();
        if (await Commands.RunAsync("lsblk", ["-nrpo", "NAME,FSTYPE,MOUNTPOINT"], cancellationToken: cancellationToken) is not { ExitCode: 0 } result)
            return devices;
        foreach (var line in result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var columns = line.Split(' ');
            devices[columns[0]] = (columns.Length > 1 && columns[1].Length > 0, columns.Length > 2 && columns[2].Length > 0);
        }
        return devices;
    }

    /// <summary>
    /// Runs as root (pkexec). $1 user, $2 id, $3 iscsi|nvme-tcp, $4 host, $5 port, $6 IQN/NQN, $7 CHAP user; the CHAP secret
    /// on standard input. Writes /etc/sudoers.d/borgstudio-&lt;id&gt; (checked with visudo) for exactly this login/logout.
    /// </summary>
    public const string SetupScript =
        """
        set -e
        user="$1"; id="$2"; proto="$3"; host="$4"; port="$5"; name="$6"; chapuser="$7"
        IFS= read -r chapsecret || true
        esc() { printf '%s' "$1" | sed 's/[,:=\\]/\\&/g'; }
        if [ "$proto" = iscsi ]; then
          a=$(command -v iscsiadm) || { echo "iscsiadm is missing (package open-iscsi)" >&2; exit 4; }
          "$a" -m discovery -t sendtargets -p "$host:$port" >/dev/null
          if [ -n "$chapuser" ]; then
            "$a" -m node -T "$name" -p "$host:$port" -o update -n node.session.auth.authmethod -v CHAP
            "$a" -m node -T "$name" -p "$host:$port" -o update -n node.session.auth.username -v "$chapuser"
            "$a" -m node -T "$name" -p "$host:$port" -o update -n node.session.auth.password -v "$chapsecret"
          fi
          p=$(esc "$host:$port"); n=$(esc "$name")
          rule="$user ALL=(root) NOPASSWD: $a -m node -T $n -p $p --login, $a -m node -T $n -p $p --logout"
        else
          a=$(command -v nvme) || { echo "nvme is missing (package nvme-cli)" >&2; exit 4; }
          modprobe nvme-tcp 2>/dev/null || true
          n=$(esc "$name")
          rule="$user ALL=(root) NOPASSWD: $a connect -t tcp -a $host -s $port -n $n, $a disconnect -n $n"
        fi
        f="/etc/sudoers.d/borgstudio-$id"
        printf '%s\n' "$rule" > "$f.tmp"
        chmod 440 "$f.tmp"
        visudo -cf "$f.tmp" >/dev/null
        mv "$f.tmp" "$f"
        """;
}
