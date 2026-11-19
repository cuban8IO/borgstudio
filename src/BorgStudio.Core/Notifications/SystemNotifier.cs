using System.Text;
using System.Xml.Linq;
using BorgStudio.Core.Processes;
using Microsoft.Win32;

namespace BorgStudio.Core.Notifications;

/// <summary>
/// Shows a notification with the system's own means – nothing to install: a toast on Windows (through Windows
/// PowerShell, under BorgStudio's name and icon), <c>osascript</c> on macOS, <c>notify-send</c> (or <c>gdbus</c>) on Linux.
/// </summary>
/// <param name="iconPath">A PNG shown with the notification (Windows, Linux); <c>null</c>: none.</param>
/// <param name="executable">BorgStudio's executable, for the start menu shortcut Windows needs to show toasts.</param>
public sealed class SystemNotifier(IProcessRunner processes, string? iconPath = null, string? executable = null)
{
    /// <summary>
    /// The id Windows shows notifications under: carried by BorgStudio's start menu shortcut (see
    /// <see cref="StartMenuShortcut"/>), with name and icon in HKCU\Software\Classes\AppUserModelId.
    /// </summary>
    public const string AppUserModelId = "net.cuban8io.BorgStudio";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    /// <summary>The platform to notify on (tests pretend another).</summary>
    internal OSPlatform Platform { get; init; } =
        OperatingSystem.IsWindows() ? OSPlatform.Windows : OperatingSystem.IsMacOS() ? OSPlatform.OSX : OSPlatform.Linux;

    /// <summary>Registers BorgStudio for toasts: shortcut, name and icon (tests leave the system alone).</summary>
    internal bool RegisterName { get; init; } = true;

    internal enum OSPlatform
    {
        Windows,
        OSX,
        Linux,
    }

    /// <exception cref="NotificationException" />
    public Task SendAsync(NotificationMessage message, CancellationToken cancellationToken = default) => Platform switch
    {
        OSPlatform.Windows => ToastAsync(message, cancellationToken),
        OSPlatform.OSX => OsaScriptAsync(message, cancellationToken),
        _ => NotifySendAsync(message, cancellationToken),
    };

    // --- Windows ---

    /// <summary>The PowerShell script that shows the toast in $env:BORGSTUDIO_TOAST under $env:BORGSTUDIO_AUMID.</summary>
    internal const string ToastScript = """
        $ErrorActionPreference = 'Stop'
        [Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] | Out-Null
        [Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom, ContentType = WindowsRuntime] | Out-Null
        $xml = New-Object Windows.Data.Xml.Dom.XmlDocument
        $xml.LoadXml($env:BORGSTUDIO_TOAST)
        $toast = New-Object Windows.UI.Notifications.ToastNotification $xml
        [Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier($env:BORGSTUDIO_AUMID).Show($toast)
        """;

    internal static string ToastXml(NotificationMessage message) =>
        new XElement("toast",
            new XElement("visual",
                new XElement("binding", new XAttribute("template", "ToastGeneric"),
                    new XElement("text", message.Title),
                    new XElement("text", message.Body)))).ToString(SaveOptions.DisableFormatting);

    private async Task ToastAsync(NotificationMessage message, CancellationToken cancellationToken)
    {
        if (RegisterName && OperatingSystem.IsWindows())
        {
            RegisterAppUserModelId();
            // Windows needs a moment to take in a new shortcut before it shows toasts for it.
            if (EnsureShortcut())
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }

        var powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        var script = Convert.ToBase64String(Encoding.Unicode.GetBytes(ToastScript));
        var result = await processes.RunAsync(powershell,
            ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", script], Timeout,
            new Dictionary<string, string?>
            {
                ["BORGSTUDIO_TOAST"] = ToastXml(message),
                ["BORGSTUDIO_AUMID"] = AppUserModelId,
            }, cancellationToken: cancellationToken);
        Check(result, "powershell.exe");
    }

    /// <summary>Lets Windows show BorgStudio's name and icon on its toasts (no shortcut or package needed).</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private void RegisterAppUserModelId()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\AppUserModelId\{AppUserModelId}");
            key.SetValue("DisplayName", "BorgStudio");
            if (iconPath is not null && File.Exists(iconPath))
                key.SetValue("IconUri", iconPath);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            // The shortcut gives the name and icon too.
        }
    }

    /// <summary>Without a start menu shortcut carrying the id, Windows keeps the toast in its history but never shows it.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private bool EnsureShortcut()
    {
        if (executable is null)
            return false;
        try
        {
            return StartMenuShortcut.Ensure(StartMenuShortcut.DefaultPath, executable, AppUserModelId);
        }
        catch (Exception exception) when (exception is System.Runtime.InteropServices.COMException or IOException or UnauthorizedAccessException)
        {
            throw new NotificationException($"The start menu shortcut Windows needs for notifications could not be created: {exception.Message}", exception);
        }
    }

    // --- macOS ---

    private async Task OsaScriptAsync(NotificationMessage message, CancellationToken cancellationToken)
    {
        // The texts go in as arguments: nothing to escape inside the script.
        var result = await processes.RunAsync("osascript",
            ["-e", "on run argv", "-e", "display notification (item 2 of argv) with title \"BorgStudio\" subtitle (item 1 of argv)",
             "-e", "end run", message.Title, message.Body], Timeout, cancellationToken: cancellationToken);
        Check(result, "osascript");
    }

    // --- Linux ---

    private async Task NotifySendAsync(NotificationMessage message, CancellationToken cancellationToken)
    {
        List<string> arguments = ["--app-name=BorgStudio"];
        if (iconPath is not null)
            arguments.Add($"--icon={iconPath}");
        if (message.Event is NotificationEvent.Failed)
            arguments.Add("--urgency=critical");
        arguments.AddRange(["--", message.Title, message.Body]);

        var result = await processes.RunAsync("notify-send", arguments, Timeout, cancellationToken: cancellationToken);
        if (result is not null)
        {
            Check(result, "notify-send");
            return;
        }

        // No libnotify tools: ask the notification service directly.
        result = await processes.RunAsync("gdbus",
            ["call", "--session", "--dest", "org.freedesktop.Notifications", "--object-path", "/org/freedesktop/Notifications",
             "--method", "org.freedesktop.Notifications.Notify", "BorgStudio", "0", GVariantString(iconPath ?? ""),
             GVariantString(message.Title), GVariantString(message.Body), "[]", "{}", "-1"], Timeout, cancellationToken: cancellationToken);
        if (result is null)
            throw new NotificationException("Neither notify-send nor gdbus was found (install libnotify-bin).");
        Check(result, "gdbus");
    }

    /// <summary>A string in GVariant text format: "…" with \ and " escaped.</summary>
    internal static string GVariantString(string text) => $"\"{text.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";

    private static void Check(ProcessResult? result, string program)
    {
        if (result is null)
            throw new NotificationException($"{program} was not found.");
        if (result.ExitCode != 0)
        {
            var error = (result.StandardError.Trim() is { Length: > 0 } text ? text : result.StandardOutput.Trim());
            throw new NotificationException($"{program}: {(error.Length > 300 ? error[..300] + "…" : error)}");
        }
    }
}
