namespace BorgStudio.Core;

/// <summary>Where BorgStudio keeps its own files (never next to the executable: single file / signed app bundle).</summary>
public static class AppPaths
{
    /// <summary>
    /// Windows: %LOCALAPPDATA%\BorgStudio, macOS: ~/Library/Application Support/BorgStudio,
    /// Linux: $XDG_DATA_HOME/BorgStudio (usually ~/.local/share/BorgStudio).
    /// </summary>
    public static string DataDirectory { get; } = Path.Combine(
        OperatingSystem.IsMacOS()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support")
            : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BorgStudio");

    /// <summary>One sub folder per external plugin, named like the plugin assembly.</summary>
    public static string PluginsDirectory { get; } = Path.Combine(DataDirectory, "plugins");
}
