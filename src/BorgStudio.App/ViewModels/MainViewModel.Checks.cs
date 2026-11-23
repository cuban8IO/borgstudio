using BorgStudio.App.Resources;
using BorgStudio.App.Services;
using BorgStudio.Core.Backups;
using BorgStudio.Core.Borg;
using BorgStudio.Core.Checks;
using BorgStudio.Core.Repositories;
using CommunityToolkit.Mvvm.Input;

namespace BorgStudio.App.ViewModels;

/// <summary>
/// Checking a repository with <c>borg check</c>: normally, thoroughly (all data read and decrypted) or – when damage was
/// found and the user insists – repairing it, followed by a normal check that tells whether all is well again.
/// </summary>
public partial class MainViewModel
{
    private RepositoryCheckStore CheckStore => RepositoryCheckStore.ForDataDirectory(_services.Jobs.DataDirectory);

    /// <summary>Shows the checks of a repository (or of all) on their items.</summary>
    internal void RefreshChecks(Guid? repositoryId = null)
    {
        var store = CheckStore;
        foreach (var item in Repositories.Where(item => repositoryId is null || item.Config.Id == repositoryId))
        {
            item.Checks = store.Get(item.Config.Id);
            NoteLockFromRuns(item);
            RefreshArchiveVerification(item.Config.Id);
        }
        RepairRepositoryCommand.NotifyCanExecuteChanged();
        BreakLockCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanRunBorg))]
    private Task CheckRepositoryAsync() => CheckAsync(SelectedRepository!, CheckDepth.Normal);

    /// <summary>Reads and decrypts all data – after telling how much that is.</summary>
    [RelayCommand(CanExecute = nameof(CanRunBorg))]
    private async Task CheckRepositoryFullAsync()
    {
        var item = SelectedRepository!;
        var size = item.Storage?.RepositoryBytes is { } bytes ? BorgTexts.Size(bytes) : null;
        var message = (item.IsSsh ? Strings.CheckFullMessageRemote : Strings.CheckFullMessageLocal)
            + " " + (size is null ? Strings.CheckFullSizeUnknown : BorgTexts.Format(Strings.CheckFullSize, size));
        if (!await _services.Dialogs.ConfirmAsync(Strings.CheckFullTitle, message, Strings.CheckFullConfirm))
            return;
        await CheckAsync(item, CheckDepth.Full);
    }

    /// <summary>Repairs a damaged repository after two warnings, then checks it again.</summary>
    [RelayCommand(CanExecute = nameof(CanRepairRepository))]
    private async Task RepairRepositoryAsync()
    {
        var item = SelectedRepository!;
        if (!await _services.Dialogs.ConfirmAsync(Strings.RepairTitle, BorgTexts.Format(Strings.RepairMessage, item.Name), Strings.RepairContinue)
            || !await _services.Dialogs.ConfirmAsync(Strings.RepairTitle, Strings.RepairConfirmMessage, Strings.RepairConfirm))
            return;
        await CheckAsync(item, CheckDepth.Repair);
    }

    private bool CanRepairRepository() => CanRunBorg() && SelectedRepository!.HasCheckProblems;

    private async Task CheckAsync(RepositoryItemViewModel item, CheckDepth depth)
    {
        if (await ConnectAsync(item) is not { } location)
            return;
        if (!await ReleaseMountsAsync(item.Config.Id, item.Name))
            return;
        if (await GetSshAccessAsync(item, location) is not { } ssh)
            return;
        if (await GetPassphraseAsync(item) is not { } passphrase)
            return;

        // borg locks the repository itself; this turns "locked" into a clear "a backup is running" up front.
        using var repositoryLock = RepositoryLock.TryAcquire(Runner.LockDirectory, item.Config.Id);
        if (repositoryLock is null)
        {
            item.ShowResult(Strings.CheckBusy, isError: true);
            return;
        }

        var borg = Borg!;
        var throttle = Throttle(item.Config, location);
        var record = await RunCheckAsync(item, depth, ct => _services.BorgClient.CheckAsync(borg, location, depth, passphrase.Value, ssh.Access,
            new ViewModelProgress<BorgCheckProgress>(this, update => ReportCheck(depth, update)), ct, throttle));
        // A repair is followed by a normal check: only that tells whether the repository is sound again.
        if (record.Outcome == CheckOutcome.Repaired)
            record = await RunCheckAsync(item, CheckDepth.Normal, ct => _services.BorgClient.CheckAsync(borg, location, CheckDepth.Normal,
                passphrase.Value, ssh.Access, new ViewModelProgress<BorgCheckProgress>(this, update => ReportCheck(CheckDepth.Normal, update)), ct,
                throttle));

        item.ShowResult(ResultText(record), isError: record.Outcome is CheckOutcome.ProblemsFound or CheckOutcome.Failed);
        RefreshDashboard();
        await NotifyCheckAsync(item.Config, record);
    }

    /// <summary>Runs one check with progress in the overview and keeps its record.</summary>
    private async Task<CheckRecord> RunCheckAsync(RepositoryItemViewModel item, CheckDepth depth,
        Func<CancellationToken, Task<BorgResult<BorgCheckResult>>> check)
    {
        using var cancellation = new CancellationTokenSource();
        var started = DateTimeOffset.Now;
        var result = await RunAsync(item, WorkingText(depth), "check-" + depth.ToString().ToLowerInvariant(), () => check(cancellation.Token),
            depth == CheckDepth.Repair ? null : cancellation.Cancel);
        var duration = DateTimeOffset.Now - started;

        var record = RepositoryChecks.ToRecord(depth, started, duration, result);
        CheckStore.Add(item.Config.Id, record);
        RefreshChecks(item.Config.Id);
        return record;
    }

    /// <summary>The whole check as one bar: each phase gets the share it usually takes.</summary>
    private void ReportCheck(CheckDepth depth, BorgCheckProgress update) =>
        Work.Report(RepositoryChecks.Overall(depth, update), 1, update.Phase switch
        {
            CheckPhase.Segments => Strings.CheckPhaseSegments,
            CheckPhase.Data => Strings.CheckPhaseData,
            _ => Strings.CheckPhaseArchives,
        });

    private static string WorkingText(CheckDepth depth) => depth switch
    {
        CheckDepth.Full => Strings.CheckWorkingFull,
        CheckDepth.Repair => Strings.CheckWorkingRepair,
        _ => Strings.CheckWorking,
    };

    private static string ResultText(CheckRecord record) => record.Outcome switch
    {
        CheckOutcome.Ok => Strings.CheckResultOk,
        CheckOutcome.ProblemsFound => BorgTexts.Format(Strings.CheckResultProblems, record.Messages.Count),
        CheckOutcome.Cancelled => Strings.CheckResultCancelled,
        _ => BorgTexts.Format(Strings.CheckResultFailed, record.Error ?? ""),
    };

    /// <summary>Reports a check from the window (in the background, never failing).</summary>
    private async Task NotifyCheckAsync(RepositoryConfig repository, CheckRecord record)
    {
        if (_services.Notifier is null)
            return;
        var vault = OpenVaultCopy(scheduledAccess: true);
        try
        {
            await Task.Run(() => new RunNotifications(_services).NotifyCheckAsync(repository, record, vault));
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
