using BorgStudio.App.Resources;
using BorgStudio.App.Services;
using BorgStudio.Core.Borg;
using BorgStudio.Core.Repositories;
using BorgStudio.Core.Storage;
using BorgStudio.Plugins;
using CommunityToolkit.Mvvm.Input;

namespace BorgStudio.App.ViewModels;

/// <summary>How full each repository's storage is: measured after every backup and on request, shown in the overview.</summary>
public partial class MainViewModel
{
    private StorageUsageStore StorageStore => StorageUsageStore.ForDataDirectory(_services.Jobs.DataDirectory);

    /// <summary>Shows the last measurement of a repository (or of all), how full that counts as, and when it will be full.</summary>
    internal void RefreshStorage(Guid? repositoryId = null)
    {
        var store = StorageStore;
        var criteria = Settings.Notifications.StorageFull;
        var now = DateTimeOffset.Now;
        foreach (var item in Repositories.Where(item => repositoryId is null || item.Config.Id == repositoryId))
        {
            item.Storage = store.Get(item.Config.Id);
            item.StorageLevel = StorageLevels.Evaluate(item.Storage, item.Config, _jobs, criteria);
            var runs = _jobs.Where(job => job.RepositoryId == item.Config.Id).SelectMany(job => Runner.History.Get(job.Id));
            item.Forecast = StorageForecast.Compute(store.History.Get(item.Config.Id), item.Storage, runs, now);
        }
        // A measurement or a run changes the statistics and the overview too.
        RefreshStatistics();
        RefreshDashboard();
    }

    /// <summary>The statistics of the selected repository; built while their tab is open.</summary>
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    public partial RepositoryStatisticsViewModel? Statistics { get; private set; }

    /// <summary>Builds the statistics of the selected repository anew (only while their tab is open – it reads files).</summary>
    internal void RefreshStatistics()
    {
        if (SelectedDetailsTab != StatisticsTab || SelectedRepository is not { } item)
            return;
        var id = item.Config.Id;
        var previousJob = Statistics?.SelectedJob.Value;
        int? archives;
        try
        {
            archives = _services.Archives.Load(id)?.Count;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Core.Security.VaultLockedException)
        {
            archives = null;
        }
        var runs = _jobs.Where(job => job.RepositoryId == id)
            .Select(job => (job, Runner.History.Get(job.Id)))
            .ToList();
        Statistics = new RepositoryStatisticsViewModel(new RepositoryStatisticsData(item.Storage, StorageStore.History.Get(id), item.Forecast,
            runs, archives, DateTimeOffset.Now));
        // The chosen job stays chosen when the numbers change.
        if (Statistics.JobOptions.FirstOrDefault(option => option.Value == previousJob) is { } option)
            Statistics.SelectedJob = option;
    }

    /// <summary>"Speicher messen" in the overview: the disk or server directly, otherwise borg (which may need the passphrase).</summary>
    [RelayCommand(CanExecute = nameof(CanRunBorg))]
    private async Task MeasureStorageAsync()
    {
        var item = SelectedRepository!;
        if (await ConnectAsync(item) is not { } location)
            return;
        if (await GetSshAccessAsync(item, location) is not { } ssh)
            return;

        var borg = Borg!;
        var probe = new StorageProbe(_services.BorgClient);
        var usage = await Task.Run(() => probe.MeasureDirectAsync(item.Config, borg, location, ssh.Access));
        if (usage is null)
        {
            if (await GetPassphraseAsync(item) is not { } passphrase)
                return;
            var result = await RunAsync(item, Strings.StorageMeasuring, "info",
                () => probe.MeasureWithBorgAsync(item.Config, borg, location, passphrase.Value, ssh.Access));
            if (result.Value is not { } measured)
            {
                item.ShowResult(BorgTexts.Describe(result.Error!), isError: true);
                return;
            }
            usage = measured;
        }

        StorageStore.Set(item.Config.Id, usage);
        RefreshStorage(item.Config.Id);
        await NotifyStorageAsync(item.Config, usage);
    }

    /// <summary>After a backup by hand: measures the storage, shows it and returns it for the notifications.</summary>
    private async Task<StorageUsage?> MeasureAfterBackupAsync(RepositoryConfig repository, BorgInstallation borg, RepositoryLocation location,
        string? passphrase, SshAccess? ssh, RepositoryStats? stats)
    {
        var usage = await Task.Run(() => ScheduledJobRunner.MeasureStorageAsync(_services, repository, borg, location, passphrase, ssh, stats));
        RefreshStorage(repository.Id);
        return usage;
    }

    /// <summary>Reports a storage level from the window (in the background, never failing).</summary>
    private async Task NotifyStorageAsync(RepositoryConfig repository, StorageUsage? usage)
    {
        if (_services.Notifier is null)
            return;
        var vault = OpenVaultCopy(scheduledAccess: true);
        try
        {
            await Task.Run(() => new RunNotifications(_services).NotifyStorageAsync(repository, usage, vault));
        }
        catch (Exception exception)
        {
            AppLog.Write(RunNotifications.LogFile, $"{repository.Name}: {exception}", _services.Jobs.DataDirectory);
        }
        finally
        {
            vault.Lock();
        }
    }
}
