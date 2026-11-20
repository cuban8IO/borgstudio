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

/// <summary>Restore tests with the automatic checks and by hand – with fake borg.</summary>
public sealed class RestoreTestFlowTests : IDisposable
{
    private readonly TestServices _test = new();
    private RepositoryConfig _repository;

    public RestoreTestFlowTests()
    {
        _repository = new RepositoryConfig
        {
            Id = Guid.NewGuid(),
            Name = "Disk",
            ProviderId = "local",
            ProviderValues = new Dictionary<string, string> { ["path"] = _test.Directory.Combine("repo") },
            PassphraseMode = PassphraseMode.None,
            EncryptionMode = "none",
            Check = new CheckPolicy { AfterBackup = true },
        };
        Directory.CreateDirectory(_test.Directory.Combine("repo"));
        Directory.CreateDirectory(Source);
        RepositoryChecks.TemporaryRoot = _test.Directory.Combine("temp");
        Directory.CreateDirectory(RepositoryChecks.TemporaryRoot);
        // Restored files come back at their size (unless a test says otherwise).
        Borg.ExtractedContent = (_, size) => new byte[size];
    }

    public void Dispose() => _test.Dispose();

    private string Source => _test.Directory.Combine("Documents");

    private FakeBorg Borg => _test.Borg;

    private RestoreTestStore Store => RestoreTestStore.ForDataDirectory(_test.Directory.Path);

    private BackupJob Job(RestoreTestMode mode) => new()
    {
        Id = Guid.NewGuid(),
        Name = "Documents",
        RepositoryId = _repository.Id,
        Sources = [Source],
        Exclusions = new BackupExclusions { CachesAndTemp = false },
        RestoreTest = mode,
        RestoreTestFiles = 2,
    };

    private void Configure(bool enabled, bool webhook = false) => _test.Settings.Save(new AppSettings
    {
        RestoreTest = new RestoreTestSettings { Enabled = enabled },
        Notifications = webhook
            ? new NotificationSettings { Webhook = new WebhookChannel { Enabled = true, Url = "https://example.com/hook" } }
            : new NotificationSettings(),
    });

    private async Task<MainViewModel> StartAsync(BackupJob job)
    {
        _test.Store.Save([_repository]);
        _test.Jobs.Save([job]);
        var main = new MainViewModel(_test.Services);
        await main.CheckBorgCommand.ExecuteAsync(null);
        main.ListedRepository = main.Repositories[0];
        main.SelectedDetailsTab = MainViewModel.JobsTab;
        main.SelectedJob = main.RepositoryJobs[0];
        return main;
    }

    private IEnumerable<string> Commands => Borg.Commands.Where(command => command is "create" or "check" or "list" or "extract");

    [Fact]
    public async Task An_automatic_check_restores_a_sample_of_the_newest_archive()
    {
        Configure(enabled: true);
        var job = Job(RestoreTestMode.Read);
        var main = await StartAsync(job);
        Borg.Calls.Clear();

        await main.RunJobCommand.ExecuteAsync(null);

        Assert.Equal(["create", "check", "list", "extract"], Commands);
        var extract = Borg.Calls.Single(call => call.Arguments[0] == "extract").Arguments;
        Assert.Contains(extract, argument => argument.EndsWith("::Documents-host-2026-11-06T10:00:00", StringComparison.Ordinal));
        Assert.Equal(2, extract.Count(argument => argument.StartsWith("pp:", StringComparison.Ordinal)));
        var test = Assert.Single(Store.Get(job.Id));
        Assert.Equal(RestoreTestOutcome.Ok, test.Outcome);
        Assert.True(test.Automatic);
        Assert.Equal(2, test.Files.Count);
        Assert.Equal("Documents-host-2026-11-06T10:00:00", test.Archive);
        Assert.True(main.SelectedJob!.HasRestoreTest);
        // The sample is gone again.
        Assert.Empty(Directory.GetFileSystemEntries(RepositoryChecks.TemporaryRoot));
    }

    [Fact]
    public async Task A_failed_restore_test_is_reported_and_critical()
    {
        Configure(enabled: true, webhook: true);
        Borg.ExtractedContent = null; // "restored" – not the files' sizes
        var job = Job(RestoreTestMode.Read);
        var main = await StartAsync(job);

        await main.RunJobCommand.ExecuteAsync(null);

        var test = Store.Get(job.Id)[0];
        Assert.Equal(RestoreTestOutcome.Failed, test.Outcome);
        Assert.All(test.Files, file => Assert.Equal(RestoredFileResult.Missing, file.Result));
        Assert.True(main.SelectedJob!.RestoreTestFailed);
        var posted = _test.Http.Requests.Select(request => JsonNode.Parse(request.Body)!).Single(body => body["restoreTest"] is not null);
        Assert.Equal("check-failed", (string?)posted["event"]);
        Assert.Equal(BorgTexts.Format(Strings.NotifyTitleRestoreTestFailed, "Documents"), (string?)posted["title"]);

        main.ShowOverviewCommand.Execute(null);
        var row = Assert.Single(main.Dashboard!.Rows);
        Assert.True(row.IsCritical);
        Assert.Equal(BorgTexts.Format(Strings.DashRestoreTestFailed, "Documents"), row.HealthText);
    }

    [Fact]
    public async Task Switched_off_in_the_settings_or_for_the_job_nothing_is_restored()
    {
        Configure(enabled: false);
        var job = Job(RestoreTestMode.Read);
        var main = await StartAsync(job);
        await main.RunJobCommand.ExecuteAsync(null);
        Assert.DoesNotContain("extract", Borg.Commands);

        Configure(enabled: true);
        var off = Job(RestoreTestMode.Off);
        main = await StartAsync(off);
        RepositoryCheckStore.ForDataDirectory(_test.Directory.Path).Remove(_repository.Id);
        await main.RunJobCommand.ExecuteAsync(null);
        Assert.DoesNotContain("extract", Borg.Commands);
        Assert.Empty(Store.Get(off.Id));
    }

    [Fact]
    public async Task A_check_by_hand_restores_nothing()
    {
        Configure(enabled: true);
        var main = await StartAsync(Job(RestoreTestMode.Read));
        main.SelectedDetailsTab = MainViewModel.OverviewTab;
        Borg.Calls.Clear();

        await main.CheckRepositoryCommand.ExecuteAsync(null);

        Assert.Equal(["check"], Commands);
    }

    [Fact]
    public async Task The_button_tests_a_job_by_hand_even_without_a_mode()
    {
        Configure(enabled: false);
        var job = Job(RestoreTestMode.Off) with
        {
            LastRun = new BackupRunSummary
            {
                Started = DateTimeOffset.Now.AddDays(-1), Duration = TimeSpan.FromMinutes(1), Outcome = BackupOutcome.Succeeded,
                ArchiveName = "Documents-host-2026-11-05T10:00:00",
            },
        };
        var main = await StartAsync(job);
        Borg.Calls.Clear();

        await main.TestRestoreCommand.ExecuteAsync(null);

        Assert.Equal(["list", "extract"], Commands);
        var test = Assert.Single(Store.Get(job.Id));
        Assert.Equal(RestoreTestMode.Compare, test.Mode);
        Assert.False(test.Automatic);
        Assert.Equal(RestoreTestOutcome.Ok, test.Outcome);
        Assert.True(main.CurrentBackup!.IsRestoreTestOnly);
        Assert.Equal(BorgTexts.Format(Strings.RestoreTestTitle, "Documents"), main.CurrentBackup.Title);
        Assert.Equal(Strings.RestoreTestOk, main.CurrentBackup.StatusText);
        Assert.False(main.IsBackupRunning);
    }

    [Fact]
    public async Task Without_a_backup_the_test_cannot_run()
    {
        var main = await StartAsync(Job(RestoreTestMode.Compare));
        Borg.Calls.Clear();

        await main.TestRestoreCommand.ExecuteAsync(null);

        Assert.Empty(Commands);
        var test = Assert.Single(Store.Get(main.SelectedJob!.Job.Id));
        Assert.Equal(RestoreTestOutcome.NotRun, test.Outcome);
        Assert.Equal(Strings.RestoreTestNoArchive, test.Error);
    }

    [Fact]
    public async Task The_whole_archive_is_read_without_writing()
    {
        Configure(enabled: true);
        var job = Job(RestoreTestMode.WholeArchive);
        var main = await StartAsync(job);
        Borg.Calls.Clear();

        await main.RunJobCommand.ExecuteAsync(null);

        var extract = Borg.Calls.Single(call => call.Arguments[0] == "extract").Arguments;
        Assert.Contains("--dry-run", extract);
        Assert.DoesNotContain(extract, argument => argument.StartsWith("pp:", StringComparison.Ordinal));
        Assert.Equal(RestoreTestOutcome.Ok, Store.Get(job.Id)[0].Outcome);
    }

    [Fact]
    public async Task The_editors_keep_the_settings()
    {
        var main = await StartAsync(Job(RestoreTestMode.Off));
        var editor = new BackupJobEditorViewModel(_repository, FakeBorg.Installation, _test.Dialogs, main.SelectedJob!.Job);
        Assert.Equal(RestoreTestMode.Off, editor.SelectedRestoreTest.Value);
        Assert.False(editor.IsRestoreTestSample);
        editor.SelectedRestoreTest = editor.RestoreTestOptions.First(option => option.Value == RestoreTestMode.Compare);
        editor.RestoreTestFiles = 50;
        editor.SaveCommand.Execute(null);
        Assert.Equal((RestoreTestMode.Compare, 50), (editor.Result!.RestoreTest, editor.Result.RestoreTestFiles));

        var settings = new SettingsViewModel(main, _test.Dialogs);
        Assert.False(settings.RestoreTestEnabled);
        Assert.Equal(1024, settings.RestoreTestTotalMegabytes);
        Assert.Equal(100, settings.RestoreTestFileMegabytes);
        Assert.True(settings.RestoreTestSizeAndTime);
        settings.RestoreTestEnabled = true;
        settings.RestoreTestFileMegabytes = 20;
        settings.RestoreTestSizeOnly = true;

        var saved = _test.Settings.Load().RestoreTest;
        Assert.True(saved.Enabled);
        Assert.Equal(20L * 1024 * 1024, saved.MaxFileBytes);
        Assert.Equal(RestoreTestComparison.SizeOnly, saved.Comparison);
    }
}
