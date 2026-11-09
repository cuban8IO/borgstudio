using System.Globalization;
using BorgStudio.App.Resources;
using BorgStudio.Core.Backups;
using BorgStudio.Core.Borg;
using BorgStudio.Core.Progress;
using BorgStudio.Plugins;
using CommunityToolkit.Mvvm.Input;

namespace BorgStudio.App.ViewModels;

/// <summary>
/// Cleaning up: <c>borg prune</c> deletes a job's old archives by its rules – after each backup, or by hand after a
/// preview – and <c>borg compact</c> then frees their space. Freeing space also works on its own for a repository.
/// </summary>
public partial class MainViewModel
{
    // --- Cleaning up a job by hand ---

    /// <summary>Shows which archives the job's rules would delete and, if confirmed, deletes them and frees the space.</summary>
    [RelayCommand(CanExecute = nameof(CanCleanUpJob))]
    private Task CleanUpJobAsync()
    {
        _backupTask = CleanUpAsync(SelectedJob!.Job);
        return _backupTask;
    }

    private bool CanCleanUpJob() => CanRunJob() && SelectedJob!.HasRetention;

    private async Task CleanUpAsync(BackupJob job)
    {
        var item = Repositories.FirstOrDefault(repository => repository.Config.Id == job.RepositoryId);
        if (item is null || job.Retention is not { HasRules: true } retention || !TryGetLocation(item, out var location))
            return;
        if (!await ReleaseMountsAsync(item.Config.Id, item.Name))
            return;
        if (await GetSshAccessAsync(item, location) is not { } ssh)
            return;
        if (await GetPassphraseAsync(item) is not { } passphrase)
            return;

        var borg = Borg!;
        var run = new BackupRunViewModel(job, cleanupOnly: true);
        CurrentBackup = run;
        IsBackupRunning = true;
        using var cancellation = new CancellationTokenSource();
        _backupCancellation = cancellation;
        CleanupSummary cleanup;
        try
        {
            var previewKey = DurationHistory.Key(job.RepositoryId, "prune-preview");
            run.StartPhase(RunPhase.Previewing, _services.Durations.Get(previewKey));
            var preview = await _services.BorgClient.PruneAsync(borg, location, ArchiveGlob(job.Name), retention.Arguments(),
                dryRun: true, passphrase.Value, ssh.Access, new ViewModelProgress<double>(this, run.ReportPhase), cancellation.Token);
            if (preview.Succeeded)
                _services.Durations.Record(previewKey, run.PhaseProgress.Elapsed);
            if (!preview.Succeeded)
                cleanup = Failed(preview.Error!);
            else if (preview.Value!.Pruned.Count == 0)
                cleanup = new CleanupSummary();
            else if (!await _services.Dialogs.ConfirmPruneAsync(new PrunePreviewViewModel(job.Name, retention, preview.Value))
                     || cancellation.IsCancellationRequested)
                cleanup = new CleanupSummary { Cancelled = true };
            else
                cleanup = await PruneAndCompactAsync(run, job, retention, borg, location, ssh.Access, passphrase.Value, cancellation.Token);
        }
        finally
        {
            _backupCancellation = null;
        }

        run.FinishCleanup(cleanup);
        IsBackupRunning = false;
        if (cleanup.PrunedArchives > 0)
            ArchivesChanged(job.RepositoryId);
    }

    /// <summary>Deletes the job's archives its rules don't keep and, if any went, frees their space.</summary>
    private async Task<CleanupSummary> PruneAndCompactAsync(BackupRunViewModel run, BackupJob job, RetentionPolicy retention,
        BorgInstallation borg, RepositoryLocation location, SshAccess? ssh, string? passphrase, CancellationToken cancellationToken)
    {
        var pruneKey = DurationHistory.Key(job.RepositoryId, "prune");
        run.StartPhase(RunPhase.Pruning, _services.Durations.Get(pruneKey));
        var pruned = await _services.BorgClient.PruneAsync(borg, location, ArchiveGlob(job.Name), retention.Arguments(),
            dryRun: false, passphrase, ssh, new ViewModelProgress<double>(this, run.ReportPhase), cancellationToken);
        if (!pruned.Succeeded)
            return Failed(pruned.Error!);
        _services.Durations.Record(pruneKey, run.PhaseProgress.Elapsed);
        var count = pruned.Value!.Pruned.Count;
        if (count == 0)
            return new CleanupSummary();

        var compactKey = DurationHistory.Key(job.RepositoryId, "compact");
        run.StartPhase(RunPhase.Compacting, _services.Durations.Get(compactKey));
        var compacted = await _services.BorgClient.CompactAsync(borg, location, ssh,
            new ViewModelProgress<double>(this, run.ReportPhase), cancellationToken);
        if (compacted.Succeeded)
            _services.Durations.Record(compactKey, run.PhaseProgress.Elapsed);
        return compacted.Succeeded
            ? new CleanupSummary { PrunedArchives = count, FreedBytes = compacted.Value!.FreedBytes }
            : Failed(compacted.Error!) with { PrunedArchives = count };
    }

    private static CleanupSummary Failed(BorgError error) => error.Kind == BorgErrorKind.Cancelled
        ? new CleanupSummary { Cancelled = true }
        : new CleanupSummary { Error = BorgTexts.Describe(error) };

    // --- Freeing space in a repository ---

    /// <summary><c>borg compact</c> on the selected repository (e.g. after archives were deleted elsewhere).</summary>
    [RelayCommand(CanExecute = nameof(CanRunBorg))]
    private async Task FreeSpaceAsync()
    {
        var item = SelectedRepository!;
        if (!TryGetLocation(item, out var location))
            return;
        if (!await ReleaseMountsAsync(item.Config.Id, item.Name))
            return;
        // compact works on the repository's segments only: no passphrase needed.
        if (await GetSshAccessAsync(item, location) is not { } ssh)
            return;

        var borg = Borg!;
        using var cancellation = new CancellationTokenSource();
        // Progress after borg has finished is ignored (the panel is stopped then).
        var progress = new ViewModelProgress<double>(this, fraction => Work.Report(fraction));
        var result = await RunAsync(item, Strings.CleanupCompacting, "compact",
            () => _services.BorgClient.CompactAsync(borg, location, ssh.Access, progress, cancellation.Token), cancellation.Cancel);

        if (result.Value is { } compacted)
            item.ShowResult(compacted.FreedBytes is { } freed ? BorgTexts.Format(Strings.SpaceFreed, BorgTexts.Size(freed)) : Strings.SpaceFreedNoSize,
                isError: false);
        else if (result.Error!.Kind == BorgErrorKind.Cancelled)
            item.ShowResult(Strings.SpaceFreeCancelled, isError: false);
        else
            item.ShowResult(BorgTexts.Describe(result.Error), isError: true);
    }
}

/// <summary>What cleaning up a job would do: which archives go, which stay (and why). Shown before deleting.</summary>
public sealed class PrunePreviewViewModel(string jobName, RetentionPolicy retention, BorgPruneResult preview)
{
    public string JobName => jobName;

    public IReadOnlyList<PruneEntryViewModel> Archives { get; } = [.. preview.Archives.Select(archive => new PruneEntryViewModel(archive))];

    public int PrunedCount => preview.Pruned.Count;

    public int KeptCount => preview.Archives.Count - PrunedCount;

    /// <summary>"Von 12 Archiven des Jobs „Dokumente“ werden 3 gelöscht, 9 bleiben."</summary>
    public string SummaryText => BorgTexts.Format(Strings.PruneSummary, jobName, preview.Archives.Count, PrunedCount, KeptCount);

    public string RulesText => BorgTexts.Format(Strings.PruneRules, BorgTexts.Retention(retention) ?? "");

    public string ConfirmText => BorgTexts.Format(Strings.PruneConfirm, PrunedCount);
}

/// <summary>An archive in the clean-up preview.</summary>
public sealed class PruneEntryViewModel(BorgPruneEntry entry)
{
    public BorgPruneEntry Entry => entry;

    public string Name => entry.Name;

    public string StartText => entry.Start == DateTimeOffset.MinValue ? "" : entry.Start.ToString("g", CultureInfo.CurrentCulture);

    public bool IsPruned => !entry.Kept;

    /// <summary>"Wird gelöscht", "Bleibt (täglich #1)", "Bleibt (Checkpoint)".</summary>
    public string StatusText => !entry.Kept ? Strings.PruneDeleted
        : entry.Rule is { } rule ? BorgTexts.Format(Strings.PruneKept, BorgTexts.PruneRule(rule, entry.RuleNumber))
        : Strings.PruneKeptCheckpoint;
}
