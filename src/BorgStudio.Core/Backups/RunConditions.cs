using System.Globalization;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using BorgStudio.Core.Processes;
using BorgStudio.Plugins;

namespace BorgStudio.Core.Backups;

/// <summary>When a scheduled run of a job may start (runs by hand and through the webhook always start).</summary>
public sealed record RunConditions
{
    /// <summary>Not while the computer runs on its battery.</summary>
    public bool NotOnBattery { get; init; }

    /// <summary>Not over a metered connection (repositories on a server or a network share).</summary>
    public bool NotMetered { get; init; }

    /// <summary>Only over a wired connection, not Wi-Fi (repositories on a server or a network share).</summary>
    public bool WiredOnly { get; init; }

    /// <summary>Only if the server answers or the folder is there; otherwise skipped instead of failed.</summary>
    public bool TargetReachable { get; init; }

    public bool Any => NotOnBattery || NotMetered || WiredOnly || TargetReachable;

    /// <summary>How often a skipped run looks again whether it may start now.</summary>
    public static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Why the run may not start now; <c>null</c> if it may. What a system cannot tell (no battery information, no
    /// connection cost on macOS) does not hold a run back.
    /// </summary>
    public async Task<BackupSkipReason?> CheckAsync(ISystemState system, RepositoryLocation location, CancellationToken cancellationToken = default)
    {
        if (!Any)
            return null;
        if (NotOnBattery && await system.IsOnBatteryAsync(cancellationToken) == true)
            return BackupSkipReason.OnBattery;
        var network = system.IsNetworkTarget(location);
        if (network && NotMetered && await system.IsMeteredAsync(cancellationToken) == true)
            return BackupSkipReason.MeteredConnection;
        if (network && WiredOnly && await system.IsWiredAsync(cancellationToken) == false)
            return BackupSkipReason.NotWired;
        if (TargetReachable && !await system.IsReachableAsync(location, cancellationToken))
            return BackupSkipReason.TargetUnreachable;
        return null;
    }
}

/// <summary>What the conditions of scheduled runs ask the computer.</summary>
public interface ISystemState
{
    /// <summary><c>null</c>: unknown (e.g. no battery information).</summary>
    Task<bool?> IsOnBatteryAsync(CancellationToken cancellationToken = default);

    /// <summary><c>null</c>: unknown (macOS, Linux without NetworkManager).</summary>
    Task<bool?> IsMeteredAsync(CancellationToken cancellationToken = default);

    /// <summary>Whether the connection is wired (not Wi-Fi or mobile); <c>null</c>: unknown.</summary>
    Task<bool?> IsWiredAsync(CancellationToken cancellationToken = default);

    /// <summary>The server answers on its port, or the folder is there.</summary>
    Task<bool> IsReachableAsync(RepositoryLocation location, CancellationToken cancellationToken = default);

    /// <summary>The repository is reached over the network: a server, or a folder on a network share.</summary>
    bool IsNetworkTarget(RepositoryLocation location);
}

/// <summary>The computer's state as Windows, macOS and Linux tell it.</summary>
public sealed class SystemState(IProcessRunner? processes = null) : ISystemState
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(15);

    private static readonly TimeSpan ReachTimeout = TimeSpan.FromSeconds(10);

    private readonly IProcessRunner _processes = processes ?? new ProcessRunner();

    public async Task<bool?> IsOnBatteryAsync(CancellationToken cancellationToken = default)
    {
        if (OperatingSystem.IsWindows())
            return GetSystemPowerStatus(out var status) && status.BatteryFlag != 128 && status.AcLineStatus != 255 ? status.AcLineStatus == 0 : null;
        if (OperatingSystem.IsMacOS())
            return await OutputAsync("pmset", ["-g", "batt"], cancellationToken) is { } pmset
                ? pmset.Contains("'Battery Power'", StringComparison.Ordinal)
                : null;
        return LinuxOnBattery("/sys/class/power_supply");
    }

    /// <summary>On battery: a battery is there and no mains supply is online.</summary>
    internal static bool? LinuxOnBattery(string powerSupplies)
    {
        try
        {
            if (!Directory.Exists(powerSupplies))
                return null;
            var supplies = Directory.GetDirectories(powerSupplies)
                .Select(folder => (Type: Read(Path.Combine(folder, "type")), Online: Read(Path.Combine(folder, "online"))))
                .ToList();
            if (!supplies.Any(supply => supply.Type == "Battery"))
                return null;
            return !supplies.Any(supply => supply.Type is "Mains" or "USB" && supply.Online == "1");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        static string? Read(string file) => File.Exists(file) ? File.ReadAllText(file).Trim() : null;
    }

    public async Task<bool?> IsMeteredAsync(CancellationToken cancellationToken = default)
    {
        if (OperatingSystem.IsWindows())
            return await WindowsConnectionAsync(cancellationToken) is { } connection ? connection.Metered : null;
        if (OperatingSystem.IsLinux())
        {
            // NetworkManager: 1 yes, 3 guessed yes; 2 no, 4 guessed no; 0 unknown.
            var output = await OutputAsync("busctl", ["--system", "get-property", "org.freedesktop.NetworkManager",
                "/org/freedesktop/NetworkManager", "org.freedesktop.NetworkManager", "Metered"], cancellationToken);
            return output?.Trim().Split(' ').LastOrDefault() switch
            {
                "1" or "3" => true,
                "2" or "4" => false,
                _ => null,
            };
        }
        return null;
    }

    public async Task<bool?> IsWiredAsync(CancellationToken cancellationToken = default)
    {
        if (OperatingSystem.IsWindows())
            return await WindowsConnectionAsync(cancellationToken) is { } connection ? connection.Wired : null;

        var wireless = OperatingSystem.IsMacOS() ? await MacWirelessDevicesAsync(cancellationToken) : null;
        var active = NetworkInterface.GetAllNetworkInterfaces()
            .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up && adapter.NetworkInterfaceType != NetworkInterfaceType.Loopback
                && adapter.GetIPProperties().GatewayAddresses.Count > 0)
            .ToList();
        if (active.Count == 0)
            return null;
        return active.Any(adapter => adapter.NetworkInterfaceType != NetworkInterfaceType.Wireless80211
            && !(OperatingSystem.IsLinux() && Directory.Exists($"/sys/class/net/{adapter.Name}/wireless"))
            && !(wireless?.Contains(adapter.Name) ?? false));
    }

    /// <summary>The Wi-Fi devices of a Mac ("Hardware Port: Wi-Fi / Device: en0").</summary>
    private async Task<HashSet<string>?> MacWirelessDevicesAsync(CancellationToken cancellationToken)
    {
        if (await OutputAsync("networksetup", ["-listallhardwareports"], cancellationToken) is not { } output)
            return null;
        var devices = new HashSet<string>();
        var lines = output.Split('\n');
        for (var index = 0; index < lines.Length - 1; index++)
        {
            if (lines[index].Contains("Wi-Fi", StringComparison.Ordinal) || lines[index].Contains("AirPort", StringComparison.Ordinal))
            {
                var device = lines[index + 1].Split(':', 2);
                if (device.Length == 2)
                    devices.Add(device[1].Trim());
            }
        }
        return devices;
    }

    internal sealed record WindowsConnection(bool Metered, bool Wired);

    /// <summary>
    /// Windows' own view of the internet connection (what the settings show as "metered"), through Windows PowerShell,
    /// which can call the Windows Runtime; <c>null</c> without a connection or if it cannot tell.
    /// </summary>
    private async Task<WindowsConnection?> WindowsConnectionAsync(CancellationToken cancellationToken)
    {
        const string script = "[void][Windows.Networking.Connectivity.NetworkInformation,Windows.Networking.Connectivity,ContentType=WindowsRuntime];"
            + "$p=[Windows.Networking.Connectivity.NetworkInformation]::GetInternetConnectionProfile();"
            + "if ($p) { \"$($p.GetConnectionCost().NetworkCostType)|$($p.IsWlanConnectionProfile)|$($p.IsWwanConnectionProfile)\" }";
        var output = await OutputAsync("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", script], cancellationToken);
        return ParseWindowsConnection(output);
    }

    internal static WindowsConnection? ParseWindowsConnection(string? output)
    {
        var parts = output?.Trim().Split('|');
        if (parts is not { Length: 3 })
            return null;
        return new WindowsConnection(parts[0] is "Fixed" or "Variable",
            !string.Equals(parts[1], "True", StringComparison.OrdinalIgnoreCase) && !string.Equals(parts[2], "True", StringComparison.OrdinalIgnoreCase));
    }

    public async Task<bool> IsReachableAsync(RepositoryLocation location, CancellationToken cancellationToken = default)
    {
        if (location.Ssh is { } endpoint)
        {
            try
            {
                using var client = new TcpClient();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(ReachTimeout);
                await client.ConnectAsync(endpoint.Host, endpoint.Port, timeout.Token);
                return true;
            }
            catch (Exception exception) when (exception is SocketException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                return false;
            }
        }
        // A share that does not answer can keep Directory.Exists waiting for long.
        var exists = Task.Run(() => Directory.Exists(location.Url), cancellationToken);
        return await Task.WhenAny(exists, Task.Delay(ReachTimeout, cancellationToken)) == exists && await exists;
    }

    public bool IsNetworkTarget(RepositoryLocation location) => location.Ssh is not null || IsOnNetworkShare(location.Url);

    /// <summary>A folder on a network share: a UNC path, a mapped network drive, or an NFS/SMB/AFP mount.</summary>
    public static bool IsOnNetworkShare(string path)
    {
        if (path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal))
            return true;
        try
        {
            var full = Path.GetFullPath(path);
            var drive = DriveInfo.GetDrives()
                .Where(candidate => full.StartsWith(candidate.Name, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                .MaxBy(candidate => candidate.Name.Length);
            return drive?.DriveType == DriveType.Network
                || drive?.DriveFormat is "nfs" or "nfs4" or "cifs" or "smbfs" or "smb3" or "afpfs" or "fuse.sshfs";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    private async Task<string?> OutputAsync(string program, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _processes.RunAsync(program, arguments, CommandTimeout, cancellationToken: cancellationToken);
            return result is { ExitCode: 0 } ? result.StandardOutput : null;
        }
        catch (TimeoutException)
        {
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte AcLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);
}
