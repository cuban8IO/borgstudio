using BorgStudio.App.Resources;
using BorgStudio.Core.Backups;
using BorgStudio.Core.Borg;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BorgStudio.App.ViewModels;

/// <summary>What a run is doing: the backup itself, then cleaning up (prune, compact).</summary>
public enum RunPhase
{
    Backup,

    /// <summary>borg prune --dry-run, for the preview of a clean-up started by hand.</summary>
    Previewing,

    Pruning,

    Compacting,
}

/// <summary>
/// A running (or just finished) backup: what borg reports, plus rates and an estimate of the remaining time. The
/// rates look at the last few seconds only, so they follow changes (e.g. many small files, then large ones). After the
/// backup the job's old archives may be cleaned up; a clean-up started by hand is a run without backup
/// (<see cref="IsCleanupOnly"/>).
/// </summary>
public sealed partial class BackupRunViewModel : ObservableObject
{
    private static readonly TimeSpan RateWindow = TimeSpan.FromSeconds(10);

    private readonly TimeProvider _time;
    private readonly DateTimeOffset _started;
    private readonly Queue<(DateTimeOffset At, long Original, long Deduplicated)> _samples = new();

    public BackupRunViewModel(BackupJob job, TimeProvider? time = null, bool cleanupOnly = false)
    {
        _time = time ?? TimeProvider.System;
        _started = _time.GetUtcNow();
        JobId = job.Id;
        JobName = job.Name;
        IsCleanupOnly = cleanupOnly;
        Phase = cleanupOnly ? RunPhase.Previewing : RunPhase.Backup;
        PhaseProgress = new ProgressViewModel(_time);
    }

    public Guid JobId { get; }

    public string JobName { get; }

    /// <summary>Cleaning up by hand, without a backup.</summary>
    public bool IsCleanupOnly { get; }

    public string Title => IsCleanupOnly ? BorgTexts.Format(Strings.CleanupTitle, JobName) : JobName;

    public DateTimeOffset Started => _started;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFinished), nameof(IsBackupPhase), nameof(IsCleanupPhase))]
    public partial bool IsRunning { get; private set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBackupPhase), nameof(IsCleanupPhase), nameof(StatusText))]
    public partial RunPhase Phase { get; private set; }

    /// <summary>The backup is running (its figures are worth showing).</summary>
    public bool IsBackupPhase => IsRunning && Phase == RunPhase.Backup;

    /// <summary>Cleaning up: <see cref="PhaseProgress"/> shows how far.</summary>
    public bool IsCleanupPhase => IsRunning && Phase != RunPhase.Backup;

    /// <summary>Progress of the clean-up phase (prune, compact), with elapsed and remaining time.</summary>
    public ProgressViewModel PhaseProgress { get; }

    /// <summary>The outcome of a clean-up started by hand, once finished.</summary>
    [ObservableProperty]
    public partial CleanupSummary? Cleanup { get; private set; }

    public bool IsFinished => !IsRunning;

    [ObservableProperty]
    public partial bool IsCancelling { get; private set; }

    [ObservableProperty]
    public partial long OriginalSize { get; private set; }

    [ObservableProperty]
    public partial long DeduplicatedSize { get; private set; }

    [ObservableProperty]
    public partial long Files { get; private set; }

    [ObservableProperty]
    public partial string? CurrentPath { get; private set; }

    /// <summary>How much the run will read in total (from the last run or a scan of the sources); <c>null</c> while unknown.</summary>
    [ObservableProperty]
    public partial long? EstimatedTotal { get; private set; }

    /// <summary>New data going into the repository per second (after deduplication and compression).</summary>
    [ObservableProperty]
    public partial double? UploadRate { get; private set; }

    /// <summary>Data read from the sources per second.</summary>
    [ObservableProperty]
    public partial double? ReadRate { get; private set; }

    [ObservableProperty]
    public partial TimeSpan Elapsed { get; private set; }

    [ObservableProperty]
    public partial TimeSpan? Remaining { get; private set; }

    /// <summary>0..1 once the total is known.</summary>
    [ObservableProperty]
    public partial double Fraction { get; private set; }

    [ObservableProperty]
    public partial bool IsIndeterminate { get; private set; } = true;

    /// <summary>The outcome, once finished.</summary>
    [ObservableProperty]
    public partial BackupRunSummary? Summary { get; private set; }

    [ObservableProperty]
    public partial IReadOnlyList<string> Warnings { get; private set; } = [];

    public string ProcessedText => EstimatedTotal is { } total
        ? BorgTexts.Format(Strings.BackupProcessedOf, BorgTexts.Size(OriginalSize), BorgTexts.Size(total), Files)
        : BorgTexts.Format(Strings.BackupProcessed, BorgTexts.Size(OriginalSize), Files);

    public string UploadText => UploadRate is { } rate
        ? BorgTexts.Format(Strings.BackupUpload, BorgTexts.Rate(rate), BorgTexts.Size(DeduplicatedSize))
        : BorgTexts.Format(Strings.BackupUpload, "…", BorgTexts.Size(DeduplicatedSize));

    public string ElapsedText => BorgTexts.Duration(Elapsed);

    public string RemainingText => Remaining is { } remaining
        ? BorgTexts.Format(Strings.BackupRemaining, BorgTexts.Duration(remaining))
        : EstimatedTotal is null ? Strings.BackupRemainingUnknown : Strings.BackupRemainingCalculating;

    public string StatusText => IsCancelling ? Strings.BackupCancelling
        : IsRunning ? Phase switch
        {
            RunPhase.Previewing => Strings.CleanupPreviewing,
            RunPhase.Pruning => Strings.CleanupPruning,
            RunPhase.Compacting => Strings.CleanupCompacting,
            _ => Strings.BackupRunning,
        }
        : Cleanup is { } cleanup ? cleanup.Error is not null ? Strings.OutcomeFailed
            : cleanup.Cancelled ? Strings.OutcomeCancelled : Strings.CleanupDone
        : Summary is { } summary ? BorgTexts.BackupOutcome(summary.Outcome) : "";

    /// <summary>The outcome in words (with the clean-up, if any), once finished.</summary>
    public string? ResultText => Cleanup is { } cleanup ? BorgTexts.Cleanup(cleanup)
        : BackupResultText is not { } backup ? null
        : Summary?.Cleanup is { } afterBackup ? backup + "\n" + BorgTexts.Cleanup(afterBackup)
        : backup;

    private string? BackupResultText => Summary switch
    {
        null => null,
        { Outcome: BackupOutcome.Succeeded or BackupOutcome.Warnings } summary => BorgTexts.Format(Strings.BackupResult,
            BorgTexts.Size(summary.OriginalSize), BorgTexts.Size(summary.CompressedSize), BorgTexts.Size(summary.DeduplicatedSize),
            summary.Files, BorgTexts.Duration(summary.Duration), summary.ArchiveName ?? ""),
        { Outcome: BackupOutcome.Cancelled } => Strings.BackupCancelledText,
        { Message: { } message } => message,
        _ => null,
    };

    public bool HasResult => ResultText is not null;

    public bool HasWarnings => Warnings.Count > 0;

    /// <summary>The first warnings (there can be thousands), with a note how many more there are.</summary>
    public IReadOnlyList<string> WarningsShown => Warnings.Count <= MaximumWarningsShown
        ? Warnings
        : [.. Warnings.Take(MaximumWarningsShown), BorgTexts.Format(Strings.BackupMoreWarnings, Warnings.Count - MaximumWarningsShown)];

    private const int MaximumWarningsShown = 20;

    partial void OnIsRunningChanged(bool value) => OnPropertyChanged(nameof(StatusText));

    partial void OnIsCancellingChanged(bool value) => OnPropertyChanged(nameof(StatusText));

    partial void OnSummaryChanged(BackupRunSummary? value) => OnResultChanged();

    partial void OnCleanupChanged(CleanupSummary? value) => OnResultChanged();

    private void OnResultChanged()
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(ResultText));
        OnPropertyChanged(nameof(HasResult));
    }


    partial void OnWarningsChanged(IReadOnlyList<string> value)
    {
        OnPropertyChanged(nameof(HasWarnings));
        OnPropertyChanged(nameof(WarningsShown));
    }

    partial void OnOriginalSizeChanged(long value) => OnPropertyChanged(nameof(ProcessedText));

    partial void OnFilesChanged(long value) => OnPropertyChanged(nameof(ProcessedText));

    partial void OnEstimatedTotalChanged(long? value) => OnPropertyChanged(nameof(ProcessedText));

    partial void OnUploadRateChanged(double? value) => OnPropertyChanged(nameof(UploadText));

    partial void OnDeduplicatedSizeChanged(long value) => OnPropertyChanged(nameof(UploadText));

    partial void OnElapsedChanged(TimeSpan value) => OnPropertyChanged(nameof(ElapsedText));

    partial void OnRemainingChanged(TimeSpan? value) => OnPropertyChanged(nameof(RemainingText));

    public void Report(BorgCreateProgress progress)
    {
        if (!IsRunning)
            return;

        var now = _time.GetUtcNow();
        OriginalSize = progress.OriginalSize;
        DeduplicatedSize = progress.DeduplicatedSize;
        Files = progress.Files;
        if (!string.IsNullOrEmpty(progress.Path))
            CurrentPath = progress.Path;

        _samples.Enqueue((now, progress.OriginalSize, progress.DeduplicatedSize));
        while (_samples.Count > 2 && now - _samples.Peek().At > RateWindow)
            _samples.Dequeue();
        var oldest = _samples.Peek();
        var seconds = (now - oldest.At).TotalSeconds;
        if (seconds >= 1)
        {
            UploadRate = Math.Max(0, (progress.DeduplicatedSize - oldest.Deduplicated) / seconds);
            ReadRate = Math.Max(0, (progress.OriginalSize - oldest.Original) / seconds);
        }

        Tick();
        UpdateEstimate();
    }

    /// <summary>The total to expect, from the last run or a scan of the sources.</summary>
    public void SetEstimate(long totalBytes)
    {
        if (!IsRunning || totalBytes <= 0)
            return;
        EstimatedTotal = totalBytes;
        UpdateEstimate();
    }

    /// <summary>Called every second, so the elapsed time moves on even while borg is busy with one large file.</summary>
    public void Tick() => Elapsed = _time.GetUtcNow() - _started;

    public void MarkCancelling() => IsCancelling = true;

    /// <summary>The backup is done (or wasn't part of the run); now <paramref name="phase"/> of the clean-up.</summary>
    /// <param name="expected">How long the phase took last time (until borg reports progress).</param>
    public void StartPhase(RunPhase phase, TimeSpan? expected = null)
    {
        Remaining = null;
        Phase = phase;
        PhaseProgress.Start(null, expected);
    }

    /// <summary>How far the clean-up phase is (0..1, from borg).</summary>
    public void ReportPhase(double fraction)
    {
        if (IsRunning && Phase != RunPhase.Backup)
            PhaseProgress.Report(fraction);
    }

    /// <summary>Ends a clean-up started by hand.</summary>
    public void FinishCleanup(CleanupSummary cleanup)
    {
        PhaseProgress.Stop();
        Tick();
        Cleanup = cleanup;
        if (cleanup is { Error: null, Cancelled: false })
        {
            Fraction = 1;
            IsIndeterminate = false;
        }
        IsRunning = false;
        IsCancelling = false;
    }

    public void Finish(BackupRunSummary summary, IReadOnlyList<string> warnings)
    {
        PhaseProgress.Stop();
        Tick();
        Summary = summary;
        Warnings = warnings;
        Remaining = null;
        if (summary.Outcome is BackupOutcome.Succeeded or BackupOutcome.Warnings)
        {
            Fraction = 1;
            IsIndeterminate = false;
        }
        IsRunning = false;
        IsCancelling = false;
    }

    private void UpdateEstimate()
    {
        if (EstimatedTotal is not { } total)
            return;

        IsIndeterminate = false;
        Fraction = Math.Clamp((double)OriginalSize / total, 0, 1);
        var left = total - OriginalSize;
        Remaining = left <= 0 ? TimeSpan.Zero
            : ReadRate is > 0 and var rate ? TimeSpan.FromSeconds(Math.Ceiling(left / rate)) : null;
    }
}
