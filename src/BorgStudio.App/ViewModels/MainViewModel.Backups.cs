using System.Collections.ObjectModel;
using System.Globalization;
using BorgStudio.App.Resources;
using BorgStudio.App.Services;
using BorgStudio.Core.Backups;
using BorgStudio.Core.Borg;
using BorgStudio.Core.Notifications;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BorgStudio.App.ViewModels;

/// <summary>Backup jobs: the ones of the selected repository, editing them, and running one at a time.</summary>
public partial class MainViewModel
{
    private readonly List<BackupJob> _jobs = [];
    private CancellationTokenSource? _backupCancellation;
    private Task _backupTask = Task.CompletedTask;

    /// <summary>The jobs of the selected repository.</summary>
    public ObservableCollection<BackupJobItemViewModel> RepositoryJobs { get; } = [];

    public bool HasRepositoryJobs => RepositoryJobs.Count > 0;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EditJobCommand), nameof(DeleteJobCommand), nameof(RunJobCommand), nameof(CleanUpJobCommand), nameof(TestRestoreCommand))]
    public partial BackupJobItemViewModel? SelectedJob { get; set; }

    /// <summary>The running backup, or the last one (with its outcome) until the next starts.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBackupRun))]
    public partial BackupRunViewModel? CurrentBackup { get; private set; }

    public bool HasBackupRun => CurrentBackup is not null;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunJobCommand), nameof(CancelBackupCommand), nameof(DeleteJobCommand), nameof(EditJobCommand),
        nameof(TestRepositoryCommand), nameof(ExportKeyCommand), nameof(RemoveRepositoryCommand), nameof(EditRepositoryCommand),
        nameof(CleanUpJobCommand), nameof(TestRestoreCommand), nameof(FreeSpaceCommand), nameof(MeasureStorageCommand), nameof(CheckRepositoryCommand),
        nameof(CheckRepositoryFullCommand), nameof(RepairRepositoryCommand), nameof(BreakLockCommand))]
    public partial bool IsBackupRunning { get; private set; }

    /// <summary>Completes when the running backup (if any) has ended.</summary>
    public Task BackupFinished => _backupTask;

    public IReadOnlyList<BackupJob> AllJobs => _jobs;

    private void LoadJobs()
    {
        _jobs.AddRange(_services.Jobs.Load());
        RefreshRepositoryJobs();
    }

    private void SaveJobs()
    {
        _services.Jobs.Save(_jobs);
        ApplySchedules();
    }

    partial void OnSelectedRepositoryChanged(RepositoryItemViewModel? value)
    {
        RefreshRepositoryJobs();
        ResetArchives();
        RefreshStatistics();
        // Another repository chosen (added, removed, opened from the tray): it is shown, not the overview.
        if (value is not null)
            IsOverviewShown = false;
    }

    private void RefreshRepositoryJobs()
    {
        var selected = SelectedJob?.Job.Id;
        RepositoryJobs.Clear();
        if (SelectedRepository is { } repository)
            foreach (var job in _jobs.Where(job => job.RepositoryId == repository.Config.Id))
                RepositoryJobs.Add(new BackupJobItemViewModel(job, RestoreTestStore.Get(job.Id).FirstOrDefault()));
        SelectedJob = RepositoryJobs.FirstOrDefault(item => item.Job.Id == selected) ?? RepositoryJobs.FirstOrDefault();
        OnPropertyChanged(nameof(HasRepositoryJobs));
        NewJobCommand.NotifyCanExecuteChanged();
        RaiseBackgroundStateChanged();
    }

    /// <summary>Replaces all jobs (imports).</summary>
    public void ReplaceJobs(IEnumerable<BackupJob> jobs)
    {
        _jobs.Clear();
        _jobs.AddRange(jobs);
        SaveJobs();
        RefreshRepositoryJobs();
    }

    /// <summary>Removes the jobs of a repository that is being removed.</summary>
    private void RemoveJobsOf(Guid repositoryId)
    {
        if (_jobs.RemoveAll(job => job.RepositoryId == repositoryId) > 0)
            SaveJobs();
        RefreshRepositoryJobs();
        _services.Archives.Remove(repositoryId);
        _services.Durations.Remove(repositoryId);
        StorageStore.Remove(repositoryId);
        CheckStore.Remove(repositoryId);
        NotificationPolicy.ForDataDirectory(_services.Jobs.DataDirectory).ForgetRepository(repositoryId);
    }

    // --- New / edit / delete ---

    [RelayCommand(CanExecute = nameof(CanAddJob))]
    private async Task NewJobAsync()
    {
        var editor = new BackupJobEditorViewModel(SelectedRepository!.Config, Borg, _services.Dialogs);
        await _services.Dialogs.ShowBackupJobEditorAsync(editor);
        if (editor.Result is not { } job)
            return;

        await PrepareScheduleAsync(job);
        _jobs.Add(job);
        SaveJobs();
        RefreshRepositoryJobs();
        SelectedJob = RepositoryJobs.FirstOrDefault(item => item.Job.Id == job.Id);
    }

    private bool CanAddJob() => SelectedRepository is not null;

    [RelayCommand(CanExecute = nameof(CanChangeJob))]
    private async Task EditJobAsync()
    {
        var editor = new BackupJobEditorViewModel(SelectedRepository!.Config, Borg, _services.Dialogs, SelectedJob!.Job);
        await _services.Dialogs.ShowBackupJobEditorAsync(editor);
        if (editor.Result is not { } job)
            return;

        if (!SelectedJob!.Job.IsScheduled)
            await PrepareScheduleAsync(job);
        _jobs[_jobs.FindIndex(existing => existing.Id == job.Id)] = job;
        SaveJobs();
        RefreshRepositoryJobs();
    }

    private bool CanChangeJob() => SelectedJob is not null && !(IsBackupRunning && CurrentBackup?.JobId == SelectedJob.Job.Id);

    [RelayCommand(CanExecute = nameof(CanChangeJob))]
    private async Task DeleteJobAsync()
    {
        var job = SelectedJob!.Job;
        if (!await _services.Dialogs.ConfirmAsync(Strings.JobDeleteTitle, BorgTexts.Format(Strings.JobDeleteMessage, job.Name), Strings.Delete))
            return;

        _jobs.RemoveAll(existing => existing.Id == job.Id);
        SaveJobs();
        Runner.History.Remove(job.Id);
        RestoreTestStore.Remove(job.Id);
        NotificationPolicy.ForDataDirectory(_services.Jobs.DataDirectory).Forget(job.Id);
        RefreshRepositoryJobs();
    }

    // --- Running ---

    [RelayCommand(CanExecute = nameof(CanRunJob))]
    private Task RunJobAsync()
    {
        _backupTask = RunBackupAsync(SelectedJob!.Job);
        return _backupTask;
    }

    private bool CanRunJob() =>
        SelectedJob is not null && !IsBackupRunning && !IsWorking && Borg is { Support: BorgSupport.Supported };

    [RelayCommand(CanExecute = nameof(IsBackupRunning))]
    private void CancelBackup()
    {
        CurrentBackup?.MarkCancelling();
        _backupCancellation?.Cancel();
    }

    private async Task RunBackupAsync(BackupJob job)
    {
        var item = Repositories.FirstOrDefault(repository => repository.Config.Id == job.RepositoryId);
        if (item is null || await ConnectAsync(item) is not { } location)
            return;
        if (!await ReleaseMountsAsync(item.Config.Id, item.Name))
            return;
        // Login and passphrase first: once the backup runs, nothing may wait for an answer.
        if (await GetSshAccessAsync(item, location) is not { } ssh)
            return;
        if (await GetPassphraseAsync(item) is not { } passphrase)
            return;
        // A scheduled backup started while the questions were open.
        if (IsBackupRunning)
            return;

        var borg = Borg!;
        Core.Checks.CheckRecord? check = null;
        var checks = new RepositoryChecks(_services);
        var throttle = Throttle(item.Config, location);
        var result = await RunWithCardAsync(job, async (report, cancellationToken) =>
        {
            var backup = await Runner.RunAsync(new BackupRunRequest(borg, location, job, passphrase.Value, ssh.Access, Throttle: throttle), report,
                cancellationToken);
            // Checked after the backup, if the repository's policy says one is due (with the jobs' restore tests).
            if (backup.Summary.Outcome is BackupOutcome.Succeeded or BackupOutcome.Warnings)
                check = await checks.CheckAfterBackupAsync(item.Config, new UnattendedAccess(borg, location, passphrase.Value, ssh.Access, throttle), report,
                    cancellationToken);
            return backup;
        });
        var summary = result.Summary;
        // Turned away (another run is busy): that one measures and reports.
        if (summary.SkipReason == BackupSkipReason.RepositoryBusy)
            return;
        var usage = await MeasureAfterBackupAsync(item.Config, borg, location, passphrase.Value, ssh.Access, result.Repository);
        await NotifyRunAsync(job, summary);
        await NotifyStorageAsync(item.Config, usage);
        if (check is not null)
            await NotifyCheckAsync(item.Config, check);
        foreach (var (tested, test) in checks.LastRestoreTests)
            await NotifyRestoreTestAsync(tested, item.Name, test);
    }

    /// <summary>The repository's limits for a run starting now (the upload limit only for a server).</summary>
    private static Core.Repositories.BorgThrottle Throttle(Core.Repositories.RepositoryConfig repository, Plugins.RepositoryLocation location) =>
        repository.Limits.ThrottleAt(DateTime.Now, location.Ssh is not null);

    /// <summary>
    /// Runs a backup with its card in the window: progress, the expected total, cancelling. The window's own runs and the
    /// ones the background scheduler starts.
    /// </summary>
    private async Task<BackupRunResult> RunWithCardAsync(BackupJob job,
        Func<IBackupRunReport, CancellationToken, Task<BackupRunResult>> start)
    {
        var run = new BackupRunViewModel(job);
        CurrentBackup = run;
        IsBackupRunning = true;
        using var cancellation = new CancellationTokenSource();
        _backupCancellation = cancellation;
        using var ticker = new CancellationTokenSource();
        _ = TickAsync(run, ticker.Token);

        // The total to expect: what the last run read, or a quick scan of the sources (first run).
        if (job.LastRun is { Outcome: BackupOutcome.Succeeded or BackupOutcome.Warnings, OriginalSize: > 0 } last)
            run.SetEstimate(last.OriginalSize);
        else
            _ = EstimateAsync(run, job, ticker.Token);

        // borg create, then the job's clean-up; the runner records the outcome with the job and in its history.
        BackupRunResult result;
        try
        {
            result = await start(new RunReport(this, run), cancellation.Token);
        }
        catch
        {
            IsBackupRunning = false;
            throw;
        }
        finally
        {
            ticker.Cancel();
            _backupCancellation = null;
        }

        var summary = result.Summary;
        run.Finish(summary, result.Warnings);
        var index = _jobs.FindIndex(existing => existing.Id == job.Id);
        // A run turned away (another one is busy) leaves the job's last run to that one.
        if (index >= 0 && summary.Outcome != BackupOutcome.Skipped)
        {
            _jobs[index] = _jobs[index] with { LastRun = summary };
            RefreshRepositoryJobs();
        }
        IsBackupRunning = false;
        if (summary.Outcome != BackupOutcome.Skipped)
            ArchivesChanged(job.RepositoryId);
        if (Repositories.FirstOrDefault(repository => repository.Config.Id == job.RepositoryId) is { } runItem
            && summary.Outcome is not (BackupOutcome.Skipped or BackupOutcome.Cancelled))
        {
            if (summary.RepositoryLocked)
                runItem.LockedSince = DateTimeOffset.Now;
            else if (summary.Outcome != BackupOutcome.Failed)
                runItem.LockedSince = null;
            BreakLockCommand.NotifyCanExecuteChanged();
        }
        // A scheduled run in the background has measured the storage meanwhile (and may have checked the repository).
        RefreshChecks(job.RepositoryId);
        RefreshStorage(job.RepositoryId);
        return result;
    }

    /// <summary>Runs backups and clean-ups – the same for the window, the background and scheduled runs.</summary>
    internal BackupRunner Runner => _runner ??= new BackupRunner(_services.BorgClient, _services.Jobs, _services.Durations, BorgTexts.Describe,
        hooks: _services.Hooks, describeHook: BorgTexts.HookProblem);

    private BackupRunner? _runner;

    /// <summary>"Dokumente & Fotos" → "Dokumente-Fotos" (see <see cref="BackupNames.ArchivePrefix"/>).</summary>
    public static string ArchivePrefix(string jobName) => BackupNames.ArchivePrefix(jobName);

    /// <summary>The archives this computer created for the job (see <see cref="BackupNames.ArchiveGlob"/>).</summary>
    public static string ArchiveGlob(string jobName) => BackupNames.ArchiveGlob(jobName);

    private async Task TickAsync(BackupRunViewModel run, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                OnViewModelThread(run.Tick);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task EstimateAsync(BackupRunViewModel run, BackupJob job, CancellationToken cancellationToken)
    {
        try
        {
            var size = await Task.Run(() => SourceScanner.Scan(job.Sources, job.Exclusions, cancellationToken), cancellationToken);
            OnViewModelThread(() => run.SetEstimate(size.Bytes));
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Shows what the runner reports in the run's card (on the view model's thread).</summary>
    private sealed class RunReport(MainViewModel owner, BackupRunViewModel run) : IBackupRunReport
    {
        public void Created(BorgCreateProgress progress) => owner.OnViewModelThread(() => run.Report(progress));

        public void PhaseStarted(BackupRunPhase phase, TimeSpan? expected) => owner.OnViewModelThread(() => run.StartPhase(
            phase switch
            {
                BackupRunPhase.Pruning => RunPhase.Pruning,
                BackupRunPhase.Compacting => RunPhase.Compacting,
                BackupRunPhase.Checking => RunPhase.Checking,
                BackupRunPhase.RestoreTesting => RunPhase.RestoreTesting,
                BackupRunPhase.HookBefore => RunPhase.HookBefore,
                BackupRunPhase.HookAfter => RunPhase.HookAfter,
                _ => RunPhase.Backup,
            }, expected));

        public void PhaseProgress(double fraction) => owner.OnViewModelThread(() => run.ReportPhase(fraction));
    }

    /// <summary>Hands progress to the view model's thread (borg reports on a background thread).</summary>
    private sealed class ViewModelProgress<T>(MainViewModel owner, Action<T> report) : IProgress<T>
    {
        public void Report(T value) => owner.OnViewModelThread(() => report(value));
    }
}

/// <summary>A job in the "Backup jobs" tab.</summary>
public sealed class BackupJobItemViewModel(BackupJob job, Core.Checks.RestoreTestRecord? lastRestoreTest = null)
{
    public BackupJob Job { get; } = job;

    /// <summary>The job's last restore test; <c>null</c> if it never had one.</summary>
    public Core.Checks.RestoreTestRecord? LastRestoreTest { get; } = lastRestoreTest;

    /// <summary>"Test-Wiederherstellung 20.11.2026 14:03: erfolgreich – 20 Dateien (84 MB), 14 verglichen".</summary>
    public string RestoreTestText => LastRestoreTest is { } test ? BorgTexts.RestoreTest(test) : "";

    public bool HasRestoreTest => LastRestoreTest is not null;

    public bool RestoreTestFailed => LastRestoreTest?.Outcome == Core.Checks.RestoreTestOutcome.Failed;

    public bool RestoreTestNotRun => LastRestoreTest?.Outcome == Core.Checks.RestoreTestOutcome.NotRun;

    public string Name => Job.Name;

    public string SourcesText => string.Join(", ", Job.Sources);

    public string LastRunText => Job.LastRun switch
    {
        null => Strings.JobNeverRun,
        { Outcome: BackupOutcome.Succeeded or BackupOutcome.Warnings } last => BorgTexts.Format(Strings.JobLastRun,
            last.Started.ToLocalTime().ToString("g", CultureInfo.CurrentCulture), BorgTexts.BackupOutcome(last.Outcome),
            BorgTexts.Size(last.OriginalSize), BorgTexts.Size(last.DeduplicatedSize)),
        // Sizes of an interrupted run say little.
        var last => BorgTexts.Format(Strings.JobLastRunShort,
            last.Started.ToLocalTime().ToString("g", CultureInfo.CurrentCulture), BorgTexts.BackupOutcome(last.Outcome)),
    };

    public bool LastRunFailed => Job.LastRun?.Outcome is BackupOutcome.Failed;

    public bool LastRunWarned => Job.LastRun?.Outcome is BackupOutcome.Warnings or BackupOutcome.Cancelled;

    /// <summary>"Behält: 7 täglich, 4 wöchentlich, 6 monatlich".</summary>
    public string RetentionText => BorgTexts.Retention(Job.Retention) is { } rules
        ? BorgTexts.Format(Strings.JobRetention, rules)
        : Strings.JobRetentionNone;

    public bool HasRetention => Job.Retention is { HasRules: true };

    public bool IsScheduled => Job.IsScheduled;

    /// <summary>"Startet nur: im Netzbetrieb, über LAN"; empty without conditions (or schedule).</summary>
    public string ConditionsText => Job.IsScheduled ? BorgTexts.Conditions(Job.Conditions) ?? "" : "";

    public bool HasConditions => ConditionsText.Length > 0;

    /// <summary>"Täglich 20:00 · Mo, Di 12:30 · nächster Lauf: Mo., 17.11. 20:00".</summary>
    public string ScheduleText => !Job.IsScheduled ? Strings.JobNotScheduled
        : ScheduleCalendar.Next(Job.Schedules, DateTime.Now) is { } next
            ? BorgTexts.Format(Strings.JobSchedule, BorgTexts.Schedules(Job.Schedules), $"{next.ToString("ddd", CultureInfo.CurrentCulture)} {next.ToString("g", CultureInfo.CurrentCulture)}")
            : BorgTexts.Schedules(Job.Schedules);
}
