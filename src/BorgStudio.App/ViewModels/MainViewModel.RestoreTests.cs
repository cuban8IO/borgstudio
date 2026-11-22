using BorgStudio.App.Resources;
using BorgStudio.App.Services;
using BorgStudio.Core.Backups;
using BorgStudio.Core.Checks;
using CommunityToolkit.Mvvm.Input;

namespace BorgStudio.App.ViewModels;

/// <summary>
/// Restore tests: the newest archive of a job restored on trial – with the repository's automatic checks (if switched on
/// in the settings and for the job), or by hand with the job's button, shown in the run's card.
/// </summary>
public partial class MainViewModel
{
    private RestoreTestStore RestoreTestStore => RestoreTestStore.ForDataDirectory(_services.Jobs.DataDirectory);

    /// <summary>"Wiederherstellung testen": the job's mode, or a sample compared with the source if the job has none.</summary>
    [RelayCommand(CanExecute = nameof(CanRunJob))]
    private Task TestRestoreAsync()
    {
        _backupTask = TestRestoreAsync(SelectedJob!.Job);
        return _backupTask;
    }

    private async Task TestRestoreAsync(BackupJob job)
    {
        var item = Repositories.FirstOrDefault(repository => repository.Config.Id == job.RepositoryId);
        if (item is null || await ConnectAsync(item) is not { } location)
            return;
        if (await GetSshAccessAsync(item, location) is not { } ssh)
            return;
        if (await GetPassphraseAsync(item) is not { } passphrase)
            return;
        if (IsBackupRunning)
            return;

        var borg = Borg!;
        var run = new BackupRunViewModel(job, restoreTestOnly: true);
        CurrentBackup = run;
        IsBackupRunning = true;
        using var cancellation = new CancellationTokenSource();
        _backupCancellation = cancellation;
        RestoreTestRecord record;
        try
        {
            using var repositoryLock = RepositoryLock.TryAcquire(Runner.LockDirectory, job.RepositoryId);
            if (repositoryLock is null)
            {
                record = new RestoreTestRecord(DateTimeOffset.Now, TimeSpan.Zero, job.RestoreTest, RestoreTestOutcome.NotRun) { Error = Strings.CheckBusy };
            }
            else
            {
                run.StartPhase(RunPhase.RestoreTesting);
                var mode = job.RestoreTest == RestoreTestMode.Off ? RestoreTestMode.Compare : job.RestoreTest;
                record = await Task.Run(() => new RepositoryChecks(_services).TestRestoreAsync(job,
                    new UnattendedAccess(borg, location, passphrase.Value, ssh.Access, Throttle(item.Config, location)), mode, Settings.RestoreTest,
                    automatic: false,
                    new RunReport(this, run), cancellation.Token));
            }
        }
        finally
        {
            _backupCancellation = null;
        }

        run.FinishRestoreTest(record);
        IsBackupRunning = false;
        RefreshRepositoryJobs();
        RefreshDashboard();
        await NotifyRestoreTestAsync(job, item.Name, record);
    }

    /// <summary>Reports a restore test from the window (in the background, never failing).</summary>
    private async Task NotifyRestoreTestAsync(BackupJob job, string? repository, RestoreTestRecord record)
    {
        if (_services.Notifier is null)
            return;
        var vault = OpenVaultCopy(scheduledAccess: true);
        try
        {
            await Task.Run(() => new RunNotifications(_services).NotifyRestoreTestAsync(job, repository, record, vault));
        }
        catch (Exception exception)
        {
            AppLog.Write(RunNotifications.LogFile, $"{job.Name}: {exception}", _services.Jobs.DataDirectory);
        }
        finally
        {
            vault.Lock();
        }
    }
}
