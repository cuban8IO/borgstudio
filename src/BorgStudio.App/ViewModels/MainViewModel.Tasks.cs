using System.Globalization;
using BorgStudio.App.Resources;
using BorgStudio.Core.Backups;
using BorgStudio.Core.Progress;

namespace BorgStudio.App.ViewModels;

/// <summary>
/// The list of running tasks in the status bar: what the window and the background work on (with progress and the time
/// still to go), what waits in the queue, and what the system's task scheduler runs in processes of its own.
/// </summary>
public partial class MainViewModel
{
    private RunningTasksViewModel? _tasks;

    public RunningTasksViewModel Tasks => _tasks ??= CreateTasks();

    private RunningTasksViewModel CreateTasks()
    {
        var tasks = new RunningTasksViewModel();
        // The window's own waits that are not bound to a repository operation.
        tasks.Track(ProgressTask(Strings.TaskArchives, null, ArchivesProgress, () => ShowDetails(ArchivesTab)));
        tasks.Track(ProgressTask(Strings.TaskMount, null, MountProgress, () => ShowDetails(ArchivesTab)));
        return tasks;
    }

    /// <summary>A task following <paramref name="progress"/>: shown while it is active, cancelled through it.</summary>
    internal static RunningTaskViewModel ProgressTask(string title, string? detail, ProgressViewModel progress, Action? show) =>
        new(title, detail, RunningTaskState.Running,
            () => new TaskProgress(progress.IsActive, progress.IsIndeterminate ? null : progress.Fraction, progress.SummaryText,
                progress.Remaining, progress.CanCancel),
            () => progress.CancelCommand.Execute(null), show, progress);

    /// <summary>A backup (or clean-up, restore test) with its card.</summary>
    private RunningTaskViewModel BackupTask(BackupRunViewModel run, BackupJob job) =>
        new(run.Title, RepositoryName(job.RepositoryId), RunningTaskState.Running, () =>
            {
                var phase = run.PhaseProgress;
                return run.IsBackupPhase
                    ? new TaskProgress(run.IsRunning, run.IsIndeterminate ? null : run.Fraction,
                        $"{run.StatusText} · {run.RemainingText}", run.Remaining, !run.IsCancelling)
                    : new TaskProgress(run.IsRunning, phase.IsIndeterminate ? null : phase.Fraction,
                        phase.SummaryText.Length > 0 ? $"{run.StatusText} · {phase.SummaryText}" : run.StatusText, phase.Remaining,
                        !run.IsCancelling);
            },
            () => CancelBackupCommand.Execute(null), () => ShowJob(job.RepositoryId, job.Id), run, run.PhaseProgress);

    /// <summary>The work of a window (restore, export, search …): "Anzeigen" brings the window to the front.</summary>
    private RunningTaskViewModel WindowTask(string title, string? detail, ProgressViewModel progress, object window) =>
        ProgressTask(title, detail, progress, () => _services.Dialogs.BringToFront(window));

    private string? RepositoryName(Guid repositoryId) => Repositories.FirstOrDefault(item => item.Config.Id == repositoryId)?.Name;

    /// <summary>Shows a repository (and, with <paramref name="jobId"/>, the job in its jobs tab).</summary>
    internal void ShowJob(Guid repositoryId, Guid? jobId = null)
    {
        if (Repositories.FirstOrDefault(item => item.Config.Id == repositoryId) is not { } repository)
            return;
        SelectedRepository = repository;
        if (jobId is not { } id)
            return;
        SelectedDetailsTab = JobsTab;
        SelectedJob = RepositoryJobs.FirstOrDefault(item => item.Job.Id == id);
    }

    private void ShowDetails(int tab)
    {
        if (SelectedRepository is not null)
            SelectedDetailsTab = tab;
    }

    // --- The queue (background mode): queued runs and checks, runs a condition holds back ---

    private string _waitingKey = "";

    /// <summary>Follows the queue; called whenever it changes.</summary>
    private void RefreshWaitingTasks()
    {
        var queuedJobs = _dueJobs.Select(queued => queued.JobId).Distinct().ToList();
        var held = _conditionWaits.Where(wait => !queuedJobs.Contains(wait.Key)).ToList();
        var checks = _dueChecks.Distinct().ToList();
        var key = string.Join(",", queuedJobs) + "|" + string.Join(",", held.Select(wait => $"{wait.Key}@{wait.Value.RetryAt:O}"))
                  + "|" + string.Join(",", checks);
        if (key == _waitingKey)
            return;
        _waitingKey = key;

        var waiting = new List<RunningTaskViewModel>();
        foreach (var jobId in queuedJobs)
            if (_jobs.FirstOrDefault(job => job.Id == jobId) is { } job)
                waiting.Add(RunningTaskViewModel.Fixed(BorgTexts.Format(Strings.TaskBackup, job.Name), RepositoryName(job.RepositoryId),
                    RunningTaskState.Waiting, Strings.TaskQueued, () => RemoveQueuedJob(job.Id), () => ShowJob(job.RepositoryId, job.Id)));
        foreach (var (jobId, (retryAt, _)) in held)
            if (_jobs.FirstOrDefault(job => job.Id == jobId) is { } job)
                waiting.Add(RunningTaskViewModel.Fixed(BorgTexts.Format(Strings.TaskBackup, job.Name), RepositoryName(job.RepositoryId),
                    RunningTaskState.Waiting,
                    BorgTexts.Format(Strings.TaskWaiting, _waitReasons.TryGetValue(jobId, out var reason) ? BorgTexts.SkipReason(reason) : Strings.TaskWaitingCondition,
                        retryAt.ToString("t", CultureInfo.CurrentCulture)),
                    () => StopWaiting(job.Id), () => ShowJob(job.RepositoryId, job.Id)));
        foreach (var repositoryId in checks)
            if (RepositoryName(repositoryId) is { } name)
                waiting.Add(RunningTaskViewModel.Fixed(BorgTexts.Format(Strings.TaskCheck, name), null, RunningTaskState.Waiting,
                    Strings.TaskQueued, () => RemoveQueuedCheck(repositoryId), () => ShowJob(repositoryId)));
        Tasks.SetWaiting(waiting);
    }

    /// <summary>Why a held back run waits (for the list).</summary>
    private readonly Dictionary<Guid, BackupSkipReason> _waitReasons = [];

    /// <summary>Takes a queued run out of the queue (a waiting webhook call learns it did not run).</summary>
    private void RemoveQueuedJob(Guid jobId)
    {
        var keep = _dueJobs.Where(queued => queued.JobId != jobId).ToList();
        _dueJobs.Clear();
        foreach (var queued in keep)
            _dueJobs.Enqueue(queued);
        FinishWaiters(jobId, null);
        RaiseBackgroundStateChanged();
    }

    /// <summary>A run held back by a condition is not tried again (until the job's next time).</summary>
    private void StopWaiting(Guid jobId)
    {
        _conditionWaits.Remove(jobId);
        _waitReasons.Remove(jobId);
        RaiseBackgroundStateChanged();
    }

    private void RemoveQueuedCheck(Guid repositoryId)
    {
        var keep = _dueChecks.Where(queued => queued != repositoryId).ToList();
        _dueChecks.Clear();
        foreach (var queued in keep)
            _dueChecks.Enqueue(queued);
        RaiseBackgroundStateChanged();
    }

    // --- Runs of the system's task scheduler (other processes) ---

    private IReadOnlyList<RunMarker> _otherRuns = [];

    /// <summary>Looks at the runs other BorgStudio processes left word of; called every few seconds.</summary>
    public void RefreshOtherRuns()
    {
        IReadOnlyList<RunMarker> markers;
        try
        {
            markers = RunMarkers.ForDataDirectory(_services.Jobs.DataDirectory).Others();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return;
        }
        if (markers.SequenceEqual(_otherRuns))
            return;
        _otherRuns = markers;
        Tasks.SetElsewhere(markers.Select(marker =>
        {
            var title = BorgTexts.Format(marker.Kind == RunMarkerKind.Check ? Strings.TaskCheck : Strings.TaskBackup, marker.Name);
            var status = marker.Waiting is { } waiting
                ? BorgTexts.Format(Strings.TaskElsewhereWaiting, waiting)
                : BorgTexts.Format(Strings.TaskElsewhere, marker.Started.ToLocalTime().ToString("t", CultureInfo.CurrentCulture));
            var detail = marker.Kind == RunMarkerKind.Backup && marker.RepositoryId is { } repositoryId ? RepositoryName(repositoryId) : null;
            return RunningTaskViewModel.Fixed(title, detail, RunningTaskState.Elsewhere, status,
                show: marker.RepositoryId is { } shown ? () => ShowJob(shown, marker.JobId) : null);
        }));
    }
}
