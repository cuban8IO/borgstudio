using System.Globalization;
using System.Security;
using BorgStudio.App.Resources;
using BorgStudio.App.Services;
using BorgStudio.Core.Backups;
using BorgStudio.Core.Security;
using BorgStudio.Core.Settings;

namespace BorgStudio.App.ViewModels;

/// <summary>What the icon in the notification area shows.</summary>
public enum TrayState
{
    Idle,
    Running,
    Failed,
    Paused,
}

/// <summary>
/// BorgStudio in the background: it starts at login, stays in the notification area and runs the scheduled jobs itself –
/// in the app, with their progress in the window, one after the other. Missed times are made up for once at startup.
/// A PIN-protected vault stays locked for the window: the scheduler opens its own copy, with the key for scheduled runs
/// (or the window's key while the window is unlocked).
/// </summary>
public partial class MainViewModel
{
    /// <summary>A run waiting its turn: due by a schedule, or asked for by the webhook (runs even while schedules are paused).</summary>
    private readonly record struct QueuedRun(Guid JobId, bool Triggered);

    private readonly Queue<QueuedRun> _dueJobs = new();

    /// <summary>Repositories whose scheduled check is due (run after the due jobs).</summary>
    private readonly Queue<Guid> _dueChecks = new();

    /// <summary>Who waits for a job's next run to finish (the webhook with ?wait=1).</summary>
    private readonly Dictionary<Guid, List<TaskCompletionSource<BackupRunSummary?>>> _runWaiters = [];
    private DateTime? _lastScheduleLook;
    private bool _runningDueJobs;
    private Task _dueJobsRun = Task.CompletedTask;

    /// <summary>The scheduled runs started by the last look (tests wait for them).</summary>
    internal Task DueJobsRun => _dueJobsRun;

    /// <summary>The time the scheduler goes by (tests set it).</summary>
    internal Func<DateTime> Clock { get; set; } = () => DateTime.Now;

    /// <summary>BorgStudio runs the schedules itself, from the notification area.</summary>
    public bool IsBackgroundMode => Settings.ScheduleRunner == ScheduleRunner.Background;

    public bool SchedulesPaused => Settings.SchedulesPaused;

    /// <summary>Something the icon in the notification area shows changed (state, jobs, next run, mode).</summary>
    public event EventHandler? BackgroundStateChanged;

    private void RaiseBackgroundStateChanged() => BackgroundStateChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>Switches who starts the scheduled jobs: the system's entries and the start at login follow.</summary>
    public void SetScheduleRunner(ScheduleRunner runner)
    {
        if (runner == Settings.ScheduleRunner)
            return;
        UpdateSettings(Settings with { ScheduleRunner = runner });
        // From now on: switching is no reason to make up for anything.
        _lastScheduleLook = Clock();
        ApplySchedules();
        ApplyAutostart();
        // The webhook only listens in the background.
        ApplyTrigger();
        OnPropertyChanged(nameof(IsBackgroundMode));
        RaiseBackgroundStateChanged();
    }

    public void SetSchedulesPaused(bool paused)
    {
        if (paused == Settings.SchedulesPaused)
            return;
        UpdateSettings(Settings with { SchedulesPaused = paused });
        OnPropertyChanged(nameof(SchedulesPaused));
        RaiseBackgroundStateChanged();
    }

    private void ApplyAutostart()
    {
        if (_services.Autostart is not { } autostart || SchedulerExecutable is not { } executable)
            return;
        try
        {
            if (IsBackgroundMode)
                autostart.Enable(executable);
            else if (autostart.IsEnabled)
                autostart.Disable();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException)
        {
            Notice = BorgTexts.Format(Strings.AutostartFailed, exception.Message);
        }
    }

    /// <summary>
    /// Called every half minute: queues the jobs whose time came since the last look and runs them one after the other.
    /// The first look (at startup) makes up for missed times instead.
    /// </summary>
    public void ScheduleTick()
    {
        var now = Clock();
        if (!IsBackgroundMode)
        {
            _lastScheduleLook = now;
            return;
        }

        var since = _lastScheduleLook;
        _lastScheduleLook = now;
        if (!Settings.SchedulesPaused)
        {
            foreach (var job in _jobs.Where(job => job.IsScheduled))
            {
                var due = since is { } last ? ScheduleCalendar.IsDue(job, last, now) : ScheduleCalendar.IsMissed(job, now);
                if (due && !_dueJobs.Any(queued => queued.JobId == job.Id))
                    _dueJobs.Enqueue(new QueuedRun(job.Id, Triggered: false));
            }
            var checks = CheckStore;
            foreach (var repository in Repositories.Select(item => item.Config).Where(config => config.Check.IsScheduled))
            {
                var policy = repository.Check;
                var due = since is { } last
                    ? ScheduleCalendar.IsDue(policy.Schedules, policy.CatchUp, last, now)
                    : ScheduleCalendar.IsMissed(policy.Schedules, policy.CatchUp, checks.Get(repository.Id).FirstOrDefault()?.Started.LocalDateTime, now);
                if (due && !_dueChecks.Contains(repository.Id))
                    _dueChecks.Enqueue(repository.Id);
            }
            StartDueJobs();
        }
        // The daily check (summary, overdue jobs) – also while the schedules are paused.
        if (_dailyCheckRun.IsCompleted)
            _dailyCheckRun = RunDailyCheckIfDueAsync(now);
        // The next run moves on.
        RaiseBackgroundStateChanged();
    }

    private Task _dailyCheckRun = Task.CompletedTask;

    /// <summary>The daily check started by the last look (tests wait for it).</summary>
    internal Task DailyCheckRun => _dailyCheckRun;

    private async Task RunDailyCheckIfDueAsync(DateTime now)
    {
        var check = new DailyCheck(_services);
        if (!Settings.Notifications.NeedsDailyCheck(_jobs) || !check.State.IsDue(Settings.Notifications.DailyTime, now))
            return;
        var vault = OpenVaultCopy(scheduledAccess: true);
        try
        {
            await Task.Run(() => check.RunAsync(now, vault));
        }
        catch (Exception exception)
        {
            AppLog.Write(RunNotifications.LogFile, $"Daily check: {exception}", _services.Jobs.DataDirectory);
        }
        finally
        {
            vault.Lock();
        }
    }

    private void StartDueJobs()
    {
        if ((_dueJobs.Count > 0 || _dueChecks.Count > 0) && !_runningDueJobs)
            _dueJobsRun = RunDueJobsAsync();
    }

    private async Task RunDueJobsAsync()
    {
        _runningDueJobs = true;
        try
        {
            do
            {
                await RunQueuedJobsAsync();
                // The checks after the backups: they wait for the repository anyway.
                while (_dueJobs.Count == 0 && _dueChecks.TryDequeue(out var repositoryId))
                {
                    while (IsBackupRunning)
                        await Task.WhenAny(BackupFinished, Task.Delay(TimeSpan.FromSeconds(2)));
                    if (!Settings.SchedulesPaused && Repositories.FirstOrDefault(item => item.Config.Id == repositoryId) is { } item)
                        await RunScheduledCheckAsync(item.Config);
                }
            }
            while (_dueJobs.Count > 0 || _dueChecks.Count > 0);
        }
        finally
        {
            _runningDueJobs = false;
        }
    }

    /// <summary>A scheduled check in the background: without questions, recorded and reported; the overview follows.</summary>
    private async Task RunScheduledCheckAsync(Core.Repositories.RepositoryConfig repository)
    {
        var vault = OpenVaultCopy(scheduledAccess: false);
        var services = _services with { Vault = vault };
        try
        {
            var record = await Task.Run(() => new RepositoryChecks(services).RunScheduledAsync(repository,
                new ScheduledAccess(vault, _services.SecretStore, ScheduledAccess.FallbackFile)));
            ScheduledRun.Log($"{repository.Name} (background check): {record.Outcome}{(record.Error is null ? "" : $" – {record.Error}")}",
                _services.Jobs.DataDirectory);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ScheduledRun.Log($"{repository.Name} (background check): {exception}", _services.Jobs.DataDirectory);
        }
        finally
        {
            vault.Lock();
        }
        RefreshChecks(repository.Id);
        RefreshRepositoryJobs();
        RefreshDashboard();
    }

    private async Task RunQueuedJobsAsync()
    {
        while (_dueJobs.TryDequeue(out var queued))
        {
            // A backup started in the window goes first.
            while (IsBackupRunning)
                await Task.WhenAny(BackupFinished, Task.Delay(TimeSpan.FromSeconds(2)));
            var job = _jobs.FirstOrDefault(candidate => candidate.Id == queued.JobId);
            BackupRunSummary? summary = null;
            if (job is not null && (queued.Triggered || (job.IsScheduled && !Settings.SchedulesPaused)))
            {
                var run = RunScheduledAsync(job);
                _backupTask = run;
                summary = await run;
            }
            FinishWaiters(queued.JobId, summary);
        }
    }

    private void FinishWaiters(Guid jobId, BackupRunSummary? summary)
    {
        if (!_runWaiters.Remove(jobId, out var waiters))
            return;
        foreach (var waiter in waiters)
            waiter.TrySetResult(summary);
    }

    /// <summary>
    /// A copy of the vault, opened with the window's key while the window is unlocked; otherwise locked or – with
    /// <paramref name="scheduledAccess"/> – opened with the key for scheduled runs. Opening and locking it leaves the
    /// window's vault alone.
    /// </summary>
    private Vault OpenVaultCopy(bool scheduledAccess)
    {
        var vault = new Vault(_services.Vault.FilePath);
        if (_services.Vault.State == VaultState.Unlocked)
        {
            try
            {
                vault.UnlockWithKey(_services.Vault.ExportKey());
            }
            catch (Exception exception) when (exception is VaultLockedException or SecretBoxException)
            {
                // Locked in the meantime: the key for scheduled runs it is.
            }
        }
        if (scheduledAccess && vault.State == VaultState.Locked)
        {
            try
            {
                new ScheduledAccess(vault, _services.SecretStore, ScheduledAccess.FallbackFile).TryUnlock();
            }
            catch (SecretBoxException)
            {
                // The stored key no longer fits: the secrets stay unreadable.
            }
        }
        return vault;
    }

    /// <summary>Reports a run of the window through the notification channels (in the background, never failing).</summary>
    private async Task NotifyRunAsync(BackupJob job, BackupRunSummary summary)
    {
        if (_services.Notifier is null || !job.Notify)
            return;
        var vault = OpenVaultCopy(scheduledAccess: true);
        try
        {
            await Task.Run(() => new RunNotifications(_services).NotifyAsync(job, summary, vault));
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

    /// <summary>A run without questions, with its card in the window; <c>null</c> if it crashed.</summary>
    private async Task<BackupRunSummary?> RunScheduledAsync(BackupJob job)
    {
        // The scheduler's own copy of the vault (it opens it with its key itself, if needed).
        var vault = OpenVaultCopy(scheduledAccess: false);
        var services = _services with { Vault = vault };
        var runner = new ScheduledJobRunner(services, new ScheduledAccess(vault, _services.SecretStore, ScheduledAccess.FallbackFile));

        try
        {
            var summary = (await RunWithCardAsync(job, (report, cancellationToken) => runner.RunAsync(job.Id, report, cancellationToken))).Summary;
            var reason = summary.SkipReason is { } skip ? BorgTexts.SkipReason(skip) : summary.Message;
            ScheduledRun.Log($"{job.Name} (background): {summary.Outcome}{(reason is null ? "" : $" – {reason}")}", _services.Jobs.DataDirectory);
            return summary;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ScheduledRun.Log($"{job.Name} (background): {exception}", _services.Jobs.DataDirectory);
            Notice = BorgTexts.Format(Strings.BackgroundRunFailed, job.Name, exception.Message);
            return null;
        }
        finally
        {
            vault.Lock();
        }
    }

    /// <summary>"Jetzt sichern" in the icon's menu: shows the job in the window and runs it there (it may ask questions).</summary>
    public async Task RunJobNowAsync(Guid jobId)
    {
        if (_jobs.FirstOrDefault(job => job.Id == jobId) is not { } job
            || Repositories.FirstOrDefault(item => item.Config.Id == job.RepositoryId) is not { } repository)
            return;
        SelectedRepository = repository;
        SelectedDetailsTab = JobsTab;
        SelectedJob = RepositoryJobs.FirstOrDefault(item => item.Job.Id == jobId);
        if (RunJobCommand.CanExecute(null))
            await RunJobCommand.ExecuteAsync(null);
    }

    public TrayState TrayState =>
        IsBackupRunning ? TrayState.Running
        : FailedJob is not null ? TrayState.Failed
        : Settings.SchedulesPaused ? TrayState.Paused
        : TrayState.Idle;

    /// <summary>A job whose last run failed (shown at the icon until it succeeds again).</summary>
    private BackupJob? FailedJob => _jobs.FirstOrDefault(job => job.LastRun?.Outcome == BackupOutcome.Failed);

    /// <summary>The icon's tooltip: "BorgStudio – Sichert: Dokumente".</summary>
    public string TrayToolTip => TrayState switch
    {
        TrayState.Running => $"BorgStudio – {BorgTexts.Format(Strings.TrayRunning, CurrentBackup?.Title ?? "")}",
        TrayState.Failed => $"BorgStudio – {BorgTexts.Format(Strings.TrayFailed, FailedJob!.Name)}",
        TrayState.Paused => $"BorgStudio – {Strings.TrayPaused}",
        _ => $"BorgStudio – {NextScheduledText}",
    };

    /// <summary>"Nächste: Dokumente, Di. 20:00", or that nothing is scheduled.</summary>
    public string NextScheduledText
    {
        get
        {
            if (Settings.SchedulesPaused)
                return Strings.TrayPaused;
            var now = Clock();
            var next = _jobs.Where(job => job.IsScheduled)
                .Select(job => (Job: job, At: ScheduleCalendar.Next(job.Schedules, now)))
                .Where(entry => entry.At is not null)
                .OrderBy(entry => entry.At)
                .FirstOrDefault();
            return next.At is { } at
                ? BorgTexts.Format(Strings.TrayNext, next.Job.Name,
                    $"{at.ToString("ddd", CultureInfo.CurrentCulture)} {at.ToString("t", CultureInfo.CurrentCulture)}")
                : Strings.TrayNothingScheduled;
        }
    }
}
