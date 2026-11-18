using System.Runtime.Versioning;
using Microsoft.Win32;

namespace BorgStudio.Core.Backups;

/// <summary>
/// Starts BorgStudio at login, in the background (only the icon in the notification area), so its own scheduler can run
/// the scheduled jobs: a "Run" entry in the registry on Windows, a LaunchAgent on macOS, an XDG autostart entry on Linux.
/// </summary>
public interface IAutostart
{
    /// <summary>Whether BorgStudio starts at login (with any executable).</summary>
    bool IsEnabled { get; }

    /// <summary>Starts <paramref name="executable"/> with <see cref="Autostart.BackgroundArgument"/> at login (replaces an older entry).</summary>
    void Enable(string executable);

    /// <summary>Removes the entry, if there is one.</summary>
    void Disable();
}

public static class Autostart
{
    /// <summary>Started at login: no window, only the icon in the notification area.</summary>
    public const string BackgroundArgument = "--background";

    public static IAutostart CreateDefault()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsWindows())
            return new WindowsAutostart();
        if (OperatingSystem.IsMacOS())
            return new LaunchdAutostart(Path.Combine(home, "Library", "LaunchAgents"));
        return new XdgAutostart(Path.Combine(
            Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } config ? config : Path.Combine(home, ".config"),
            "autostart"));
    }
}

/// <summary>A value in HKCU\Software\Microsoft\Windows\CurrentVersion\Run.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsAutostart(string keyPath = WindowsAutostart.RunKey, string valueName = "BorgStudio") : IAutostart
{
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(keyPath);
            return key?.GetValue(valueName) is string;
        }
    }

    /// <summary>The command line in the registry (tests).</summary>
    public string? Command
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(keyPath);
            return key?.GetValue(valueName) as string;
        }
    }

    public void Enable(string executable)
    {
        using var key = Registry.CurrentUser.CreateSubKey(keyPath);
        key.SetValue(valueName, $"\"{executable}\" {Autostart.BackgroundArgument}", RegistryValueKind.String);
    }

    public void Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: true);
        key?.DeleteValue(valueName, throwOnMissingValue: false);
    }
}

/// <summary>
/// A LaunchAgent that runs at login. Its label lies outside the scheduler's <see cref="LaunchdScheduler.Prefix"/>, so
/// updating the schedules never removes it. Written only: launchd loads it at the next login (loading it now would
/// start the app a second time).
/// </summary>
public sealed class LaunchdAutostart(string agentsDirectory) : IAutostart
{
    public const string Label = "net.cuban8io.borgstudio-autostart";

    public string FilePath => Path.Combine(agentsDirectory, Label + ".plist");

    public bool IsEnabled => File.Exists(FilePath);

    public void Enable(string executable)
    {
        Directory.CreateDirectory(agentsDirectory);
        File.WriteAllText(FilePath, LaunchdScheduler.Plist(Label, [executable, Autostart.BackgroundArgument], runAtLoad: true,
            calendar: "    <key>LimitLoadToSessionType</key>\n    <string>Aqua</string>\n"));
    }

    public void Disable()
    {
        if (File.Exists(FilePath))
            File.Delete(FilePath);
    }
}

/// <summary>~/.config/autostart/borgstudio.desktop – started by the desktop environment at login.</summary>
public sealed class XdgAutostart(string autostartDirectory) : IAutostart
{
    public string FilePath => Path.Combine(autostartDirectory, "borgstudio.desktop");

    public bool IsEnabled => File.Exists(FilePath);

    public void Enable(string executable)
    {
        Directory.CreateDirectory(autostartDirectory);
        File.WriteAllText(FilePath, Entry(executable));
    }

    public void Disable()
    {
        if (File.Exists(FilePath))
            File.Delete(FilePath);
    }

    public static string Entry(string executable) =>
        $"""
        [Desktop Entry]
        Type=Application
        Name=BorgStudio
        Comment=Runs scheduled backups
        Exec={Quote(executable)} {Autostart.BackgroundArgument}
        Terminal=false
        X-GNOME-Autostart-enabled=true

        """.ReplaceLineEndings("\n"); // A Windows checkout gives the template CRLF; desktop files are Unix files.

    /// <summary>Quoted as the desktop entry specification wants it: in double quotes, with ", `, $ and \ escaped.</summary>
    private static string Quote(string argument)
    {
        var escaped = argument.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("`", "\\`").Replace("$", "\\$");
        // Within a desktop file a backslash is itself escaped once more.
        return "\"" + escaped.Replace("\\", "\\\\") + "\"";
    }
}
