using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using BorgStudio.Plugins;
using BorgStudio.Providers.NetworkDisk.Resources;

namespace BorgStudio.Providers.NetworkDisk;

/// <summary>
/// Windows' built-in iSCSI initiator. Logging in needs administrator rights, so setting up (with one UAC prompt) creates
/// two tasks in the Task Scheduler that run with highest rights for exactly this target – log in, log out – and BorgStudio
/// starts them later without asking (<c>schtasks /run</c>).
/// </summary>
public sealed class WindowsIscsi : INetworkDiskSystem
{
    public const string TaskFolder = @"\BorgStudio\";

    public static string TaskName(string id, string action) => $"NetworkDisk-{id}-{action}";

    public async Task SetUpAsync(NetworkDiskTarget target, string id, string? chapSecret, CancellationToken cancellationToken)
    {
        var log = Path.Combine(Path.GetTempPath(), $"borgstudio-network-disk-{id}.log");
        File.Delete(log);
        var script = SetupScript(target, id, chapSecret, CurrentUser(), log);
        var start = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
            Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + Encode(script),
        };
        try
        {
            using var process = Process.Start(start) ?? throw new RepositoryUnavailableException(Strings.SetupFailed);
            await process.WaitForExitAsync(cancellationToken);
            if (process.ExitCode != 0)
                throw new RepositoryUnavailableException(string.Format(CultureInfo.CurrentCulture, Strings.SetupFailedBecause,
                    File.Exists(log) ? File.ReadAllText(log).Trim() : process.ExitCode.ToString(CultureInfo.InvariantCulture)));
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            // The UAC prompt was declined.
            throw new RepositoryUnavailableException(Strings.SetupDeclined);
        }
        finally
        {
            File.Delete(log);
        }
    }

    public async Task ConnectAsync(NetworkDiskTarget target, string id, CancellationToken cancellationToken)
    {
        var result = await Commands.RunAsync("schtasks.exe", ["/run", "/tn", TaskFolder + TaskName(id, "connect")], cancellationToken: cancellationToken);
        if (result is not { ExitCode: 0 })
            throw new RepositoryUnavailableException(Strings.NotSetUp);
    }

    public async Task<string?> DisconnectAsync(NetworkDiskTarget target, string id, CancellationToken cancellationToken)
    {
        var result = await Commands.RunAsync("schtasks.exe", ["/run", "/tn", TaskFolder + TaskName(id, "disconnect")], cancellationToken: cancellationToken);
        return result is { ExitCode: 0 } ? null : string.Format(CultureInfo.CurrentCulture, Strings.DisconnectFailed, target.Name, result?.Error.Trim() ?? "schtasks");
    }

    private static string CurrentUser() =>
        OperatingSystem.IsWindows() ? System.Security.Principal.WindowsIdentity.GetCurrent().Name : Environment.UserName;

    /// <summary>PowerShell's -EncodedCommand: UTF-16LE, Base64.</summary>
    public static string Encode(string script) => Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

    /// <summary>PowerShell single quoting.</summary>
    private static string Quote(string value) => "'" + value.Replace("'", "''") + "'";

    /// <summary>The command the "connect" task runs.</summary>
    public static string ConnectCommand(NetworkDiskTarget target, string? chapSecret)
    {
        var chap = target.ChapUser is { } user && chapSecret is { Length: > 0 }
            ? $" -AuthenticationType ONEWAYCHAP -ChapUsername {Quote(user)} -ChapSecret {Quote(chapSecret)}"
            : "";
        return "$t = Get-IscsiTarget | Where-Object { $_.NodeAddress -eq " + Quote(target.Name) + " }; "
            + "if (-not $t -or -not $t.IsConnected) { Connect-IscsiTarget -NodeAddress " + Quote(target.Name)
            + $" -TargetPortalAddress {Quote(target.Host)} -TargetPortalPortNumber {target.Port}{chap} | Out-Null }}";
    }

    /// <summary>The command the "disconnect" task runs.</summary>
    public static string DisconnectCommand(NetworkDiskTarget target) =>
        $"Disconnect-IscsiTarget -NodeAddress {Quote(target.Name)} -Confirm:$false";

    /// <summary>
    /// Runs elevated: starts the initiator service, adds the portal, and registers the two tasks (highest rights, the user's
    /// account, also while not logged on). Errors end up in <paramref name="log"/>.
    /// </summary>
    public static string SetupScript(NetworkDiskTarget target, string id, string? chapSecret, string user, string log)
    {
        string Action(string command) =>
            "(New-ScheduledTaskAction -Execute 'powershell.exe' -Argument "
            + Quote("-NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand " + Encode(command)) + ")";
        return string.Join("\n",
            "$ErrorActionPreference = 'Stop'",
            "try {",
            "  Set-Service -Name MSiSCSI -StartupType Automatic",
            "  Start-Service -Name MSiSCSI",
            $"  if (-not (Get-IscsiTargetPortal | Where-Object {{ $_.TargetPortalAddress -eq {Quote(target.Host)} -and $_.TargetPortalPortNumber -eq {target.Port} }})) {{",
            $"    New-IscsiTargetPortal -TargetPortalAddress {Quote(target.Host)} -TargetPortalPortNumber {target.Port} | Out-Null",
            "  }",
            $"  $principal = New-ScheduledTaskPrincipal -UserId {Quote(user)} -LogonType S4U -RunLevel Highest",
            "  $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit (New-TimeSpan -Minutes 5)",
            $"  Register-ScheduledTask -TaskPath {Quote(TaskFolder)} -TaskName {Quote(TaskName(id, "connect"))} -Action {Action(ConnectCommand(target, chapSecret))} -Principal $principal -Settings $settings -Force | Out-Null",
            $"  Register-ScheduledTask -TaskPath {Quote(TaskFolder)} -TaskName {Quote(TaskName(id, "disconnect"))} -Action {Action(DisconnectCommand(target))} -Principal $principal -Settings $settings -Force | Out-Null",
            "} catch {",
            $"  $_.Exception.Message | Out-File -FilePath {Quote(log)} -Encoding utf8",
            "  exit 1",
            "}");
    }
}
