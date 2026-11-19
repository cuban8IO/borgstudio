using System.Text.Json.Nodes;
using BorgStudio.App.Resources;
using BorgStudio.App.Services;
using BorgStudio.App.ViewModels;
using BorgStudio.Core.Backups;
using BorgStudio.Core.Notifications;
using BorgStudio.Core.Repositories;
using BorgStudio.Core.Settings;
using BorgStudio.Core.Storage;
using BorgStudio.Tests.TestSupport;

namespace BorgStudio.Tests.ViewModels;

/// <summary>The daily check: when it is due, overdue jobs, the summary, and who starts it.</summary>
public sealed class DailyCheckFlowTests : IDisposable
{
    // Thursday, 19 November 2026.
    private static readonly DateTime Today = new(2026, 11, 19);

    private readonly TestServices _test = new();
    private readonly RepositoryConfig _repository;

    public DailyCheckFlowTests()
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
        _test.Store.Save([_repository]);
    }

    public void Dispose() => _test.Dispose();

    private BackupRunHistory History => new(_test.Directory.Combine("history"));

    private DailyCheckState State => DailyCheckState.ForDataDirectory(_test.Directory.Path);

    private BackupJob Job(string name = "Documents", int overdueDays = 0) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        RepositoryId = _repository.Id,
        OverdueDays = overdueDays,
    };

    private static BackupRunSummary Run(DateTime started, BackupOutcome outcome = BackupOutcome.Succeeded, string? message = null) => new()
    {
        Started = new DateTimeOffset(started),
        Duration = TimeSpan.FromMinutes(2),
        Outcome = outcome,
        OriginalSize = 1024 * 1024,
        DeduplicatedSize = 1024,
        Message = message,
    };

    private static NotificationSettings Webhook(bool summary = true, bool whenAllWell = true) => new()
    {
        Webhook = new WebhookChannel { Enabled = true, Url = "https://example.com/hook", DailySummary = summary },
        SummaryWhenAllWell = whenAllWell,
    };

    private List<JsonNode> Posted => [.. _test.Http.Requests.Select(request => JsonNode.Parse(request.Body)!)];

    // --- When ---

    [Fact]
    public void The_check_is_due_once_per_day_and_made_up_for_once()
    {
        var eight = new TimeOnly(8, 0);
        // Never checked: the first check is the next 08:00.
        Assert.False(State.IsDue(eight, Today.AddHours(15)));
        Assert.False(State.IsDue(eight, Today.AddHours(20)));
        Assert.True(State.IsDue(eight, Today.AddDays(1).AddHours(8)));
        State.Record(Today.AddDays(1).AddHours(8));
        Assert.False(State.IsDue(eight, Today.AddDays(1).AddHours(23)));

        // The computer was off for two days: one check when it is back.
        Assert.True(State.IsDue(eight, Today.AddDays(4).AddHours(7)));
        State.Record(Today.AddDays(4).AddHours(7));
        Assert.False(State.IsDue(eight, Today.AddDays(4).AddHours(7.5)));
        Assert.True(State.IsDue(eight, Today.AddDays(4).AddHours(8)));
    }

    [Fact]
    public void A_job_is_overdue_after_its_days_without_success()
    {
        var backedUp = Job("Backed up", overdueDays: 3);
        var never = Job("Never", overdueDays: 3);
        var off = Job("Off");
        History.Add(backedUp.Id, Run(Today.AddDays(-4)));
        History.Add(backedUp.Id, Run(Today.AddDays(-1), BackupOutcome.Failed));

        Assert.Equal(["Backed up"], State.Overdue([backedUp, never, off], History, Today).Select(entry => entry.Job.Name));
        // A job that never backed up counts from when the check first saw it.
        Assert.Equal(["Backed up", "Never"], State.Overdue([backedUp, never, off], History, Today.AddDays(4)).Select(entry => entry.Job.Name));
        History.Add(backedUp.Id, Run(Today.AddDays(4), BackupOutcome.Warnings));
        Assert.Equal(["Never"], State.Overdue([backedUp, never], History, Today.AddDays(5)).Select(entry => entry.Job.Name));
    }

    [Fact]
    public void An_overdue_job_is_reported_once_until_it_backs_up()
    {
        var policy = NotificationPolicy.ForDataDirectory(_test.Directory.Path);
        var job = Guid.NewGuid();

        Assert.Null(policy.DecideOverdue(job, false));
        Assert.Equal(new NotificationDecision(NotificationEvent.Overdue, false), policy.DecideOverdue(job, true));
        Assert.True(policy.DecideOverdue(job, true)!.IsRepeat);
        Assert.Null(policy.DecideOverdue(job, false));
        Assert.False(policy.DecideOverdue(job, true)!.IsRepeat);
    }

    // --- What it sends ---

    [Fact]
    public async Task The_summary_lists_runs_storage_and_overdue_jobs()
    {
        var documents = Job("Documents");
        var photos = Job("Photos", overdueDays: 2);
        _test.Jobs.Save([documents, photos]);
        _test.Settings.Save(new AppSettings { Notifications = Webhook() });
        State.Record(Today.AddDays(-1).AddHours(8));
        History.Add(documents.Id, Run(Today.AddDays(-2)));
        History.Add(documents.Id, Run(Today.AddHours(2), BackupOutcome.Failed, "disk gone"));
        History.Add(photos.Id, Run(Today.AddDays(-5)));
        StorageUsageStore.ForDataDirectory(_test.Directory.Path).Set(_repository.Id,
            new StorageUsage { Measured = DateTimeOffset.Now, Source = StorageSource.Disk, Total = 100, Free = 5 });

        await new DailyCheck(_test.Services).RunAsync(Today.AddHours(8), _test.Vault);

        // The overdue job alone, then the summary.
        Assert.Equal(["overdue", "summary"], Posted.Select(body => (string?)body["event"]));
        Assert.Equal(BorgTexts.Format(Strings.NotifyTitleOverdue, "Photos"), (string?)Posted[0]["title"]);
        var summary = (string)Posted[1]["message"]!;
        Assert.Equal(BorgTexts.Format(Strings.SummaryTitleProblems, 3), (string?)Posted[1]["title"]);
        Assert.Contains("Documents: " + BorgTexts.BackupOutcome(BackupOutcome.Failed) + " – disk gone", summary);
        Assert.DoesNotContain(Today.AddDays(-2).ToString("g", System.Globalization.CultureInfo.CurrentCulture), summary);
        Assert.Contains($"• Disk: ", summary);
        Assert.Contains(Strings.SummaryOverdue, summary);
        Assert.Equal(Today.AddHours(8), State.LastCheck!.Value.LocalDateTime);

        // The next day: the overdue job is not reported again, the summary is.
        _test.Http.Requests.Clear();
        await new DailyCheck(_test.Services).RunAsync(Today.AddDays(1).AddHours(8), _test.Vault);
        Assert.Equal(["summary"], Posted.Select(body => (string?)body["event"]));
        Assert.Contains(Strings.SummaryNoRuns, (string)Posted[0]["message"]!);
    }

    [Fact]
    public async Task A_quiet_day_is_left_out_if_wanted_and_channels_without_summary_get_none()
    {
        var job = Job();
        _test.Jobs.Save([job]);
        History.Add(job.Id, Run(Today.AddHours(2)));
        State.Record(Today);

        _test.Settings.Save(new AppSettings { Notifications = Webhook(whenAllWell: false) });
        await new DailyCheck(_test.Services).RunAsync(Today.AddHours(8), _test.Vault);
        Assert.Empty(_test.Http.Requests);

        _test.Settings.Save(new AppSettings { Notifications = Webhook(summary: false) });
        await new DailyCheck(_test.Services).RunAsync(Today.AddDays(1).AddHours(8), _test.Vault);
        Assert.Empty(_test.Http.Requests);

        _test.Settings.Save(new AppSettings { Notifications = Webhook() });
        State.Record(Today);
        await new DailyCheck(_test.Services).RunAsync(Today.AddHours(8), _test.Vault);
        Assert.Equal(Strings.SummaryTitleAllWell, (string?)Assert.Single(Posted)["title"]);
    }

    // --- Who starts it ---

    [Fact]
    public async Task The_system_scheduler_gets_the_daily_check_only_when_there_is_something_to_do()
    {
        _test.Jobs.Save([Job()]);
        var main = new MainViewModel(_test.Services) { SchedulerExecutable = "BorgStudio.exe" };
        await main.SchedulesApplied;
        Assert.Null(_test.Scheduler.DailyCheck);

        var window = new NotificationsViewModel(main) { WebhookEnabled = true, WebhookUrl = "https://example.com/hook", WebhookDailySummary = true };
        window.DailyTime = new TimeSpan(7, 30, 0);
        window.SaveCommand.Execute(null);
        await main.SchedulesApplied;
        Assert.Equal(new TimeOnly(7, 30), _test.Scheduler.DailyCheck);

        // In the background BorgStudio does it itself.
        main.SetScheduleRunner(ScheduleRunner.Background);
        await main.SchedulesApplied;
        Assert.Null(_test.Scheduler.DailyCheck);
    }

    [Fact]
    public async Task In_the_background_the_app_runs_the_check_at_its_time()
    {
        _test.Jobs.Save([Job()]);
        _test.Settings.Save(new AppSettings { ScheduleRunner = ScheduleRunner.Background, Notifications = Webhook() });
        var now = Today.AddHours(7);
        var main = new MainViewModel(_test.Services) { SchedulerExecutable = "BorgStudio.exe", Clock = () => now };

        main.ScheduleTick();
        await main.DailyCheckRun;
        Assert.Empty(_test.Http.Requests);

        now = Today.AddHours(8).AddMinutes(1);
        main.ScheduleTick();
        await main.DailyCheckRun;
        Assert.Equal("summary", (string?)Assert.Single(Posted)["event"]);

        main.ScheduleTick();
        await main.DailyCheckRun;
        Assert.Single(_test.Http.Requests);
    }

    [Fact]
    public void The_job_editor_keeps_the_overdue_days()
    {
        var editor = new BackupJobEditorViewModel(_repository, FakeBorg.Installation, _test.Dialogs, Job(overdueDays: 4) with { Sources = ["C:\\Data"] });
        Assert.Equal(4, editor.OverdueDays);
        editor.OverdueDays = 7;
        editor.SaveCommand.Execute(null);
        Assert.Equal(7, editor.Result!.OverdueDays);
    }
}
