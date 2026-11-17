using BorgStudio.App.Resources;
using BorgStudio.App.ViewModels;
using BorgStudio.Core.Backups;
using BorgStudio.Core.Repositories;
using BorgStudio.Tests.TestSupport;

namespace BorgStudio.Tests.ViewModels;

/// <summary>Schedules in the job editor, the job list and the system's task scheduler.</summary>
public sealed class ScheduleFlowTests : IDisposable
{
    private const string Pin = "123456";

    private readonly TestServices _test = new();
    private RepositoryConfig _repository;

    public ScheduleFlowTests()
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
        Directory.CreateDirectory(Source);
    }

    public void Dispose() => _test.Dispose();

    private string Source => _test.Directory.Combine("Documents");

    private FakeDialogs Dialogs => _test.Dialogs;

    private async Task<MainViewModel> StartAsync(params BackupJob[] jobs)
    {
        _test.Store.Save([_repository]);
        _test.Jobs.Save(jobs);
        var main = new MainViewModel(_test.Services) { SchedulerExecutable = "BorgStudio.exe" };
        await main.CheckBorgCommand.ExecuteAsync(null);
        await main.SchedulesApplied;
        return main;
    }

    private BackupJob Job(params BackupSchedule[] schedules) => new()
    {
        Id = Guid.NewGuid(),
        Name = "Documents",
        RepositoryId = _repository.Id,
        Sources = [Source],
        Exclusions = new BackupExclusions { CachesAndTemp = false },
        Schedules = [.. schedules],
    };

    /// <summary>Opens the editor of the selected job (or a new one) and adds a schedule.</summary>
    private Func<BackupJobEditorViewModel, Task> AddSchedule(Action<ScheduleItemViewModel> set) => editor =>
    {
        editor.AddScheduleCommand.Execute(null);
        set(editor.Schedules[^1]);
        editor.SaveCommand.Execute(null);
        return Task.CompletedTask;
    };

    [Fact]
    public async Task A_schedule_is_saved_and_handed_to_the_system()
    {
        var main = await StartAsync(Job());
        Assert.Equal(Strings.JobNotScheduled, main.SelectedJob!.ScheduleText);

        BackupJobEditorViewModel? seen = null;
        Dialogs.JobEditor = editor =>
        {
            seen = editor;
            editor.AddScheduleCommand.Execute(null);
            var schedule = editor.Schedules[0];
            schedule.SelectedKind = schedule.KindOptions.Single(option => option.Value == ScheduleKind.Weekdays);
            schedule.Time = new TimeSpan(12, 30, 0);
            // No day ticked yet.
            editor.SaveCommand.Execute(null);
            Assert.Contains(Strings.JobEditorScheduleDaysRequired, editor.Errors);
            schedule.Days.Single(day => day.Day == DayOfWeek.Monday).IsChecked = true;
            editor.CatchUp = false;
            editor.SaveCommand.Execute(null);
            return Task.CompletedTask;
        };
        await main.EditJobCommand.ExecuteAsync(null);
        await main.SchedulesApplied;

        Assert.Equal(DayOfWeek.Monday, seen!.Schedules[0].Days[0].Day);
        var saved = Assert.Single(_test.Jobs.Load());
        var schedule = Assert.Single(saved.Schedules);
        Assert.Equal(ScheduleKind.Weekdays, schedule.Kind);
        Assert.Equal(new TimeOnly(12, 30), schedule.Time);
        Assert.Equal([DayOfWeek.Monday], schedule.Days);
        Assert.False(saved.CatchUp);

        Assert.Equal(saved.Id, Assert.Single(_test.Scheduler.Scheduled).Id);
        Assert.Equal("BorgStudio.exe", _test.Scheduler.Executable);
        Assert.StartsWith(BorgTexts.Schedules(saved.Schedules), main.SelectedJob!.ScheduleText);
        Assert.Contains(Strings.JobSchedule[(Strings.JobSchedule.IndexOf('}') + 1)..Strings.JobSchedule.LastIndexOf('{')], main.SelectedJob.ScheduleText);
    }

    [Fact]
    public async Task Deleting_a_job_removes_its_schedule()
    {
        var main = await StartAsync(Job(new BackupSchedule()));
        Assert.Single(_test.Scheduler.Scheduled);

        Dialogs.ConfirmAnswers.Enqueue(true);
        await main.DeleteJobCommand.ExecuteAsync(null);
        await main.SchedulesApplied;

        Assert.Empty(_test.Scheduler.Scheduled);
        Assert.Empty(_test.Scheduler.Applied[^1]);
    }

    [Fact]
    public async Task With_a_pin_the_first_schedule_asks_to_let_scheduled_runs_open_the_vault()
    {
        _test.Vault.SetUp(Pin);
        var main = await StartAsync(Job());
        Assert.False(main.ScheduledAccess.IsEnabled);

        Dialogs.ConfirmAnswers.Enqueue(true);
        Dialogs.JobEditor = AddSchedule(_ => { });
        await main.EditJobCommand.ExecuteAsync(null);

        Assert.Contains(Strings.ScheduleAccessTitle, Dialogs.Shown);
        Assert.True(main.ScheduledAccess.IsEnabled);
        Assert.True(_test.Secrets.Secrets.ContainsKey(Core.Security.ScheduledAccess.SecretKey));

        // A second schedule does not ask again.
        Dialogs.Shown.Clear();
        Dialogs.JobEditor = AddSchedule(_ => { });
        await main.EditJobCommand.ExecuteAsync(null);
        Assert.DoesNotContain(Strings.ScheduleAccessTitle, Dialogs.Shown);
    }

    [Fact]
    public async Task Declining_keeps_the_schedule_but_not_the_key()
    {
        _test.Vault.SetUp(Pin);
        var main = await StartAsync(Job());

        Dialogs.ConfirmAnswers.Enqueue(false);
        Dialogs.JobEditor = AddSchedule(_ => { });
        await main.EditJobCommand.ExecuteAsync(null);

        Assert.False(main.ScheduledAccess.IsEnabled);
        Assert.True(Assert.Single(_test.Jobs.Load()).IsScheduled);
    }

    [Fact]
    public async Task A_passphrase_asked_every_time_can_be_stored_for_schedules()
    {
        _repository = _repository with { PassphraseMode = PassphraseMode.Ask, EncryptionMode = "repokey-blake2" };
        var main = await StartAsync(Job());

        Dialogs.ConfirmAnswers.Enqueue(true);
        Dialogs.Passphrases.Enqueue("secret");
        Dialogs.JobEditor = AddSchedule(_ => { });
        await main.EditJobCommand.ExecuteAsync(null);

        Assert.Contains(Strings.SchedulePassphraseTitle, Dialogs.Shown);
        // No PIN: into the system keychain.
        var repository = Assert.Single(_test.Store.Load().Repositories);
        Assert.Equal(PassphraseMode.Stored, repository.PassphraseMode);
        Assert.Equal("secret", _test.Secrets.Secrets[repository.SecretKey]);
    }

    [Fact]
    public async Task A_refusing_scheduler_shows_a_notice()
    {
        var main = await StartAsync(Job());
        _test.Scheduler.Error = "access denied";

        Dialogs.JobEditor = AddSchedule(_ => { });
        await main.EditJobCommand.ExecuteAsync(null);
        await main.SchedulesApplied;

        Assert.Equal(BorgTexts.Format(Strings.ScheduleApplyFailed, "access denied"), main.Notice);
        // The job keeps its schedule: the next save tries again.
        Assert.True(Assert.Single(_test.Jobs.Load()).IsScheduled);
    }
}
