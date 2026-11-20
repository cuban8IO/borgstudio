using System.Text.Json.Nodes;
using BorgStudio.App.Resources;
using BorgStudio.App.Services;
using BorgStudio.App.ViewModels;
using BorgStudio.Core.Backups;
using BorgStudio.Core.Checks;
using BorgStudio.Core.Notifications;
using BorgStudio.Core.Repositories;
using BorgStudio.Core.Settings;
using BorgStudio.Tests.TestSupport;

namespace BorgStudio.Tests.ViewModels;

/// <summary>Checks that run by themselves: after backups, by schedule (without window and in the background) – with fake borg.</summary>
public sealed class CheckScheduleFlowTests : IDisposable
{
    private static readonly BackupSchedule FirstSunday = new() { Kind = ScheduleKind.MonthlyWeekday, Week = 1, Weekday = DayOfWeek.Sunday, Time = new TimeOnly(3, 0) };

    private readonly TestServices _test = new();
    private RepositoryConfig _repository;

    public CheckScheduleFlowTests()
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
        Directory.CreateDirectory(RepositoryFolder);
        Directory.CreateDirectory(Source);
        RepositoryChecks.LockPoll = TimeSpan.FromMilliseconds(20);
    }

    public void Dispose() => _test.Dispose();

    private string RepositoryFolder => _test.Directory.Combine("repo");

    private string Source => _test.Directory.Combine("Documents");

    private FakeBorg Borg => _test.Borg;

    private RepositoryCheckStore Store => RepositoryCheckStore.ForDataDirectory(_test.Directory.Path);

    private int CheckCalls => Borg.Calls.Count(call => call.Arguments[0] == "check");

    private List<string?> PostedEvents => [.. _test.Http.Requests.Select(request => (string?)JsonNode.Parse(request.Body)!["event"])];

    private BackupJob Job(params BackupSchedule[] schedules) => new()
    {
        Id = Guid.NewGuid(),
        Name = "Documents",
        RepositoryId = _repository.Id,
        Sources = [Source],
        Exclusions = new BackupExclusions { CachesAndTemp = false },
        Schedules = [.. schedules],
    };

    private void Save(CheckPolicy policy, params BackupJob[] jobs)
    {
        _repository = _repository with { Check = policy };
        _test.Store.Save([_repository]);
        _test.Jobs.Save(jobs);
    }

    private void WithWebhook() => _test.Settings.Save(new AppSettings
    {
        Notifications = new NotificationSettings { Webhook = new WebhookChannel { Enabled = true, Url = "https://example.com/hook" } },
    });

    private async Task<MainViewModel> StartAsync()
    {
        var main = new MainViewModel(_test.Services) { SchedulerExecutable = "BorgStudio.exe" };
        await main.CheckBorgCommand.ExecuteAsync(null);
        await main.SchedulesApplied;
        main.ListedRepository = main.Repositories[0];
        Borg.Calls.Clear();
        return main;
    }

    [Fact]
    public async Task A_backup_by_hand_is_followed_by_a_check_when_one_is_due()
    {
        var job = Job();
        Save(new CheckPolicy { AfterBackup = true, IntervalDays = 30 }, job);
        var main = await StartAsync();
        main.SelectedDetailsTab = MainViewModel.JobsTab;
        main.SelectedJob = main.RepositoryJobs[0];

        await main.RunJobCommand.ExecuteAsync(null);

        Assert.Equal(["create", "check"], Borg.Commands.Where(command => command is "create" or "check"));
        var check = Assert.Single(Store.Get(_repository.Id));
        Assert.True(check.Automatic);
        Assert.Equal(CheckOutcome.Ok, check.Outcome);
        Assert.True(main.SelectedRepository!.IsCheckOk);

        // Checked just now: the next backup goes without.
        await main.RunJobCommand.ExecuteAsync(null);
        Assert.Equal(1, CheckCalls);
    }

    [Fact]
    public async Task Without_the_option_a_backup_checks_nothing()
    {
        var job = Job();
        Save(new CheckPolicy(), job);
        var main = await StartAsync();
        main.SelectedDetailsTab = MainViewModel.JobsTab;
        main.SelectedJob = main.RepositoryJobs[0];

        await main.RunJobCommand.ExecuteAsync(null);

        Assert.Equal(0, CheckCalls);
        Assert.Empty(Store.Get(_repository.Id));
    }

    [Fact]
    public async Task A_scheduled_backup_checks_after_it_and_reports_damage()
    {
        WithWebhook();
        var job = Job(new BackupSchedule());
        Save(new CheckPolicy { AfterBackup = true }, job);
        Borg.CheckProblems.Add("Index object count mismatch.");

        await new ScheduledJobRunner(_test.Services, MainViewModel.CreateScheduledAccess(_test.Services)).RunAsync(job.Id);

        Assert.Equal(1, CheckCalls);
        Assert.Equal(CheckOutcome.ProblemsFound, Store.Get(_repository.Id)[0].Outcome);
        Assert.Contains("check-failed", PostedEvents);
    }

    [Fact]
    public async Task A_scheduled_check_that_cannot_run_is_recorded_and_reported_once()
    {
        WithWebhook();
        Save(new CheckPolicy { Schedules = [FirstSunday] });
        _repository = _repository with { PassphraseMode = PassphraseMode.Ask, EncryptionMode = "repokey-blake2" };
        _test.Store.Save([_repository]);
        var checks = new RepositoryChecks(_test.Services);

        var first = await checks.RunScheduledAsync(_repository, MainViewModel.CreateScheduledAccess(_test.Services));
        await checks.RunScheduledAsync(_repository, MainViewModel.CreateScheduledAccess(_test.Services));

        Assert.Equal(CheckOutcome.Failed, first.Outcome);
        Assert.True(first.Automatic);
        Assert.Equal(BorgTexts.SkipReason(BackupSkipReason.PassphraseAsked), first.Error);
        Assert.Equal(0, CheckCalls);
        var posted = JsonNode.Parse(Assert.Single(_test.Http.Requests).Body)!;
        Assert.Equal("check-failed", (string?)posted["event"]);
        Assert.Equal(BorgTexts.Format(Strings.NotifyTitleCheckNotRun, "Disk"), (string?)posted["title"]);
        Assert.Equal(CheckHealthReason.NotRun, CheckHealth.Evaluate(_repository.Check, Store.Get(_repository.Id), DateTime.Now).Reason);

        // Once it runs again: "recovered".
        _repository = _repository with { PassphraseMode = PassphraseMode.None, EncryptionMode = "none" };
        var ok = await checks.RunScheduledAsync(_repository, MainViewModel.CreateScheduledAccess(_test.Services));
        Assert.Equal(CheckOutcome.Ok, ok.Outcome);
        Assert.Equal(["check-failed", "recovered"], PostedEvents);
    }

    [Fact]
    public async Task A_scheduled_check_waits_for_a_backup_on_the_repository()
    {
        Save(new CheckPolicy { Schedules = [FirstSunday] });
        var checks = new RepositoryChecks(_test.Services);
        var lockDirectory = Path.Combine(_test.Directory.Path, "locks");
        var backup = RepositoryLock.TryAcquire(lockDirectory, _repository.Id)!;
        RepositoryChecks.LockWait = TimeSpan.FromSeconds(30);
        try
        {
            var check = checks.RunScheduledAsync(_repository, MainViewModel.CreateScheduledAccess(_test.Services));
            await Task.Delay(100);
            Assert.False(check.IsCompleted);
            backup.Dispose();
            Assert.Equal(CheckOutcome.Ok, (await check).Outcome);

            // Busy for longer than it waits: not run.
            using (RepositoryLock.TryAcquire(lockDirectory, _repository.Id))
            {
                RepositoryChecks.LockWait = TimeSpan.FromMilliseconds(50);
                var busy = await checks.RunScheduledAsync(_repository, MainViewModel.CreateScheduledAccess(_test.Services));
                Assert.Equal(CheckOutcome.Failed, busy.Outcome);
                Assert.Equal(Strings.CheckBusy, busy.Error);
            }
        }
        finally
        {
            backup.Dispose();
            RepositoryChecks.LockWait = TimeSpan.FromHours(6);
        }
    }

    [Fact]
    public async Task The_background_runs_a_due_check_after_its_backups()
    {
        Save(new CheckPolicy { Schedules = [FirstSunday], CatchUp = false });
        _test.Settings.Save(new AppSettings { ScheduleRunner = ScheduleRunner.Background });
        var now = new DateTime(2026, 12, 6, 2, 59, 0);
        var main = new MainViewModel(_test.Services) { SchedulerExecutable = "BorgStudio.exe", Clock = () => now };
        await main.CheckBorgCommand.ExecuteAsync(null);
        main.ScheduleTick();
        await main.DueJobsRun;
        Assert.Equal(0, CheckCalls);

        now = new DateTime(2026, 12, 6, 3, 0, 30);
        main.ScheduleTick();
        await main.DueJobsRun;

        Assert.Equal(1, CheckCalls);
        Assert.True(Store.Get(_repository.Id)[0].Automatic);
        Assert.True(main.Repositories[0].IsCheckOk);
        // The system's scheduler has no entry for it: the app does it.
        Assert.Empty(_test.Scheduler.ScheduledChecks);
    }

    [Fact]
    public async Task The_editor_keeps_the_checks_and_the_system_gets_an_entry()
    {
        Save(new CheckPolicy());
        var main = await StartAsync();
        Assert.Empty(_test.Scheduler.ScheduledChecks);
        Assert.Equal(Strings.CheckPlanNone, main.SelectedRepository!.CheckPlanText);
        _test.Dialogs.Editor = editor =>
        {
            Assert.False(editor.CheckAfterBackup);
            editor.CheckAfterBackup = true;
            editor.CheckIntervalDays = 14;
            editor.AddCheckScheduleCommand.Execute(null);
            var schedule = Assert.Single(editor.CheckSchedules);
            Assert.True(schedule.IsMonthlyWeekday);
            schedule.SelectedWeek = schedule.WeekOptions[^1];
            schedule.SelectedWeekday = schedule.WeekdayOptions.First(option => option.Value == DayOfWeek.Saturday);
            return editor.ConfirmCommand.ExecuteAsync(null);
        };

        await main.EditRepositoryCommand.ExecuteAsync(null);
        await main.SchedulesApplied;

        var policy = _test.Store.Load().Repositories[0].Check;
        Assert.True(policy.AfterBackup);
        Assert.Equal(14, policy.IntervalDays);
        var saved = Assert.Single(policy.Schedules);
        Assert.Equal((ScheduleKind.MonthlyWeekday, BackupSchedule.Last, DayOfWeek.Saturday), (saved.Kind, saved.Week, saved.Weekday));
        Assert.Equal(_repository.Id, Assert.Single(_test.Scheduler.ScheduledChecks).Id);
        Assert.Contains(BorgTexts.Format(Strings.CheckPlanAfterBackup, 14), main.SelectedRepository!.CheckPlanText);

        // Monthly also for backup jobs: the job editor offers the same kinds.
        Assert.Contains(new ScheduleItemViewModel(new BackupSchedule(), _ => { }).KindOptions, option => option.Value == ScheduleKind.MonthlyDay);
    }

    [Fact]
    public async Task The_overview_warns_about_overdue_checks_and_ones_that_could_not_run()
    {
        Save(new CheckPolicy { AfterBackup = true, IntervalDays = 30 });
        Store.Add(_repository.Id, new CheckRecord(DateTimeOffset.Now.AddDays(-60), TimeSpan.FromMinutes(3), CheckDepth.Normal, CheckOutcome.Ok));
        var main = await StartAsync();
        Assert.Equal(Strings.CheckHealthOverdue, main.SelectedRepository!.CheckHealthText);
        main.ShowOverviewCommand.Execute(null);
        var row = Assert.Single(main.Dashboard!.Rows);
        Assert.True(row.IsWarning);
        Assert.Equal(Strings.DashCheckOverdue, row.HealthText);

        Store.Add(_repository.Id, new CheckRecord(DateTimeOffset.Now, TimeSpan.Zero, CheckDepth.Normal, CheckOutcome.Failed) { Automatic = true });
        main.RefreshChecks();
        main.ShowOverviewCommand.Execute(null);
        main.RefreshDashboard();
        Assert.Equal(Strings.DashCheckNotRun, Assert.Single(main.Dashboard!.Rows).HealthText);
    }
}
