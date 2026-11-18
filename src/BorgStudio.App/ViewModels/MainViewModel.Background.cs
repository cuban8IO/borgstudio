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
    private readonly Queue<Guid> _dueJobs = new();
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
                if (due && !_dueJobs.Contains(job.Id))
                    _dueJobs.Enqueue(job.Id);
            }
            if (_dueJobs.Count > 0 && !_runningDueJobs)
                _dueJobsRun = RunDueJobsAsync();
        }
        // The next run moves on.
        RaiseBackgroundStateChanged();
    }

    private async Task RunDueJobsAsync()
    {
        _runningDueJobs = true;
        try
        {
            while (_dueJobs.TryDequeue(out var id))
            {
                // A backup started in the window goes first.
                while (IsBackupRunning)
                    await Task.WhenAny(BackupFinished, Task.Delay(TimeSpan.FromSeconds(2)));
                if (_jobs.FirstOrDefault(job => job.Id == id) is not { IsScheduled: true } job || Settings.SchedulesPaused)
                    continue;
                var run = RunScheduledAsync(job);
                _backupTask = run;
                await run;
            }
        }
        finally
        {
            _runningDueJobs = false;
        }
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

    private async Task RunScheduledAsync(BackupJob job)
    {
        // The scheduler's own copy of the vault (it opens it with its key itself, if needed).
        var vault = OpenVaultCopy(scheduledAccess: false);
        var services = _services with { Vault = vault };
        var runner = new ScheduledJobRunner(services, new ScheduledAccess(vault, _services.SecretStore, ScheduledAccess.FallbackFile));

        try
        {
            var summary = await RunWithCardAsync(job, (report, cancellationToken) => runner.RunAsync(job.Id, report, cancellationToken));
            var reason = summary.SkipReason is { } skip ? BorgTexts.SkipReason(skip) : summary.Message;
            ScheduledRun.Log($"{job.Name} (background): {summary.Outcome}{(reason is null ? "" : $" – {reason}")}", _services.Jobs.DataDirectory);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ScheduledRun.Log($"{job.Name} (background): {exception}", _services.Jobs.DataDirectory);
            Notice = BorgTexts.Format(Strings.BackgroundRunFailed, job.Name, exception.Message);
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
