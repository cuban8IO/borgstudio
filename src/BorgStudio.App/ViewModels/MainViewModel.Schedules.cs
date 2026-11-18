using BorgStudio.App.Resources;
using BorgStudio.Core.Backups;
using BorgStudio.Core.Repositories;
using BorgStudio.Core.Secrets;
using BorgStudio.Core.Security;

namespace BorgStudio.App.ViewModels;

/// <summary>
/// Scheduled backups: the system's task scheduler gets one entry per scheduled job (kept in line whenever the jobs are
/// saved, and at startup), and a job's first schedule sets up what runs without window need.
/// </summary>
public partial class MainViewModel
{
    private readonly SemaphoreSlim _scheduleGate = new(1, 1);

    /// <summary>The last update of the system's scheduler (tests wait for it).</summary>
    internal Task SchedulesApplied { get; private set; } = Task.CompletedTask;

    /// <summary>What the system's scheduler starts: this program.</summary>
    internal string? SchedulerExecutable { get; set; } = Environment.ProcessPath;

    private void ApplySchedules() => SchedulesApplied = ApplySchedulesAsync();

    private async Task ApplySchedulesAsync()
    {
        if (_services.Scheduler is not { } scheduler || SchedulerExecutable is not { } executable)
            return;

        // In the background BorgStudio starts the jobs itself: the system's entries go.
        List<BackupJob> jobs = IsBackgroundMode ? [] : [.. _jobs];
        await _scheduleGate.WaitAsync();
        try
        {
            await Task.Run(() => scheduler.ApplyAsync(jobs, executable));
        }
        catch (Exception exception) when (exception is SchedulerException or IOException or UnauthorizedAccessException)
        {
            OnViewModelThread(() => Notice = BorgTexts.Format(Strings.ScheduleApplyFailed, exception.Message));
        }
        finally
        {
            _scheduleGate.Release();
        }
    }

    /// <summary>
    /// Before a scheduled job is saved: runs without window cannot ask. A repository that asks for its passphrase gets
    /// the offer to store it; a PIN-protected vault the question whether scheduled runs may open it.
    /// </summary>
    private async Task PrepareScheduleAsync(BackupJob job)
    {
        if (!job.IsScheduled)
            return;

        if (Repositories.FirstOrDefault(item => item.Config.Id == job.RepositoryId) is { Config.PassphraseMode: PassphraseMode.Ask } item)
            await OfferToStorePassphraseAsync(item);

        if (_services.Vault.State == VaultState.NotSetUp || ScheduledAccess.IsEnabled)
            return;
        var message = ScheduledAccess.UsesFile ? Strings.ScheduleAccessMessageFile : Strings.ScheduleAccessMessage;
        if (!await _services.Dialogs.ConfirmAsync(Strings.ScheduleAccessTitle, message, Strings.ScheduleAccessConfirm, Strings.ScheduleAccessLater))
            return;
        try
        {
            if (await VaultAccess.EnsureUnlockedAsync(_services.Vault, _services.Dialogs))
                ScheduledAccess.Enable();
        }
        catch (Exception exception) when (exception is SecretStoreException or SecretBoxException or IOException or UnauthorizedAccessException)
        {
            Notice = BorgTexts.Format(Strings.ScheduleAccessFailed, exception.Message);
        }
    }

    private async Task OfferToStorePassphraseAsync(RepositoryItemViewModel item)
    {
        // In the vault if there is one, else in the system keychain.
        PassphraseMode? mode = _services.Vault.State != VaultState.NotSetUp ? PassphraseMode.Vault
            : _services.SecretStore.IsAvailable ? PassphraseMode.Stored
            : null;
        if (mode is not { } storage)
            return;
        if (!await _services.Dialogs.ConfirmAsync(Strings.SchedulePassphraseTitle, BorgTexts.Format(Strings.SchedulePassphraseMessage, item.Name),
                Strings.SchedulePassphraseConfirm, Strings.ScheduleAccessLater))
            return;
        if (await _services.Dialogs.AskPassphraseAsync(item.Name) is not { } passphrase)
            return;

        var updated = item.Config with { PassphraseMode = storage };
        if (await new PassphraseStore(_services).SetAsync(updated, storage, passphrase, _services.Dialogs) is { } error)
        {
            Notice = error;
            return;
        }
        item.Config = updated;
        SaveRepositories();
    }
}
