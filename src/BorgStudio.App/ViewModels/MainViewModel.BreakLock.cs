using System.Globalization;
using BorgStudio.App.Resources;
using BorgStudio.Core.Backups;
using BorgStudio.Core.Borg;
using CommunityToolkit.Mvvm.Input;

namespace BorgStudio.App.ViewModels;

/// <summary>
/// A lock left behind (a crash, a connection that broke off) keeps borg away from a repository. Once borg reported the
/// repository locked, its overview offers "Sperre lösen …" – with a clear warning and, for a local repository, who holds it.
/// </summary>
public partial class MainViewModel
{
    /// <summary>Notes on the repository whether borg found it locked (or got through, which clears that).</summary>
    private static void NoteLock(RepositoryItemViewModel item, BorgError? error)
    {
        if (error?.Kind == BorgErrorKind.Locked)
            item.LockedSince = DateTimeOffset.Now;
        else if (error is null)
            item.LockedSince = null;
    }

    /// <summary>The newest run of the repository's jobs (also one without window) tells whether it was found locked.</summary>
    private void NoteLockFromRuns(RepositoryItemViewModel item)
    {
        if (_jobs.Where(job => job.RepositoryId == item.Config.Id).Select(job => job.LastRun).OfType<BackupRunSummary>()
                .MaxBy(run => run.Started) is not { } newest
            || (item.LockedSince is { } since && since >= newest.Started))
            return;
        if (newest.RepositoryLocked)
            item.LockedSince = newest.Started;
        else if (newest.Outcome is BackupOutcome.Succeeded or BackupOutcome.Warnings)
            item.LockedSince = null;
    }

    [RelayCommand(CanExecute = nameof(CanBreakLock))]
    private async Task BreakLockAsync()
    {
        var item = SelectedRepository!;
        if (!TryGetLocation(item, out var location))
            return;
        // BorgStudio's own runs on the repository hold this lock: that is no lock left behind.
        using var ownLock = RepositoryLock.TryAcquire(Runner.LockDirectory, item.Config.Id);
        if (ownLock is null)
        {
            item.ShowResult(Strings.BreakLockBusy, isError: true);
            return;
        }

        var message = BorgTexts.Format(Strings.BreakLockMessage, item.Name);
        if (location.Ssh is null && RepositoryLockInfo.Read(location.Url) is { } info)
            message += "\n\n" + HolderText(info);
        if (!await _services.Dialogs.ConfirmAsync(Strings.BreakLockTitle, message, Strings.BreakLockConfirm))
            return;
        if (await GetSshAccessAsync(item, location) is not { } ssh)
            return;

        var borg = Borg!;
        var result = await RunAsync(item, Strings.BreakLockWorking, "break-lock",
            () => _services.BorgClient.BreakLockAsync(borg, location, ssh.Access));
        if (result.Succeeded)
        {
            item.LockedSince = null;
            item.ShowResult(Strings.BreakLockDone, isError: false);
        }
        else
        {
            item.ShowResult(BorgTexts.Describe(result.Error!), isError: true);
        }
    }

    private bool CanBreakLock() => CanRunBorg() && SelectedRepository!.IsLocked;

    /// <summary>"Gesperrt von NAS-PC (Prozess 4711, schreibend) seit 20.11.2026 14:03." – and what it means if it is this computer.</summary>
    internal static string HolderText(RepositoryLockInfo info)
    {
        var holders = string.Join(", ", info.Holders.Select(holder => BorgTexts.Format(
            holder.Exclusive ? Strings.BreakLockHolderExclusive : Strings.BreakLockHolderShared, holder.Host, holder.ProcessId)));
        var text = info.Since is { } since
            ? BorgTexts.Format(Strings.BreakLockHeldSince, holders, since.ToLocalTime().ToString("g", CultureInfo.CurrentCulture))
            : BorgTexts.Format(Strings.BreakLockHeld, holders);
        if (info.Holders.Any(holder => RepositoryLockInfo.IsThisComputer(holder.Host)))
            text += " " + Strings.BreakLockThisComputer;
        return text;
    }
}
