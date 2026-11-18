using BorgStudio.App.Resources;
using BorgStudio.App.ViewModels;
using BorgStudio.Core.Backups;
using BorgStudio.Core.Repositories;
using BorgStudio.Core.Security;
using BorgStudio.Core.Settings;
using BorgStudio.Tests.TestSupport;

namespace BorgStudio.Tests.ViewModels;

/// <summary>BorgStudio in the background: it runs the scheduled jobs itself, in the app, and starts at login.</summary>
public sealed class BackgroundModeTests : IDisposable
{
    private const string Pin = "123456";

    // Monday, 16 November 2026, 10:00.
    private static readonly DateTime Monday = new(2026, 11, 16, 10, 0, 0);

    private static readonly BackupSchedule HalfPastTen = new() { Kind = ScheduleKind.Daily, Time = new TimeOnly(10, 30) };

    private readonly TestServices _test = new();
    private RepositoryConfig _repository;
    private DateTime _now = Monday;

    public BackgroundModeTests()
    {
        _repository = new RepositoryConfig
        {
            Id = Guid.NewGuid(),
            Name = "Disk",
            ProviderId = "local",
            ProviderValues = new Dictionary<string, string> { ["path"] = RepositoryFolder },
            PassphraseMode = PassphraseMode.None,
            EncryptionMode = "none",
        };
        Directory.CreateDirectory(Source);
        Directory.CreateDirectory(RepositoryFolder);
    }

    public void Dispose() => _test.Dispose();

    private string Source => _test.Directory.Combine("Documents");

    private string RepositoryFolder => _test.Directory.Combine("repo");

    private BackupJob Job(bool catchUp = false, string name = "Documents") => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        RepositoryId = _repository.Id,
        Sources = [Source],
        Exclusions = new BackupExclusions { CachesAndTemp = false },
        Schedules = [HalfPastTen],
        CatchUp = catchUp,
    };

    private async Task<MainViewModel> StartAsync(ScheduleRunner runner, params BackupJob[] jobs)
    {
        _test.Store.Save([_repository]);
        _test.Jobs.Save(jobs);
        _test.Settings.Save(new AppSettings { ScheduleRunner = runner });
        var main = new MainViewModel(_test.Services) { SchedulerExecutable = "BorgStudio.exe", Clock = () => _now };
        await main.CheckBorgCommand.ExecuteAsync(null);
        await main.SchedulesApplied;
        _test.Borg.Calls.Clear();
        return main;
    }

    /// <summary>Lets the clock run to <paramref name="time"/> and the scheduler look.</summary>
    private async Task TickAtAsync(MainViewModel main, DateTime time)
    {
        _now = time;
        main.ScheduleTick();
        await main.DueJobsRun;
    }

    private int Backups => _test.Borg.Calls.Count(call => call.Arguments[0] == "create");

    [Fact]
    public async Task Switching_to_the_background_moves_the_schedules_out_of_the_system_and_starts_at_login()
    {
        var main = await StartAsync(ScheduleRunner.System, Job());
        Assert.Single(_test.Scheduler.Scheduled);
        Assert.Null(_test.Autostart.Executable);

        main.SetScheduleRunner(ScheduleRunner.Background);
        await main.SchedulesApplied;

        Assert.True(main.IsBackgroundMode);
        Assert.Empty(_test.Scheduler.Applied[^1]);
        Assert.Equal("BorgStudio.exe", _test.Autostart.Executable);
        Assert.Equal(ScheduleRunner.Background, _test.Settings.Load().ScheduleRunner);

        main.SetScheduleRunner(ScheduleRunner.System);
        await main.SchedulesApplied;

        Assert.Single(_test.Scheduler.Scheduled);
        Assert.Null(_test.Autostart.Executable);
    }

    [Fact]
    public async Task A_due_job_runs_in_the_app_with_its_card()
    {
        var job = Job();
        var main = await StartAsync(ScheduleRunner.Background, job);

        // The first look: nothing missed (no catch-up), nothing due yet.
        await TickAtAsync(main, Monday);
        await TickAtAsync(main, Monday.AddMinutes(20));
        Assert.Equal(0, Backups);

        await TickAtAsync(main, Monday.AddMinutes(30).AddSeconds(20));

        Assert.Equal(1, Backups);
        Assert.Equal(job.Name, main.CurrentBackup!.Title);
        Assert.Equal(BackupOutcome.Succeeded, Assert.Single(_test.Jobs.Load()).LastRun!.Outcome);
        Assert.Contains("Documents (background): Succeeded", File.ReadAllText(_test.Directory.Combine("logs", "scheduled.log")));
        Assert.False(main.IsBackupRunning);

        // Once per scheduled time.
        await TickAtAsync(main, Monday.AddMinutes(31));
        Assert.Equal(1, Backups);
    }

    [Fact]
    public async Task Missed_times_are_made_up_for_at_startup_once()
    {
        var main = await StartAsync(ScheduleRunner.Background, Job(catchUp: true), Job(catchUp: false, name: "Photos"));

        // Never run, so the last time (Sunday 10:30) was missed.
        await TickAtAsync(main, Monday);

        Assert.Equal(1, Backups);
        await TickAtAsync(main, Monday.AddMinutes(1));
        Assert.Equal(1, Backups);
    }

    [Fact]
    public async Task A_time_long_past_after_sleep_runs_only_with_catch_up()
    {
        var main = await StartAsync(ScheduleRunner.Background, Job(catchUp: false));
        await TickAtAsync(main, Monday);

        // The computer slept from 10:00 to 12:00.
        await TickAtAsync(main, Monday.AddHours(2));

        Assert.Equal(0, Backups);
    }

    [Fact]
    public async Task Paused_schedules_start_nothing()
    {
        var main = await StartAsync(ScheduleRunner.Background, Job());
        await TickAtAsync(main, Monday);

        main.SetSchedulesPaused(true);
        await TickAtAsync(main, Monday.AddMinutes(31));

        Assert.Equal(0, Backups);
        Assert.Equal(TrayState.Paused, main.TrayState);
        Assert.Equal(Strings.TrayPaused, main.NextScheduledText);
        Assert.True(_test.Settings.Load().SchedulesPaused);
    }

    [Fact]
    public async Task With_the_system_scheduler_the_app_starts_nothing()
    {
        var main = await StartAsync(ScheduleRunner.System, Job(catchUp: true));

        await TickAtAsync(main, Monday);
        await TickAtAsync(main, Monday.AddMinutes(31));

        Assert.Equal(0, Backups);
    }

    [Fact]
    public async Task The_vault_stays_locked_for_the_window()
    {
        _repository = _repository with { PassphraseMode = PassphraseMode.Vault, EncryptionMode = "repokey-blake2" };
        _test.Vault.SetUp(Pin);
        _test.Vault.SetPassphrase(_repository.Id, "from-the-vault");
        var main = await StartAsync(ScheduleRunner.Background, Job());
        main.ScheduledAccess.Enable();
        _test.Vault.Lock();
        await TickAtAsync(main, Monday);

        await TickAtAsync(main, Monday.AddMinutes(31));

        Assert.Equal("from-the-vault", _test.Borg.Calls.Single(call => call.Arguments[0] == "create").Passphrase);
        Assert.Equal(VaultState.Locked, _test.Vault.State);
        Assert.True(main.IsLocked);
    }

    [Fact]
    public async Task The_icon_shows_the_next_backup_and_a_failed_one()
    {
        var main = await StartAsync(ScheduleRunner.Background, Job());
        Assert.Equal(TrayState.Idle, main.TrayState);
        Assert.StartsWith(BorgTexts.Format(Strings.TrayNext, "Documents", "")[..^1], main.NextScheduledText);
        Assert.Contains(main.NextScheduledText, main.TrayToolTip);

        // The disk is gone at 10:30.
        Directory.Delete(RepositoryFolder);
        await TickAtAsync(main, Monday);
        await TickAtAsync(main, Monday.AddMinutes(31));

        Assert.Equal(TrayState.Failed, main.TrayState);
        Assert.Contains(BorgTexts.Format(Strings.TrayFailed, "Documents"), main.TrayToolTip);
    }

    [Fact]
    public async Task Back_up_now_from_the_icon_shows_the_job_and_runs_it()
    {
        var other = new RepositoryConfig { Id = Guid.NewGuid(), Name = "Other", ProviderId = "local",
            ProviderValues = new Dictionary<string, string> { ["path"] = _test.Directory.Combine("other") } };
        var job = Job();
        _test.Store.Save([other, _repository]);
        _test.Jobs.Save([job]);
        _test.Settings.Save(new AppSettings { ScheduleRunner = ScheduleRunner.Background });
        var main = new MainViewModel(_test.Services) { SchedulerExecutable = "BorgStudio.exe", Clock = () => _now };
        await main.CheckBorgCommand.ExecuteAsync(null);
        main.SelectedRepository = main.Repositories.Single(item => item.Name == "Other");

        await main.RunJobNowAsync(job.Id);
        await main.BackupFinished;

        Assert.Equal("Disk", main.SelectedRepository!.Name);
        Assert.Equal(MainViewModel.JobsTab, main.SelectedDetailsTab);
        Assert.Equal(job.Id, main.SelectedJob!.Job.Id);
        Assert.Equal(1, Backups);
    }
}
