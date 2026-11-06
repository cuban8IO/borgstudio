using System.Collections.ObjectModel;
using System.Globalization;
using BorgStudio.App.Resources;
using BorgStudio.Core.Backups;
using BorgStudio.Core.Borg;
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
    [NotifyCanExecuteChangedFor(nameof(EditJobCommand), nameof(DeleteJobCommand), nameof(RunJobCommand))]
    public partial BackupJobItemViewModel? SelectedJob { get; set; }

    /// <summary>The running backup, or the last one (with its outcome) until the next starts.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBackupRun))]
    public partial BackupRunViewModel? CurrentBackup { get; private set; }

    public bool HasBackupRun => CurrentBackup is not null;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunJobCommand), nameof(CancelBackupCommand), nameof(DeleteJobCommand), nameof(EditJobCommand),
        nameof(TestRepositoryCommand), nameof(ExportKeyCommand), nameof(RemoveRepositoryCommand), nameof(EditRepositoryCommand))]
    public partial bool IsBackupRunning { get; private set; }

    /// <summary>Completes when the running backup (if any) has ended.</summary>
    public Task BackupFinished => _backupTask;

    public IReadOnlyList<BackupJob> AllJobs => _jobs;

    private void LoadJobs()
    {
        _jobs.AddRange(_services.Jobs.Load());
        RefreshRepositoryJobs();
    }

    private void SaveJobs() => _services.Jobs.Save(_jobs);

    partial void OnSelectedRepositoryChanged(RepositoryItemViewModel? value)
    {
        RefreshRepositoryJobs();
        ResetArchives();
    }

    private void RefreshRepositoryJobs()
    {
        var selected = SelectedJob?.Job.Id;
        RepositoryJobs.Clear();
        if (SelectedRepository is { } repository)
            foreach (var job in _jobs.Where(job => job.RepositoryId == repository.Config.Id))
                RepositoryJobs.Add(new BackupJobItemViewModel(job));
        SelectedJob = RepositoryJobs.FirstOrDefault(item => item.Job.Id == selected) ?? RepositoryJobs.FirstOrDefault();
        OnPropertyChanged(nameof(HasRepositoryJobs));
        NewJobCommand.NotifyCanExecuteChanged();
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
    }

    // --- New / edit / delete ---

    [RelayCommand(CanExecute = nameof(CanAddJob))]
    private async Task NewJobAsync()
    {
        var editor = new BackupJobEditorViewModel(SelectedRepository!.Config, Borg, _services.Dialogs);
        await _services.Dialogs.ShowBackupJobEditorAsync(editor);
        if (editor.Result is not { } job)
            return;

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
        if (item is null || !TryGetLocation(item, out var location))
            return;
        // Login and passphrase first: once the backup runs, nothing may wait for an answer.
        if (await GetSshAccessAsync(item, location) is not { } ssh)
            return;
        if (await GetPassphraseAsync(item) is not { } passphrase)
            return;

        var borg = Borg!;
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

        var options = new List<string> { "--compression", job.Compression };
        options.AddRange(BackupPatterns.Arguments(job.Exclusions, path => BorgClient.BorgPath(borg, path)));
        var archive = $"{ArchivePrefix(job.Name)}-{{hostname}}-{{now:%Y-%m-%dT%H:%M:%S}}";
        var progress = new ViewModelProgress<BorgCreateProgress>(this, run.Report);

        BorgResult<BorgCreateResult> result;
        try
        {
            result = await _services.BorgClient.CreateAsync(borg, location, archive, job.Sources, options, passphrase.Value,
                ssh.Access, progress, cancellation.Token);
        }
        finally
        {
            ticker.Cancel();
            _backupCancellation = null;
        }

        var summary = Summarize(run, result);
        run.Finish(summary, result.Value?.Warnings ?? []);
        var index = _jobs.FindIndex(existing => existing.Id == job.Id);
        if (index >= 0)
        {
            _jobs[index] = _jobs[index] with { LastRun = summary };
            SaveJobs();
            RefreshRepositoryJobs();
        }
        IsBackupRunning = false;
        ArchivesChanged(job.RepositoryId);
    }

    private static BackupRunSummary Summarize(BackupRunViewModel run, BorgResult<BorgCreateResult> result)
    {
        if (result.Value is { } created)
        {
            return new BackupRunSummary
            {
                Started = run.Started,
                Duration = run.Elapsed,
                Outcome = created.Warnings.Count > 0 ? BackupOutcome.Warnings : BackupOutcome.Succeeded,
                ArchiveName = created.ArchiveName,
                OriginalSize = created.OriginalSize,
                CompressedSize = created.CompressedSize,
                DeduplicatedSize = created.DeduplicatedSize,
                Files = created.Files,
                Message = created.Warnings.FirstOrDefault(),
            };
        }

        var cancelled = result.Error!.Kind == BorgErrorKind.Cancelled;
        return new BackupRunSummary
        {
            Started = run.Started,
            Duration = run.Elapsed,
            Outcome = cancelled ? BackupOutcome.Cancelled : BackupOutcome.Failed,
            OriginalSize = run.OriginalSize,
            DeduplicatedSize = run.DeduplicatedSize,
            Files = run.Files,
            Message = cancelled ? null : BorgTexts.Describe(result.Error),
        };
    }

    /// <summary>"Dokumente & Fotos" → "Dokumente-Fotos": safe in an archive name and for later per-job clean-ups.</summary>
    public static string ArchivePrefix(string jobName)
    {
        var safe = new string(jobName.Trim().Select(c => char.IsLetterOrDigit(c) || c is '_' or '.' ? c : '-').ToArray());
        while (safe.Contains("--", StringComparison.Ordinal))
            safe = safe.Replace("--", "-", StringComparison.Ordinal);
        safe = safe.Trim('-');
        return safe.Length > 0 ? safe : "backup";
    }

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

    /// <summary>Hands progress to the view model's thread (borg reports on a background thread).</summary>
    private sealed class ViewModelProgress<T>(MainViewModel owner, Action<T> report) : IProgress<T>
    {
        public void Report(T value) => owner.OnViewModelThread(() => report(value));
    }
}

/// <summary>A job in the "Backup jobs" tab.</summary>
public sealed class BackupJobItemViewModel(BackupJob job)
{
    public BackupJob Job { get; } = job;

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
}
