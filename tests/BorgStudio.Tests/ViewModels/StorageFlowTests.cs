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

/// <summary>The storage in the overview, its settings in the repository editor and its notifications.</summary>
public sealed class StorageFlowTests : IDisposable
{
    private const long Gigabyte = 1024L * 1024 * 1024;

    private readonly TestServices _test = new();
    private readonly RepositoryConfig _repository;

    public StorageFlowTests()
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
        _test.Store.Save([_repository]);
    }

    public void Dispose() => _test.Dispose();

    private string Source => _test.Directory.Combine("Documents");

    private string RepositoryFolder => _test.Directory.Combine("repo");

    private BackupJob SaveJob(BackupRunSummary? lastRun = null)
    {
        var job = new BackupJob
        {
            Id = Guid.NewGuid(),
            Name = "Documents",
            RepositoryId = _repository.Id,
            Sources = [Source],
            Exclusions = new BackupExclusions { CachesAndTemp = false },
            LastRun = lastRun,
        };
        _test.Jobs.Save([job]);
        return job;
    }

    private async Task<MainViewModel> StartAsync()
    {
        var main = new MainViewModel(_test.Services);
        await main.CheckBorgCommand.ExecuteAsync(null);
        return main;
    }

    private StorageUsageStore Store => StorageUsageStore.ForDataDirectory(_test.Directory.Path);

    [Fact]
    public async Task A_backup_measures_the_disk_and_the_overview_shows_it()
    {
        Directory.CreateDirectory(RepositoryFolder);
        SaveJob();
        var main = await StartAsync();
        var item = main.SelectedRepository!;
        Assert.False(item.HasStorage);
        Assert.Equal(Strings.StorageNotMeasured, item.StorageText);

        await main.RunJobCommand.ExecuteAsync(null);

        Assert.Equal(StorageSource.Disk, item.Storage!.Source);
        Assert.True(item.HasStorageBar);
        Assert.Equal(item.Storage, Store.Get(_repository.Id));
        Assert.DoesNotContain("info", _test.Borg.Commands);

        // Shown again after a restart.
        Assert.Equal(item.Storage, (await StartAsync()).SelectedRepository!.Storage);
    }

    [Fact]
    public async Task Without_a_disk_to_ask_measuring_falls_back_to_borg()
    {
        var main = await StartAsync();
        var item = main.SelectedRepository!;

        await main.MeasureStorageCommand.ExecuteAsync(null);

        Assert.Equal(StorageSource.RepositoryOnly, item.Storage!.Source);
        Assert.Equal(BorgTexts.Format(Strings.StorageRepositoryOnly, BorgTexts.Size(_test.Borg.RepositorySize)), item.StorageText);
        Assert.False(item.HasStorageBar);
        // A local repository has no capacity to enter.
        Assert.False(item.HasStorageHint);
    }

    [Fact]
    public async Task The_overview_shows_when_storage_is_almost_full_or_full()
    {
        Store.Set(_repository.Id, new StorageUsage { Measured = DateTimeOffset.Now, Source = StorageSource.Disk, Total = 100 * Gigabyte, Free = 5 * Gigabyte });
        var main = await StartAsync();
        var item = main.SelectedRepository!;
        Assert.True(item.IsStorageLow);
        Assert.Equal(BorgTexts.Format(Strings.StorageLowText, 10), item.StorageLevelText);
        Assert.Equal(0.95, item.StorageUsedFraction, 3);

        // A backup that ran out of space makes it full (while that rule is on).
        SaveJob(new BackupRunSummary { Started = DateTimeOffset.Now, Duration = TimeSpan.Zero, Outcome = BackupOutcome.Failed, OutOfSpace = true });
        main = await StartAsync();
        Assert.True(main.SelectedRepository!.IsStorageFull);
        main.UpdateSettings(main.Settings with
        {
            Notifications = new NotificationSettings { StorageFull = new StorageFullCriteria { BackupFailed = false } },
        });
        Assert.True(main.SelectedRepository!.IsStorageLow);
    }

    [Fact]
    public async Task Storage_notifications_come_once_per_level()
    {
        SaveJob();
        _test.Settings.Save(new AppSettings
        {
            Notifications = new NotificationSettings { Webhook = new WebhookChannel { Enabled = true, Url = "https://example.com/hook" } },
        });
        var notifications = new RunNotifications(_test.Services);
        var low = new StorageUsage { Measured = DateTimeOffset.Now, Source = StorageSource.Disk, Total = 100 * Gigabyte, Free = 5 * Gigabyte };

        await notifications.NotifyStorageAsync(_repository, low, _test.Vault);
        await notifications.NotifyStorageAsync(_repository, low, _test.Vault);
        await notifications.NotifyStorageAsync(_repository, low with { Free = 100 * 1024 * 1024 }, _test.Vault);

        var events = _test.Http.Requests.Select(request => JsonNode.Parse(request.Body)!).ToList();
        Assert.Equal(["storage-low", "storage-full"], events.Select(body => (string?)body["event"]));
        Assert.Equal(5 * Gigabyte, (long?)events[0]["storage"]!["free"]);
        Assert.Equal(BorgTexts.Format(Strings.NotifyTitleStorageLow, "Disk"), (string?)events[0]["title"]);
    }

    [Fact]
    public async Task The_editor_keeps_threshold_and_capacity()
    {
        var existing = new RepositoryConfig
        {
            Id = Guid.NewGuid(),
            Name = "Box",
            ProviderId = "ssh",
            ProviderValues = new Dictionary<string, string>
            {
                ["host"] = "nas.local", ["port"] = "22", ["user"] = "backup", ["path"] = "backups", ["remoteBorg"] = "",
            },
            PassphraseMode = PassphraseMode.Ask,
            EncryptionMode = "repokey-blake2",
            SshKeyId = Guid.NewGuid(),
            SshKeyRestricted = true,
        };

        var editor = new RepositoryEditorViewModel(_test.Services, FakeBorg.Installation, existing);
        Assert.Equal(10, editor.StorageWarnPercent);
        editor.StorageWarnPercent = 20;
        editor.StorageCapacityGigabytes = "abc";
        await editor.ConfirmCommand.ExecuteAsync(null);
        Assert.Contains(Strings.EditorCapacityInvalid, editor.Errors);

        editor.StorageCapacityGigabytes = (1.5).ToString(System.Globalization.CultureInfo.CurrentCulture);
        await editor.ConfirmCommand.ExecuteAsync(null);
        var saved = editor.Result!.Repository;
        Assert.Equal(20, saved.StorageWarnPercent);
        Assert.Equal((long)(1.5 * Gigabyte), saved.StorageCapacity);

        Assert.Equal(editor.StorageCapacityGigabytes, new RepositoryEditorViewModel(_test.Services, FakeBorg.Installation, saved).StorageCapacityGigabytes);
    }

    [Fact]
    public void The_notifications_window_keeps_what_counts_as_full()
    {
        var main = new MainViewModel(_test.Services);
        var window = new NotificationsViewModel(main) { FullNextBackupWontFit = false };
        Assert.Contains(window.SystemEvents, option => option.Event == NotificationEvent.StorageFull && option.IsChecked);

        window.SaveCommand.Execute(null);

        var saved = _test.Settings.Load().Notifications.StorageFull;
        Assert.True(saved.BelowOnePercent);
        Assert.False(saved.NextBackupWontFit);
        Assert.True(saved.BackupFailed);
    }
}
