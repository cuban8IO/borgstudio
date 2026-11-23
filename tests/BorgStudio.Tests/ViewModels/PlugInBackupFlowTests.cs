using BorgStudio.App.Resources;
using BorgStudio.App.ViewModels;
using BorgStudio.Core.Backups;
using BorgStudio.Core.Repositories;
using BorgStudio.Core.Settings;
using BorgStudio.Providers.Usb;
using BorgStudio.Tests.TestSupport;

namespace BorgStudio.Tests.ViewModels;

/// <summary>Backing up when a USB drive is attached – BorgStudio in the background asks, then runs all jobs of the repository.</summary>
public sealed class PlugInBackupFlowTests : IDisposable
{
    private const string Id = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    // Monday, 16 November 2026, 10:00.
    private static readonly DateTime Monday = new(2026, 11, 16, 10, 0, 0);

    private readonly TestServices _test = new();
    private RepositoryConfig _repository;
    private DateTime _now = Monday;

    public PlugInBackupFlowTests()
    {
        _repository = new RepositoryConfig
        {
            Id = Guid.NewGuid(),
            Name = "Stick",
            ProviderId = UsbRepositoryProvider.ProviderId,
            ProviderValues = new Dictionary<string, string> { [UsbRepositoryProvider.PathKey] = Folder, [UsbRepositoryProvider.EjectKey] = "true" },
            PassphraseMode = PassphraseMode.None,
            EncryptionMode = "none",
            BorgRepositoryId = Id,
            PlugIn = new PlugInBackup { Enabled = true },
        };
        Directory.CreateDirectory(Source);
        Directory.CreateDirectory(Drive);
        _test.Drives.Attached.Add(new DriveRoot(_test.Directory.Combine("drives", "C"), IsNetwork: false));
    }

    public void Dispose() => _test.Dispose();

    private string Source => _test.Directory.Combine("Documents");

    /// <summary>The stick, as it gets attached.</summary>
    private string Drive => _test.Directory.Combine("drives", "F");

    private string Folder => Path.Combine(Drive, "borg");

    private int Backups => _test.Borg.Calls.Count(call => call.Arguments[0] == "create");

    private BackupJob Job(string name) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        RepositoryId = _repository.Id,
        Sources = [Source],
        Exclusions = new BackupExclusions { CachesAndTemp = false },
        Schedules = [new BackupSchedule { Kind = ScheduleKind.Daily, Time = new TimeOnly(10, 30) }],
        CatchUp = false,
    };

    private async Task<MainViewModel> StartAsync(ScheduleRunner runner = ScheduleRunner.Background, params BackupJob[] jobs)
    {
        _test.Store.Save([_repository]);
        _test.Jobs.Save(jobs.Length > 0 ? jobs : [Job("Documents"), Job("Photos")]);
        _test.Settings.Save(new AppSettings { ScheduleRunner = runner });
        var main = new MainViewModel(_test.Services) { SchedulerExecutable = "BorgStudio.exe", Clock = () => _now };
        await main.CheckBorgCommand.ExecuteAsync(null);
        await main.SchedulesApplied;
        _test.Borg.Calls.Clear();
        main.DriveTick(); // the drives attached at startup: remembered, nothing asked
        return main;
    }

    private async Task AttachAsync(MainViewModel main)
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(Path.Combine(Folder, "config"), $"[repository]\nversion = 1\nid = {Id}\n");
        _test.Drives.Attached.Add(new DriveRoot(Drive, IsNetwork: false));
        main.DriveTick();
        await main.PlugInRun;
    }

    [Fact]
    public async Task Attaching_asks_and_then_backs_up_all_jobs_and_ejects_once_at_the_end()
    {
        // The runs are recorded with the real time.
        var start = DateTime.Now;
        _now = start;
        var main = await StartAsync();

        await AttachAsync(main);

        var (title, message) = Assert.Single(_test.Dialogs.OnTop);
        Assert.Equal(Strings.PlugInTitle, title);
        Assert.Contains("Stick", message);
        Assert.Contains("Documents, Photos", message);
        Assert.Equal(2, Backups);
        Assert.All(_test.Borg.Calls.Where(call => call.Arguments[0] == "create"),
            call => Assert.Contains(call.Arguments, argument => argument.StartsWith(Folder + "::", StringComparison.Ordinal)));
        Assert.Equal([Folder], _test.Drives.Ejected);

        // Attached again soon after: backed up within the 12 hours, so nothing is asked.
        _test.Drives.Attached.RemoveAt(1);
        main.DriveTick();
        _now = start.AddHours(2);
        await AttachAsync(main);
        Assert.Single(_test.Dialogs.OnTop);

        // After the 12 hours it asks again.
        _test.Drives.Attached.RemoveAt(1);
        main.DriveTick();
        _now = start.AddHours(13);
        await AttachAsync(main);
        Assert.Equal(2, _test.Dialogs.OnTop.Count);
    }

    [Fact]
    public async Task A_backup_started_by_hand_does_not_hold_back_the_question()
    {
        _now = DateTime.Now;
        _test.Drives.Attached.Add(new DriveRoot(Drive, IsNetwork: false));
        Directory.CreateDirectory(Folder);
        File.WriteAllText(Path.Combine(Folder, "config"), $"[repository]\nversion = 1\nid = {Id}\n");
        var main = await StartAsync();
        await main.RunJobCommand.ExecuteAsync(null);
        Assert.Equal(1, Backups);
        Assert.False(_test.Jobs.Load().Single(job => job.LastRun is not null).LastRun!.Automatic);

        // Pulled and attached again right after trying it by hand: asked all the same.
        _test.Drives.Attached.RemoveAt(1);
        main.DriveTick();
        await AttachAsync(main);

        Assert.Single(_test.Dialogs.OnTop);
        Assert.Equal(3, Backups);
        Assert.All(_test.Jobs.Load(), job => Assert.True(job.LastRun!.Automatic));
    }

    [Fact]
    public async Task Not_now_leaves_the_drive_attached_and_backs_up_nothing()
    {
        var main = await StartAsync();
        _test.Dialogs.OnTopAnswer = false;

        await AttachAsync(main);

        Assert.Single(_test.Dialogs.OnTop);
        Assert.Equal(0, Backups);
        Assert.Empty(_test.Drives.Ejected);
    }

    [Fact]
    public async Task Pulled_while_asking_the_question_goes_away()
    {
        var main = await StartAsync();
        _test.Dialogs.OnTopOpenUntil = new TaskCompletionSource().Task; // never answered

        Directory.CreateDirectory(Folder);
        File.WriteAllText(Path.Combine(Folder, "config"), $"[repository]\nid = {Id}\n");
        _test.Drives.Attached.Add(new DriveRoot(Drive, IsNetwork: false));
        main.DriveTick();
        await Task.Delay(100);
        _test.Drives.Attached.RemoveAt(1);
        main.DriveTick();
        await main.PlugInRun;

        Assert.Single(_test.Dialogs.OnTop);
        Assert.Equal(0, Backups);
    }

    [Fact]
    public async Task Asked_whenever_BorgStudio_is_open_but_only_when_switched_on()
    {
        // Schedules run by the system's task scheduler: the open app asks all the same.
        var main = await StartAsync(ScheduleRunner.System);
        await AttachAsync(main);
        Assert.Single(_test.Dialogs.OnTop);
        Assert.Equal(2, Backups);

        _test.Dialogs.OnTop.Clear();
        _test.Borg.Calls.Clear();
        _repository = _repository with { PlugIn = new PlugInBackup { Enabled = false } };
        _test.Drives.Attached.RemoveAt(1);
        main = await StartAsync();
        await AttachAsync(main);
        Assert.Empty(_test.Dialogs.OnTop);
        Assert.Equal(0, Backups);
    }

    [Fact]
    public async Task Scheduled_runs_of_one_drive_in_a_row_eject_it_after_the_last()
    {
        _test.Drives.Attached.Add(new DriveRoot(Drive, IsNetwork: false));
        Directory.CreateDirectory(Folder);
        File.WriteAllText(Path.Combine(Folder, "config"), $"[repository]\nid = {Id}\n");
        var main = await StartAsync(ScheduleRunner.Background, Job("Documents"), Job("Photos"));

        _now = Monday.AddMinutes(1);
        main.ScheduleTick();
        _now = Monday.AddMinutes(31);
        main.ScheduleTick();
        await main.DueJobsRun;

        Assert.Equal(2, Backups);
        Assert.Equal([Folder], _test.Drives.Ejected);
    }

    [Fact]
    public async Task The_editor_keeps_the_setting_for_usb_drives_only()
    {
        _test.Settings.Save(new AppSettings { ScheduleRunner = ScheduleRunner.System });
        var editor = new RepositoryEditorViewModel(_test.Services, FakeBorg.Installation, _repository with { PlugIn = new PlugInBackup() });
        Assert.True(editor.IsUsb);
        Assert.True(editor.PlugInNeedsBackground);
        Assert.False(editor.PlugInEnabled);
        Assert.Equal(PlugInBackup.DefaultMinimumHours, editor.PlugInHours);

        editor.PlugInEnabled = true;
        editor.PlugInHours = 4;
        await editor.ConfirmCommand.ExecuteAsync(null);

        Assert.Equal(new PlugInBackup { Enabled = true, MinimumHours = 4 }, editor.Result!.Repository.PlugIn);
    }
}
