using BorgStudio.Core.Borg;

namespace BorgStudio.Tests.Borg;

public class BorgLocatorTests
{
    private static BorgSearchEnvironment Environment(
        HostPlatform platform, string? path, string home, params string[] existingFiles) =>
        new(platform, path, home, platform == HostPlatform.Windows ? @"C:\Windows\system32" : null,
            existingFiles.Contains);

    [Fact]
    public void Linux_searches_PATH_first_then_usual_locations()
    {
        var environment = Environment(HostPlatform.Linux, "/opt/tools:/usr/bin", "/home/me",
            "/usr/bin/borg", "/opt/tools/borg", "/home/me/.local/bin/borg");

        Assert.Equal(
            ["/opt/tools/borg", "/usr/bin/borg", "/home/me/.local/bin/borg"],
            BorgLocator.FindExecutables(environment));
    }

    [Fact]
    public void MacOS_finds_Homebrew_even_without_it_on_PATH()
    {
        // Typical PATH of an app started from the Finder.
        var environment = Environment(HostPlatform.MacOS, "/usr/bin:/bin:/usr/sbin:/sbin", "/Users/me",
            "/opt/homebrew/bin/borg");

        Assert.Equal(["/opt/homebrew/bin/borg"], BorgLocator.FindExecutables(environment));
    }

    [Fact]
    public void Windows_looks_for_borg_exe_and_ignores_case_quotes_and_empty_entries()
    {
        var environment = Environment(HostPlatform.Windows, @"""C:\Tools\Borg\"";;c:\msys64\UCRT64\bin", @"C:\Users\me",
            @"C:\Tools\Borg\borg.exe", @"c:\msys64\UCRT64\bin\borg.exe", @"C:\msys64\ucrt64\bin\borg.exe");

        Assert.Equal(
            [@"C:\Tools\Borg\borg.exe", @"c:\msys64\UCRT64\bin\borg.exe"],
            BorgLocator.FindExecutables(environment));
    }

    [Fact]
    public void Nothing_installed_means_no_candidates()
    {
        var environment = Environment(HostPlatform.Linux, null, "/home/me");

        Assert.Empty(BorgLocator.FindExecutables(environment));
    }

    [Fact]
    public void FindWsl_returns_wsl_exe_only_on_Windows_when_present()
    {
        Assert.Equal(@"C:\Windows\system32\wsl.exe",
            BorgLocator.FindWsl(Environment(HostPlatform.Windows, null, @"C:\Users\me", @"C:\Windows\system32\wsl.exe")));
        Assert.Null(BorgLocator.FindWsl(Environment(HostPlatform.Windows, null, @"C:\Users\me")));
        Assert.Null(BorgLocator.FindWsl(Environment(HostPlatform.Linux, null, "/home/me", @"C:\Windows\system32\wsl.exe")));
    }
}
