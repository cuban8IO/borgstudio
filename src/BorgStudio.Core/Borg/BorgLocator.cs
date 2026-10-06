namespace BorgStudio.Core.Borg;

/// <summary>Finds the places where a borg executable may be installed.</summary>
public static class BorgLocator
{
    // Apps started from the macOS Finder (or a desktop launcher) don't see the PATH of the user's shell,
    // so the usual install locations are searched explicitly after PATH.
    private static readonly string[] WindowsDirectories = [@"C:\msys64\ucrt64\bin", @"C:\cygwin64\bin"];
    private static readonly string[] MacOSDirectories = ["/opt/homebrew/bin", "/usr/local/bin", "/opt/local/bin", "~/.local/bin"];
    private static readonly string[] LinuxDirectories = ["/usr/bin", "/usr/local/bin", "~/.local/bin", "/snap/bin"];

    /// <summary>Existing native borg executables: PATH first, then the usual install locations. No duplicates.</summary>
    public static IReadOnlyList<string> FindExecutables(BorgSearchEnvironment environment)
    {
        var windows = environment.Platform == HostPlatform.Windows;
        var executableName = windows ? "borg.exe" : "borg";

        var pathDirectories = (environment.PathVariable ?? "")
            .Split(windows ? ';' : ':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(directory => directory.Trim('"'));

        var knownDirectories = (environment.Platform switch
            {
                HostPlatform.Windows => WindowsDirectories,
                HostPlatform.MacOS => MacOSDirectories,
                _ => LinuxDirectories,
            })
            .Select(directory => directory.StartsWith("~/", StringComparison.Ordinal)
                ? Join(environment, environment.HomeDirectory, directory[2..])
                : directory);

        return pathDirectories
            .Concat(knownDirectories)
            .Where(directory => directory.Length > 0)
            .Select(directory => Join(environment, directory, executableName))
            .Distinct(windows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .Where(environment.FileExists)
            .ToList();
    }

    /// <summary>Path of wsl.exe on Windows if WSL is available, otherwise <c>null</c>.</summary>
    public static string? FindWsl(BorgSearchEnvironment environment)
    {
        if (environment.Platform != HostPlatform.Windows || environment.SystemDirectory is null)
            return null;

        var wsl = Join(environment, environment.SystemDirectory, "wsl.exe");
        return environment.FileExists(wsl) ? wsl : null;
    }

    // Uses the separator of the searched platform, not of the one we run on (tests cover all platforms everywhere).
    private static string Join(BorgSearchEnvironment environment, string directory, string name)
    {
        var separator = environment.Platform == HostPlatform.Windows ? '\\' : '/';
        return directory.TrimEnd('\\', '/') + separator + name.Replace('/', separator);
    }
}
