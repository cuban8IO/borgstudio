using BorgStudio.App.Resources;
using BorgStudio.App.Services;
using BorgStudio.App.ViewModels;
using BorgStudio.Core.Backups;
using BorgStudio.Core.Repositories;
using BorgStudio.Core.Settings;
using BorgStudio.Tests.TestSupport;

namespace BorgStudio.Tests.ViewModels;

/// <summary>Scheduled runs held back by their job's conditions – without window and in the background.</summary>
public sealed class RunConditionFlowTests : IDisposable
{
    // Monday, 16 November 2026.
    private static readonly DateTime Monday = new(2026, 11, 16, 10, 0, 0);

    private static readonly BackupSchedule HalfPastTen = new() { Kind = ScheduleKind.Daily, Time = new TimeOnly(10, 30) };

    private readonly TestServices _test = new();
    private readonly RepositoryConfig _repository;

    public RunConditionFlowTests()
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

    private int Backups => _test.Borg.Calls.Count(call => call.Arguments[0] == "create");

    private BackupJob Job() => new()
    {
        Id = Guid.NewGuid(),
        Name = "Documents",
        RepositoryId = _repository.Id,
        Sources = [Source],
        Exclusions = new BackupExclusions { CachesAndTemp = false },
        Schedules = [HalfPastTen],
        CatchUp = false,
        Conditions = new RunConditions { NotOnBattery = true },
    };

    private ScheduledJobRunner Runner => new(_test.Services, MainViewModel.CreateScheduledAccess(_test.Services));

    [Fact]
    public async Task Without_window_a_run_waits_until_the_condition_is_met()
    {
        var job = Job();
        _test.Jobs.Save([job]);
        _test.SystemState.OnBattery = true;
        var now = Monday.AddMinutes(30);
        var waits = new List<TimeSpan>();
        Task Wait(TimeSpan time, CancellationToken _)
        {
            waits.Add(time);
            now += time;
            if (waits.Count == 3)
                _test.SystemState.OnBattery = false; // plugged in
            return Task.CompletedTask;
        }

        var result = await Runner.RunWhenAllowedAsync(job.Id, Wait, () => now);

        Assert.Equal(3, waits.Count);
        Assert.All(waits, time => Assert.Equal(RunConditions.RetryInterval, time));
        Assert.Equal(BackupOutcome.Succeeded, result.Summary.Outcome);
        Assert.Equal(1, Backups);
    }

    [Fact]
    public async Task Held_back_until_the_next_time_it_is_skipped_with_its_reason()
    {
        var job = Job();
        _test.Jobs.Save([job]);
        _test.Settings.Save(new AppSettings
        {
            Notifications = new Core.Notifications.NotificationSettings
            {
                Webhook = new Core.Notifications.WebhookChannel { Enabled = true, Url = "https://example.com/hook" },
            },
        });
        _test.SystemState.OnBattery = true;
        var now = Monday.AddMinutes(30);
        Task Wait(TimeSpan time, CancellationToken _)
        {
            now += time;
            return Task.CompletedTask;
        }

        var result = await Runner.RunWhenAllowedAsync(job.Id, Wait, () => now);

        Assert.Equal(0, Backups);
        Assert.Equal((BackupOutcome.Skipped, BackupSkipReason.OnBattery), (result.Summary.Outcome, result.Summary.SkipReason));
        // Until shortly before the next time (tomorrow 10:30): a day of looks.
        Assert.InRange(now, Monday.AddDays(1).AddMinutes(15), Monday.AddDays(1).AddMinutes(30));
        Assert.Equal(BackupSkipReason.OnBattery, _test.Jobs.Load()[0].LastRun!.SkipReason);
        Assert.Contains(_test.Http.Requests, request => request.Body.Contains("\"skipped\"", StringComparison.Ordinal));
        Assert.Equal(Strings.SkipOnBattery, BorgTexts.SkipReason(BackupSkipReason.OnBattery));
    }

    [Fact]
    public async Task By_hand_the_conditions_do_not_apply()
    {
        var job = Job();
        _test.Jobs.Save([job]);
        _test.SystemState.OnBattery = true;

        var result = await Runner.RunAsync(job.Id);

        Assert.Equal(BackupOutcome.Succeeded, result.Summary.Outcome);
    }

    [Fact]
    public async Task The_background_looks_again_every_fifteen_minutes()
    {
        var job = Job();
        _test.Jobs.Save([job]);
        _test.Settings.Save(new AppSettings { ScheduleRunner = ScheduleRunner.Background });
        var now = Monday;
        var main = new MainViewModel(_test.Services) { SchedulerExecutable = "BorgStudio.exe", Clock = () => now };
        await main.CheckBorgCommand.ExecuteAsync(null);
        async Task TickAt(DateTime time)
        {
            now = time;
            main.ScheduleTick();
            await main.DueJobsRun;
        }

        await TickAt(Monday);
        _test.SystemState.OnBattery = true;
        await TickAt(Monday.AddMinutes(30));
        Assert.Equal(0, Backups);
        var looks = _test.SystemState.Looks;

        // Not yet 15 minutes later: nothing.
        await TickAt(Monday.AddMinutes(40));
        Assert.Equal(looks, _test.SystemState.Looks);

        // 15 minutes later, plugged in: the backup runs.
        _test.SystemState.OnBattery = false;
        await TickAt(Monday.AddMinutes(46));
        Assert.Equal(1, Backups);
        Assert.Equal(BackupOutcome.Succeeded, main.Repositories.Count == 1 ? _test.Jobs.Load()[0].LastRun!.Outcome : default);
    }

    [Fact]
    public async Task The_editor_and_the_job_list_show_the_conditions()
    {
        var job = Job() with { Conditions = new RunConditions() };
        var editor = new BackupJobEditorViewModel(_repository, FakeBorg.Installation, _test.Dialogs, job);
        Assert.False(editor.NotOnBattery);

        editor.NotOnBattery = true;
        editor.WiredOnly = true;
        editor.SaveCommand.Execute(null);

        var conditions = editor.Result!.Conditions;
        Assert.True(conditions.NotOnBattery && conditions.WiredOnly && !conditions.NotMetered && !conditions.TargetReachable);
        var item = new BackupJobItemViewModel(editor.Result);
        Assert.True(item.HasConditions);
        Assert.Equal(BorgTexts.Format(Strings.JobConditions, Strings.ConditionNotOnBattery + ", " + Strings.ConditionWiredOnly), item.ConditionsText);
        Assert.False(new BackupJobItemViewModel(editor.Result with { Schedules = [] }).HasConditions);
    }
}
