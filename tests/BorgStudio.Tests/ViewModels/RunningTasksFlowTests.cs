using System.Diagnostics;
using System.Text.Json;
using BorgStudio.App.Resources;
using BorgStudio.App.Services;
using BorgStudio.App.ViewModels;
using BorgStudio.Core.Backups;
using BorgStudio.Core.Progress;
using BorgStudio.Core.Repositories;
using BorgStudio.Core.Settings;
using BorgStudio.Tests.TestSupport;

namespace BorgStudio.Tests.ViewModels;

/// <summary>The list of running tasks in the status bar: what works, what waits, what the task scheduler runs elsewhere.</summary>
public sealed class RunningTasksFlowTests : IDisposable
{
    // Monday, 16 November 2026.
    private static readonly DateTime Monday = new(2026, 11, 16, 10, 0, 0);

    private readonly TestServices _test = new();
    private readonly RepositoryConfig _repository;

    public RunningTasksFlowTests()
    {
        _repository = new RepositoryConfig
        {
            Id = Guid.NewGuid(),
            Name = "Disk",
            ProviderId = "local",
            ProviderValues = new Dictionary<string, string> { ["path"] = _test.Directory.Combine("repo") },
            PassphraseMode = PassphraseMode.None,
            EncryptionMode = "none",
        };
        Directory.CreateDirectory(_test.Directory.Combine("repo"));
        Directory.CreateDirectory(Source);
        _test.Store.Save([_repository]);
    }

    public void Dispose() => _test.Dispose();

    private string Source => _test.Directory.Combine("Documents");

    private BackupJob Job(RunConditions? conditions = null) => new()
    {
        Id = Guid.NewGuid(),
        Name = "Documents",
        RepositoryId = _repository.Id,
        Sources = [Source],
        Exclusions = new BackupExclusions { CachesAndTemp = false },
        Schedules = [new BackupSchedule { Kind = ScheduleKind.Daily, Time = new TimeOnly(10, 30) }],
        CatchUp = false,
        Conditions = conditions ?? new RunConditions(),
    };

    private RunMarkers Markers => RunMarkers.ForDataDirectory(_test.Jobs.DataDirectory);

    [Fact]
    public async Task A_backup_is_listed_with_its_progress_and_can_be_stopped_from_the_list()
    {
        _test.Jobs.Save([Job()]);
        var main = new MainViewModel(_test.Services);
        await main.CheckBorgCommand.ExecuteAsync(null);
        Assert.Equal(Strings.TasksNone, main.Tasks.SummaryText);
        Assert.False(main.Tasks.HasTasks);

        RunningTaskViewModel? seen = null;
        var status = "";
        _test.Borg.AfterCreateProgress = _ =>
        {
            if (seen is not null)
                return;
            seen = Assert.Single(main.Tasks.Items);
            status = seen.Status;
            Assert.True(main.Tasks.IsBusy);
            Assert.Equal(Strings.TasksOne, main.Tasks.SummaryText.Split(" · ")[0]);
            seen.CancelCommand.Execute(null);
        };
        await main.RunJobCommand.ExecuteAsync(null);

        Assert.NotNull(seen);
        Assert.Equal("Disk", seen.Detail);
        Assert.True(seen.IsRunning);
        Assert.Contains(Strings.BackupRunning, status);
        Assert.Equal(BackupOutcome.Cancelled, _test.Jobs.Load()[0].LastRun!.Outcome);
        Assert.Empty(main.Tasks.Items);
        Assert.Equal(Strings.TasksNone, main.Tasks.SummaryText);

        // "Anzeigen" goes to the job.
        main.ShowJob(_repository.Id, _test.Jobs.Load()[0].Id);
        Assert.Equal(MainViewModel.JobsTab, main.SelectedDetailsTab);
        Assert.Equal("Documents", main.SelectedJob!.Name);
    }

    [Fact]
    public void Progress_tasks_show_while_active_and_say_how_long_is_left()
    {
        var time = new ManualTime();
        var progress = new ProgressViewModel(time);
        var tasks = new RunningTasksViewModel();
        using var registration = tasks.Track(MainViewModel.ProgressTask("Prüfen", "Disk", progress, show: null));
        Assert.Empty(tasks.Items);

        progress.Start("Prüfen …", expected: TimeSpan.FromMinutes(10), cancel: () => { });
        time.Advance(TimeSpan.FromMinutes(4));
        progress.Tick();

        var task = Assert.Single(tasks.Items);
        Assert.Equal(TimeSpan.FromMinutes(6), task.Remaining);
        Assert.True(task.CanCancel);
        Assert.False(task.CanShow);
        Assert.Equal(BorgTexts.Format(Strings.TasksRemaining, Strings.TasksOne, BorgTexts.Duration(TimeSpan.FromMinutes(6))), tasks.SummaryText);

        progress.Stop();
        Assert.Empty(tasks.Items);
        Assert.Equal(Strings.TasksNone, tasks.SummaryText);
    }

    [Fact]
    public async Task A_run_held_back_by_a_condition_waits_in_the_list_until_stopped()
    {
        var job = Job(new RunConditions { NotOnBattery = true });
        _test.Jobs.Save([job]);
        _test.Settings.Save(new AppSettings { ScheduleRunner = ScheduleRunner.Background });
        var now = Monday;
        var main = new MainViewModel(_test.Services) { SchedulerExecutable = "BorgStudio.exe", Clock = () => now };
        await main.CheckBorgCommand.ExecuteAsync(null);
        main.ScheduleTick();
        await main.DueJobsRun;
        _test.SystemState.OnBattery = true;
        now = Monday.AddMinutes(30);
        main.ScheduleTick();
        await main.DueJobsRun;

        var waiting = Assert.Single(main.Tasks.Items);
        Assert.Equal(RunningTaskState.Waiting, waiting.State);
        Assert.Equal(BorgTexts.Format(Strings.TaskBackup, "Documents"), waiting.Title);
        Assert.Equal(BorgTexts.Format(Strings.TaskWaiting, BorgTexts.SkipReason(BackupSkipReason.OnBattery),
            Monday.AddMinutes(45).ToString("t", System.Globalization.CultureInfo.CurrentCulture)), waiting.Status);
        Assert.False(main.Tasks.IsBusy);
        Assert.Equal(Strings.TasksOne, main.Tasks.SummaryText);

        // Stopped from the list: not tried again 15 minutes later, though plugged in.
        waiting.CancelCommand.Execute(null);
        Assert.Empty(main.Tasks.Items);
        _test.SystemState.OnBattery = false;
        now = Monday.AddMinutes(46);
        main.ScheduleTick();
        await main.DueJobsRun;
        Assert.DoesNotContain(_test.Borg.Calls, call => call.Arguments[0] == "create");
    }

    [Fact]
    public async Task Runs_of_the_task_scheduler_leave_word_while_they_wait_and_work()
    {
        var job = Job(new RunConditions { NotOnBattery = true });
        _test.Jobs.Save([job]);
        _test.SystemState.OnBattery = true;
        var now = Monday.AddMinutes(30);
        var seen = new List<RunMarker>();
        string ReadMarker() => File.ReadAllText(Assert.Single(Directory.GetFiles(Markers.Directory, "*.json")));
        Task Wait(TimeSpan time, CancellationToken _)
        {
            seen.Add(JsonSerializer.Deserialize<RunMarker>(ReadMarker())!);
            now += time;
            _test.SystemState.OnBattery = false;
            return Task.CompletedTask;
        }
        _test.Borg.AfterCreateProgress = _ => seen.Add(JsonSerializer.Deserialize<RunMarker>(ReadMarker())!);

        var runner = new ScheduledJobRunner(_test.Services, MainViewModel.CreateScheduledAccess(_test.Services));
        await runner.RunWhenAllowedAsync(job.Id, Wait, () => now);

        Assert.Equal(BorgTexts.SkipReason(BackupSkipReason.OnBattery), seen[0].Waiting);
        Assert.Null(seen[^1].Waiting);
        Assert.Equal(("Documents", RunMarkerKind.Backup, job.Id), (seen[^1].Name, seen[^1].Kind, seen[^1].JobId));
        Assert.Empty(Directory.GetFiles(Markers.Directory, "*.json"));
    }

    [Fact]
    public async Task The_window_lists_runs_of_other_processes_and_forgets_ended_ones()
    {
        _test.Jobs.Save([Job()]);
        var main = new MainViewModel(_test.Services);
        await main.CheckBorgCommand.ExecuteAsync(null);
        // Another process that is alive (this test's parent will do), and one long gone.
        var other = Process.GetProcesses().First(process => process.Id != Environment.ProcessId && CanSeeStart(process));
        Directory.CreateDirectory(Markers.Directory);
        File.WriteAllText(Path.Combine(Markers.Directory, "alive.json"), JsonSerializer.Serialize(
            new RunMarker(RunMarkerKind.Backup, "Documents", _repository.Id, Guid.NewGuid(), DateTimeOffset.Now, other.Id)));
        File.WriteAllText(Path.Combine(Markers.Directory, "gone.json"), JsonSerializer.Serialize(
            new RunMarker(RunMarkerKind.Check, "Disk", _repository.Id, null, DateTimeOffset.Now, int.MaxValue - 7)));

        main.RefreshOtherRuns();

        var elsewhere = Assert.Single(main.Tasks.Items);
        Assert.Equal(RunningTaskState.Elsewhere, elsewhere.State);
        Assert.Equal(BorgTexts.Format(Strings.TaskBackup, "Documents"), elsewhere.Title);
        Assert.Equal("Disk", elsewhere.Detail);
        Assert.StartsWith(Strings.TaskElsewhere.Split('{')[0], elsewhere.Status, StringComparison.Ordinal);
        Assert.False(elsewhere.CanCancel);
        Assert.True(elsewhere.CanShow);
        Assert.False(File.Exists(Path.Combine(Markers.Directory, "gone.json")));

        File.Delete(Path.Combine(Markers.Directory, "alive.json"));
        main.RefreshOtherRuns();
        Assert.Empty(main.Tasks.Items);
    }

    private static bool CanSeeStart(Process process)
    {
        try
        {
            return process.StartTime < DateTime.Now && !process.HasExited;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    [Fact]
    public async Task An_export_window_shows_its_work_and_comes_to_the_front_from_the_list()
    {
        _test.Jobs.Save([Job()]);
        _test.Borg.Archives.Add(("Documents-host-2026-11-01", new DateTime(2026, 11, 1, 20, 0, 0)));
        var main = new MainViewModel(_test.Services);
        await main.CheckBorgCommand.ExecuteAsync(null);
        main.ListedRepository = main.Repositories[0];
        main.SelectedDetailsTab = MainViewModel.ArchivesTab;
        await main.ArchivesLoad;
        main.SelectedArchive = main.Archives[0];
        main.SelectedArchives.Add(main.Archives[0]);
        Directory.CreateDirectory(_test.Directory.Combine("exports"));
        _test.Dialogs.ExportFile = _test.Directory.Combine("exports", "backup.tar");
        RunningTaskViewModel? seen = null;
        _test.Borg.DuringExport = () =>
        {
            seen = main.Tasks.Items.Single(task => task.Title == BorgTexts.Format(Strings.TaskExport, "backup.tar"));
            seen.ShowCommand.Execute(null);
        };

        await main.ExportArchiveCommand.ExecuteAsync(null);

        Assert.NotNull(seen);
        Assert.Same(_test.Dialogs.Exports[^1], Assert.Single(_test.Dialogs.BroughtToFront));
        Assert.Empty(main.Tasks.Items);
    }
}
