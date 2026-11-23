using BorgStudio.App.Resources;
using BorgStudio.App.Services;
using BorgStudio.Core.Backups;
using BorgStudio.Core.Repositories;
using BorgStudio.Plugins;
using BorgStudio.Providers.Usb;

namespace BorgStudio.App.ViewModels;

/// <summary>
/// Backing up when a drive is attached (while BorgStudio runs, with its window or in the notification area): every few
/// seconds the drives are listed; on a new one, each USB drive repository that wants it and is found there – and each job
/// backing up that very drive – asks "Jetzt sichern?". On yes all its jobs run one after the other, and the drive stays
/// connected until the last one is done (then it is ejected, if wanted).
/// </summary>
public partial class MainViewModel
{
    private HashSet<string>? _knownDrives;
    private readonly Dictionary<Guid, (string Root, CancellationTokenSource Away)> _plugInQuestions = [];
    private readonly HashSet<Guid> _plugInBusy = [];
    private Task _plugInRun = Task.CompletedTask;

    /// <summary>What the last attached drive started (tests wait for it).</summary>
    internal Task PlugInRun => _plugInRun;

    private static StringComparer PathComparer =>
        OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

    private IDriveSystem? UsbDrives => _services.Plugins.RepositoryProviders.OfType<UsbRepositoryProvider>().FirstOrDefault()?.Drives;

    /// <summary>Called every few seconds: notices drives attached and pulled since the last look (the first look only remembers).</summary>
    public void DriveTick()
    {
        if (UsbDrives is not { } drives)
            return;
        HashSet<string> now;
        try
        {
            now = drives.Roots().Select(root => root.Path).ToHashSet(PathComparer);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return;
        }
        var before = _knownDrives;
        _knownDrives = now;
        if (before is null)
            return;

        // Pulled while its question is open: the question goes away.
        foreach (var (_, (root, away)) in _plugInQuestions.Where(question => !now.Contains(question.Value.Root)).ToList())
            away.Cancel();

        // Asked whenever BorgStudio runs (window or notification area), whichever way schedules are run.
        var attached = now.Where(root => !before.Contains(root)).ToList();
        if (attached.Count == 0)
            return;
        foreach (var repository in Repositories.Select(item => item.Config)
                     .Where(config => config.ProviderId == UsbRepositoryProvider.ProviderId && config.PlugIn.Enabled))
        {
            if (!_plugInBusy.Add(repository.Id))
                continue;
            var run = PlugInAsync(repository, attached);
            _plugInRun = Task.WhenAll(_plugInRun, run);
        }

        // Jobs backing up a drive that was just attached.
        if (_jobs.Any(job => job.Drive is { PlugIn.Enabled: true }) && SourceDriveLocator.For(_services) is { } sources)
            _plugInRun = Task.WhenAll(_plugInRun, Task.WhenAll(attached.Select(root => PlugInSourceAsync(sources, root))));
    }

    private readonly HashSet<string> _plugInDrivesBusy = new(StringComparer.Ordinal);

    /// <summary>
    /// A drive was attached that jobs back up (and want to be asked for): "Back up now?" – on yes they all run, the drive
    /// held until the last one is done, then ejected if one of them wants that.
    /// </summary>
    private async Task PlugInSourceAsync(SourceDriveLocator sources, string root)
    {
        string? key = null;
        try
        {
            var ids = await sources.IdsAsync(root);
            var jobs = _jobs.Where(job => job.Drive is { PlugIn.Enabled: true } drive && ids.Contains(drive.Id)
                                          && !BackedUpRecently(drive.PlugIn.MinimumHours, [job])).ToList();
            if (jobs.Count == 0 || !_plugInDrivesBusy.Add(key = jobs[0].Drive!.Id))
            {
                key = null;
                return;
            }

            var name = BorgTexts.DriveName(jobs[0].Drive!);
            using var away = new CancellationTokenSource();
            var question = Guid.NewGuid();
            _plugInQuestions[question] = (root, away);
            bool backUp;
            try
            {
                backUp = await _services.Dialogs.AskOnTopAsync(Strings.PlugInTitle,
                    BorgTexts.Format(Strings.PlugInSourceQuestion, name, string.Join(", ", jobs.Select(job => job.Name))),
                    Strings.PlugInBackUp, Strings.PlugInNotNow, away.Token);
            }
            finally
            {
                _plugInQuestions.Remove(question);
            }
            if (!backUp)
            {
                ScheduledRun.Log($"{name} (attached): not backed up now", _services.Jobs.DataDirectory);
                return;
            }

            ScheduledRun.Log($"{name} (attached): backing up {jobs.Count} job(s)", _services.Jobs.DataDirectory);
            using (SourceDriveLocator.Hold(root))
                await Task.WhenAll(jobs.Select(job => QueueTriggeredRun(job.Id).Finished));
            if (jobs.Any(job => job.Drive!.Eject) && await sources.EjectAsync(root) is { } problem)
                ScheduledRun.Log($"{name}: {problem}", _services.Jobs.DataDirectory);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ScheduledRun.Log($"{root} (attached): {exception}", _services.Jobs.DataDirectory);
        }
        finally
        {
            if (key is not null)
                _plugInDrivesBusy.Remove(key);
        }
    }

    private async Task PlugInAsync(RepositoryConfig repository, IReadOnlyList<string> attached)
    {
        try
        {
            var jobs = _jobs.Where(job => job.RepositoryId == repository.Id).ToList();
            if (jobs.Count == 0 || BackedUpRecently(repository.PlugIn.MinimumHours, jobs))
                return;
            // On one of the drives just attached? (Looked for without holding the drive: "Nicht jetzt" leaves it attached.)
            if (await FindOnAttachedAsync(repository, attached) is not { } root)
                return;

            using var away = new CancellationTokenSource();
            _plugInQuestions[repository.Id] = (root, away);
            bool backUp;
            try
            {
                backUp = await _services.Dialogs.AskOnTopAsync(Strings.PlugInTitle,
                    BorgTexts.Format(Strings.PlugInQuestion, repository.Name, string.Join(", ", jobs.Select(job => job.Name))),
                    Strings.PlugInBackUp, Strings.PlugInNotNow, away.Token);
            }
            finally
            {
                _plugInQuestions.Remove(repository.Id);
            }
            if (!backUp)
            {
                ScheduledRun.Log($"{repository.Name} (attached): not backed up now", _services.Jobs.DataDirectory);
                return;
            }

            ScheduledRun.Log($"{repository.Name} (attached): backing up {jobs.Count} job(s)", _services.Jobs.DataDirectory);
            // Held for all jobs: the drive is ejected (if wanted) only after the last one.
            RepositoryLease? lease = null;
            try
            {
                lease = await _services.Connections.ConnectAsync(repository, unattended: true);
            }
            catch (RepositoryUnavailableException)
            {
                // Pulled meanwhile: the jobs record that.
            }
            try
            {
                await Task.WhenAll(jobs.Select(job => QueueTriggeredRun(job.Id).Finished));
            }
            finally
            {
                if (lease is not null && await lease.ReleaseAsync() is { } problem)
                    ScheduledRun.Log($"{repository.Name}: {problem}", _services.Jobs.DataDirectory);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ScheduledRun.Log($"{repository.Name} (attached): {exception}", _services.Jobs.DataDirectory);
        }
        finally
        {
            _plugInBusy.Remove(repository.Id);
        }
    }

    /// <summary>
    /// One of the jobs backed up successfully by itself (on a schedule, when attached …) within the last
    /// <paramref name="hours"/> (<see cref="PlugInBackup.MinimumHours"/>). Backups started by hand don't count: right after
    /// trying a job out, attaching the drive still asks.
    /// </summary>
    private bool BackedUpRecently(int hours, IEnumerable<BackupJob> jobs) =>
        hours > 0 && jobs.Any(job => Runner.History.Get(job.Id).Append(job.LastRun).Any(run =>
            run is { Automatic: true, Outcome: BackupOutcome.Succeeded or BackupOutcome.Warnings }
            && Clock() - run.Started.LocalDateTime < TimeSpan.FromHours(hours)));

    /// <summary>The attached drive the repository is on; <c>null</c> if it is elsewhere (it was attached before) or not found.</summary>
    private async Task<string?> FindOnAttachedAsync(RepositoryConfig repository, IReadOnlyList<string> attached)
    {
        try
        {
            await using var lease = await _services.Connections.ConnectAsync(repository);
            if (lease is null)
                return null;
            if (lease.Repository != repository && Repositories.FirstOrDefault(item => item.Config.Id == repository.Id) is { } item)
                item.Config = lease.Repository;
            var comparison = PathComparer == StringComparer.Ordinal ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            return attached.FirstOrDefault(root => lease.Location.Url.StartsWith(root, comparison));
        }
        catch (RepositoryUnavailableException)
        {
            return null;
        }
    }

    /// <summary>Unmounts the SFTP folders kept mounted for the next borg call (when the app ends).</summary>
    public void CloseSftpMounts() => Task.Run(_services.BorgClient.CloseSftpMountsAsync).Wait(TimeSpan.FromSeconds(30));

    // --- Several queued runs on one drive ---

    private readonly Dictionary<Guid, RepositoryLease> _queueLeases = [];

    /// <summary>
    /// Keeps a drive connected while more queued runs of its repository wait, so it is ejected (if wanted) only after the
    /// last one – not after the first.
    /// </summary>
    private async Task HoldWhileQueuedAsync(Guid repositoryId)
    {
        if (_queueLeases.ContainsKey(repositoryId) || !IsQueued(repositoryId)
            || Repositories.FirstOrDefault(item => item.Config.Id == repositoryId)?.Config is not { } repository
            || _services.Plugins.FindRepositoryProvider(repository.ProviderId) is not IRepositoryConnector)
            return;
        try
        {
            if (await _services.Connections.ConnectAsync(repository, unattended: true) is { } lease)
                _queueLeases[repositoryId] = lease;
        }
        catch (RepositoryUnavailableException)
        {
            // Not there: each run records that itself.
        }
    }

    /// <summary>Lets go of the drives no queued run waits for any more (<paramref name="all"/>: of every one).</summary>
    private async Task ReleaseQueueLeasesAsync(bool all = false)
    {
        foreach (var (repositoryId, lease) in _queueLeases.Where(held => all || !IsQueued(held.Key)).ToList())
        {
            _queueLeases.Remove(repositoryId);
            if (await lease.ReleaseAsync() is { } problem)
                ScheduledRun.Log($"{lease.Repository.Name}: {problem}", _services.Jobs.DataDirectory);
        }
    }

    private bool IsQueued(Guid repositoryId) =>
        _dueChecks.Contains(repositoryId)
        || _dueJobs.Any(queued => _jobs.FirstOrDefault(job => job.Id == queued.JobId)?.RepositoryId == repositoryId);
}
