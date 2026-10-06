namespace BorgStudio.Core.Borg;

public enum HostPlatform
{
    Windows,
    MacOS,
    Linux,
}

/// <summary>
/// Everything the borg search depends on, so it can be tested for any platform on any platform.
/// </summary>
/// <param name="PathVariable">Value of the PATH environment variable.</param>
/// <param name="SystemDirectory">Windows system directory (for wsl.exe); <c>null</c> elsewhere.</param>
public sealed record BorgSearchEnvironment(
    HostPlatform Platform,
    string? PathVariable,
    string HomeDirectory,
    string? SystemDirectory,
    Func<string, bool> FileExists)
{
    public static BorgSearchEnvironment Current() => new(
        OperatingSystem.IsWindows() ? HostPlatform.Windows
            : OperatingSystem.IsMacOS() ? HostPlatform.MacOS
            : HostPlatform.Linux,
        Environment.GetEnvironmentVariable("PATH"),
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        OperatingSystem.IsWindows() ? Environment.SystemDirectory : null,
        File.Exists);
}
