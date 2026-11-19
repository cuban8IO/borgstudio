using System.Runtime.Versioning;
using BorgStudio.App.Services;
using BorgStudio.Core.Backups;
using BorgStudio.Tests.TestSupport;
using Microsoft.Win32;

namespace BorgStudio.Tests.Backups;

/// <summary>Starting at login, and a second start that brings the first one's window forward.</summary>
public sealed class AutostartTests : IDisposable
{
    private readonly TemporaryDirectory _directory = new();

    public void Dispose() => _directory.Dispose();

    [Fact]
    public void Linux_gets_an_autostart_entry_with_a_quoted_command()
    {
        var autostart = new XdgAutostart(_directory.Combine("autostart"));
        Assert.False(autostart.IsEnabled);

        autostart.Enable("/opt/Borg Studio/BorgStudio");

        Assert.True(autostart.IsEnabled);
        var entry = File.ReadAllText(autostart.FilePath);
        Assert.Contains("Exec=\"/opt/Borg Studio/BorgStudio\" --background\n", entry);
        Assert.Contains("Type=Application", entry);

        autostart.Disable();
        Assert.False(File.Exists(autostart.FilePath));
        autostart.Disable();
    }

    [Fact]
    public async Task MacOS_gets_a_login_agent_the_scheduler_leaves_alone()
    {
        var agents = _directory.Combine("agents");
        var autostart = new LaunchdAutostart(agents);
        autostart.Enable("/Applications/BorgStudio.app/Contents/MacOS/BorgStudio");

        var plist = File.ReadAllText(autostart.FilePath);
        Assert.Contains("<string>--background</string>", plist);
        Assert.Contains("<key>RunAtLoad</key>\n    <true/>", plist);

        // Updating the schedules removes the scheduler's agents, not this one.
        await new LaunchdScheduler(new NoCommands(), agents).ApplyAsync([], "/Applications/BorgStudio.app/Contents/MacOS/BorgStudio");
        Assert.True(autostart.IsEnabled);

        autostart.Disable();
        Assert.False(autostart.IsEnabled);
    }

    [WindowsFact]
    [SupportedOSPlatform("windows")]
    public void Windows_gets_a_run_entry()
    {
        var key = $@"Software\BorgStudioTests\{Guid.NewGuid():N}";
        try
        {
            var autostart = new WindowsAutostart(key);
            Assert.False(autostart.IsEnabled);

            autostart.Enable(@"C:\Tools\Borg Studio\BorgStudio.exe");

            Assert.True(autostart.IsEnabled);
            Assert.Equal("\"C:\\Tools\\Borg Studio\\BorgStudio.exe\" --background", autostart.Command);
            autostart.Disable();
            Assert.False(autostart.IsEnabled);
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\BorgStudioTests", throwOnMissingSubKey: false);
        }
    }

    [WindowsFact]
    [SupportedOSPlatform("windows")]
    public void Windows_gets_a_start_menu_shortcut_for_toasts()
    {
        var path = _directory.Combine("Programs", "BorgStudio.lnk");
        var executable = _directory.Combine("BorgStudio.exe");
        File.WriteAllText(executable, "");

        Assert.True(Core.Notifications.StartMenuShortcut.Ensure(path, executable, "net.cuban8io.BorgStudio"));
        Assert.Equal((executable, "net.cuban8io.BorgStudio"), Core.Notifications.StartMenuShortcut.Read(path));
        // Up to date: left alone; moved app: rewritten.
        Assert.False(Core.Notifications.StartMenuShortcut.Ensure(path, executable, "net.cuban8io.BorgStudio"));
        var moved = _directory.Combine("moved", "BorgStudio.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(moved)!);
        File.WriteAllText(moved, "");
        Assert.True(Core.Notifications.StartMenuShortcut.Ensure(path, moved, "net.cuban8io.BorgStudio"));
        Assert.Equal(moved, Core.Notifications.StartMenuShortcut.Read(path)!.Value.Target);
    }

    [Fact]
    public async Task A_second_start_asks_the_first_to_show_its_window()
    {
        var data = _directory.Combine("data");
        using var first = SingleInstance.TryClaim(data);
        Assert.NotNull(first);
        var shown = new TaskCompletionSource();
        first.ShowRequested += (_, _) => shown.TrySetResult();

        // The second start finds the folder taken.
        Assert.Null(SingleInstance.TryClaim(data));
        Assert.True(SingleInstance.ShowExisting(data));
        await shown.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Once the first one has gone, nobody answers and the next start may claim the folder.
        first.Dispose();
        Assert.False(SingleInstance.ShowExisting(data, TimeSpan.FromMilliseconds(300)));
        using var next = SingleInstance.TryClaim(data);
        Assert.NotNull(next);
    }

    /// <summary>Answers every command with success.</summary>
    private sealed class NoCommands : Core.Processes.IProcessRunner
    {
        public Task<Core.Processes.ProcessResult?> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout,
            IReadOnlyDictionary<string, string?>? environment = null, string? standardInput = null, CancellationToken cancellationToken = default,
            Action<string>? standardErrorLine = null, Action<string>? standardOutputLine = null, string? workingDirectory = null) =>
            Task.FromResult<Core.Processes.ProcessResult?>(new Core.Processes.ProcessResult(0, "501", ""));
    }
}
