using BorgStudio.Core.Borg;
using BorgStudio.Core.Progress;
using BorgStudio.Plugins;

namespace BorgStudio.Core.Backups;

/// <summary>What a run is doing after the backup itself: cleaning up the job's old archives.</summary>
public enum BackupRunPhase
{
    Backup,
    Pruning,
    Compacting,
}

/// <summary>Follows a run while it works; called on background threads.</summary>
public interface IBackupRunReport
{
    /// <summary>borg create's progress.</summary>
    void Created(BorgCreateProgress progress);

    /// <summary>A clean-up phase starts; <paramref name="expected"/> is how long it took last time.</summary>
    void PhaseStarted(BackupRunPhase phase, TimeSpan? expected);

    /// <summary>How far the clean-up phase is (0..1).</summary>
    void PhaseProgress(double fraction);
}

/// <summary>Everything a run needs that the caller has to find out first (with questions, if it may ask).</summary>
/// <param name="Scheduled">Started by a schedule: a busy repository then counts as a failed run, recorded with the job.</param>
public sealed record BackupRunRequest(BorgInstallation Borg, RepositoryLocation Location, BackupJob Job, string? Passphrase, SshAccess? Ssh,
    bool Scheduled = false);

/// <summary>A finished run: its summary (also saved with the job and in its history) and borg's warnings.</summary>
/// <param name="Repository">The repository's statistics after the run (borg reports them with the backup; clean-up subtracted).</param>
public sealed record BackupRunResult(BackupRunSummary Summary, IReadOnlyList<string> Warnings, Storage.RepositoryStats? Repository = null);

/// <summary>
/// Runs a backup job: <c>borg create</c>, then – if the job keeps only some archives – <c>borg prune</c> and, when
/// archives went, <c>borg compact</c>. Records the outcome as the job's last run and in its history. The same for the
/// window, the app running in the background and a scheduled run without window; while one of them works on a
/// repository, the others are turned away (<see cref="BackupSkipReason.RepositoryBusy"/>).
/// </summary>
/// <param name="describe">Turns a borg error into the text kept with the run (localized by the app).</param>
public sealed class BackupRunner(BorgClient borg, BackupJobStore jobs, DurationHistory durations, Func<BorgError, string> describe,
    TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public BackupRunHistory History { get; } = new(Path.Combine(jobs.DataDirectory, "history"));

    public string LockDirectory { get; } = Path.Combine(jobs.DataDirectory, "locks");

    public async Task<BackupRunResult> RunAsync(BackupRunRequest request, IBackupRunReport? report = null,
        CancellationToken cancellationToken = default)
    {
        var job = request.Job;
        var started = _time.GetUtcNow();

        using var repositoryLock = RepositoryLock.TryAcquire(LockDirectory, job.RepositoryId);
        if (repositoryLock is null)
        {
            // By hand: the run that holds the lock records the job's outcome, this one only goes into the history.
            // Scheduled: a backup that did not happen is a failure of the job.
            var notRun = NotRun(BackupSkipReason.RepositoryBusy, request.Scheduled ? BackupOutcome.Failed : BackupOutcome.Skipped);
            Record(job.Id, notRun, asLastRun: request.Scheduled);
            return new BackupRunResult(notRun, []);
        }

        var tracker = new Tracker(report);
        var options = new List<string> { "--compression", job.Compression };
        options.AddRange(BackupPatterns.Arguments(job.Exclusions, path => BorgClient.BorgPath(request.Borg, path)));
        var created = await borg.CreateAsync(request.Borg, request.Location, BackupNames.ArchiveName(job.Name), job.Sources, options,
            request.Passphrase, request.Ssh, tracker, cancellationToken);

        var summary = Summarize(created, tracker, started);
        if (summary.Outcome is BackupOutcome.Succeeded or BackupOutcome.Warnings && job.Retention is { HasRules: true } retention)
            summary = summary with { Cleanup = await PruneAndCompactAsync(request, retention, report, cancellationToken) };

        Record(job.Id, summary);
        // What compact freed is gone from the repository (the archives' totals shrink too, by an unknown amount).
        var repository = created.Value?.Repository is { } stats && summary.Cleanup?.FreedBytes is { } freed
            ? stats with { UniqueCompressedSize = Math.Max(0, stats.UniqueCompressedSize - freed) }
            : created.Value?.Repository;
        return new BackupRunResult(summary, created.Value?.Warnings ?? [], repository);
    }

    /// <summary>A run that did not start, as of now.</summary>
    public BackupRunSummary NotRun(BackupSkipReason reason, BackupOutcome outcome) => new()
    {
        Started = _time.GetUtcNow(),
        Duration = TimeSpan.Zero,
        Outcome = outcome,
        SkipReason = reason,
    };

    /// <summary>Keeps a run in the job's history and, by default, as its last run.</summary>
    public void Record(Guid jobId, BackupRunSummary run, bool asLastRun = true)
    {
        if (asLastRun)
            jobs.UpdateLastRun(jobId, run);
        History.Add(jobId, run);
    }

    /// <summary>Deletes the job's archives its rules don't keep and, if any went, frees their space.</summary>
    public async Task<CleanupSummary> PruneAndCompactAsync(BackupRunRequest request, RetentionPolicy retention, IBackupRunReport? report,
        CancellationToken cancellationToken)
    {
        var job = request.Job;
        var phase = new PhaseProgress(report, _time);

        var pruneKey = DurationHistory.Key(job.RepositoryId, "prune");
        phase.Start(BackupRunPhase.Pruning, durations.Get(pruneKey));
        var pruned = await borg.PruneAsync(request.Borg, request.Location, BackupNames.ArchiveGlob(job.Name), retention.Arguments(),
            dryRun: false, request.Passphrase, request.Ssh, phase, cancellationToken);
        if (!pruned.Succeeded)
            return Failed(pruned.Error!);
        durations.Record(pruneKey, phase.Elapsed);
        var count = pruned.Value!.Pruned.Count;
        if (count == 0)
            return new CleanupSummary();

        var compactKey = DurationHistory.Key(job.RepositoryId, "compact");
        phase.Start(BackupRunPhase.Compacting, durations.Get(compactKey));
        var compacted = await borg.CompactAsync(request.Borg, request.Location, request.Ssh, phase, cancellationToken);
        if (compacted.Succeeded)
            durations.Record(compactKey, phase.Elapsed);
        return compacted.Succeeded
            ? new CleanupSummary { PrunedArchives = count, FreedBytes = compacted.Value!.FreedBytes }
            : Failed(compacted.Error!) with { PrunedArchives = count };
    }

    private CleanupSummary Failed(BorgError error) => error.Kind == BorgErrorKind.Cancelled
        ? new CleanupSummary { Cancelled = true }
        : new CleanupSummary { Error = describe(error) };

    private BackupRunSummary Summarize(BorgResult<BorgCreateResult> result, Tracker tracker, DateTimeOffset started)
    {
        var duration = _time.GetUtcNow() - started;
        if (result.Value is { } created)
        {
            return new BackupRunSummary
            {
                Started = started,
                Duration = duration,
                Outcome = created.Warnings.Count > 0 ? BackupOutcome.Warnings : BackupOutcome.Succeeded,
                ArchiveName = created.ArchiveName,
                OriginalSize = created.OriginalSize,
                CompressedSize = created.CompressedSize,
                DeduplicatedSize = created.DeduplicatedSize,
                Files = created.Files,
                Message = created.Warnings.FirstOrDefault(),
            };
        }

        // Failed or stopped: what borg had read until then.
        var cancelled = result.Error!.Kind == BorgErrorKind.Cancelled;
        var last = tracker.Last;
        return new BackupRunSummary
        {
            Started = started,
            Duration = duration,
            Outcome = cancelled ? BackupOutcome.Cancelled : BackupOutcome.Failed,
            OriginalSize = last?.OriginalSize ?? 0,
            DeduplicatedSize = last?.DeduplicatedSize ?? 0,
            Files = last?.Files ?? 0,
            Message = cancelled ? null : describe(result.Error),
            OutOfSpace = result.Error.Kind == BorgErrorKind.NoSpace,
        };
    }

    /// <summary>Passes borg create's progress on and remembers the last report (for a run that fails).</summary>
    private sealed class Tracker(IBackupRunReport? report) : IProgress<BorgCreateProgress>
    {
        public BorgCreateProgress? Last { get; private set; }

        public void Report(BorgCreateProgress value)
        {
            Last = value;
            report?.Created(value);
        }
    }

    /// <summary>Times a clean-up phase (for the next estimate) and passes its progress on.</summary>
    private sealed class PhaseProgress(IBackupRunReport? report, TimeProvider time) : IProgress<double>
    {
        private DateTimeOffset _started;

        public TimeSpan Elapsed => time.GetUtcNow() - _started;

        public void Start(BackupRunPhase phase, TimeSpan? expected)
        {
            _started = time.GetUtcNow();
            report?.PhaseStarted(phase, expected);
        }

        public void Report(double value) => report?.PhaseProgress(value);
    }
}
