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

    /// <summary>Shows the last measurement of a repository (or of all) and how full that counts as.</summary>
    internal void RefreshStorage(Guid? repositoryId = null)
    {
        var store = StorageStore;
        var criteria = Settings.Notifications.StorageFull;
        foreach (var item in Repositories.Where(item => repositoryId is null || item.Config.Id == repositoryId))
        {
            item.Storage = store.Get(item.Config.Id);
            item.StorageLevel = StorageLevels.Evaluate(item.Storage, item.Config, _jobs, criteria);
        }
    }

    /// <summary>"Speicher messen" in the overview: the disk or server directly, otherwise borg (which may need the passphrase).</summary>
    [RelayCommand(CanExecute = nameof(CanRunBorg))]
    private async Task MeasureStorageAsync()
    {
        var item = SelectedRepository!;
        if (!TryGetLocation(item, out var location))
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
        string? passphrase, SshAccess? ssh)
    {
        var usage = await Task.Run(() => ScheduledJobRunner.MeasureStorageAsync(_services, repository, borg, location, passphrase, ssh));
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
