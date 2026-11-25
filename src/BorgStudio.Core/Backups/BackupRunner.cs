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

    /// <summary>Checking the repository after the backup (<c>borg check</c>, when due).</summary>
    Checking,

    /// <summary>Restoring the job's newest archive on trial (with the check).</summary>
    RestoreTesting,

    /// <summary>The job's command before the backup.</summary>
    HookBefore,

    /// <summary>The job's command after the backup.</summary>
    HookAfter,
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
/// <param name="Throttle">The repository's limits for this run (upload rate, low priority).</param>
public sealed record BackupRunRequest(BorgInstallation Borg, RepositoryLocation Location, BackupJob Job, string? Passphrase, SshAccess? Ssh,
    bool Scheduled = false, Repositories.BorgThrottle? Throttle = null);

/// <summary>A finished run: its summary (also saved with the job and in its history) and borg's warnings.</summary>
/// <param name="Repository">The repository's statistics after the run (borg reports them with the backup; clean-up subtracted).</param>
/// <param name="SourceRoot">Where the job's drive was attached (a job backing up a <see cref="SourceDrive"/>), e.g. to eject it.</param>
public sealed record BackupRunResult(BackupRunSummary Summary, IReadOnlyList<string> Warnings, Storage.RepositoryStats? Repository = null,
    string? SourceRoot = null);

/// <summary>
/// Runs a backup job: <c>borg create</c>, then – if the job keeps only some archives – <c>borg prune</c> and, when
/// archives went, <c>borg compact</c>. Records the outcome as the job's last run and in its history. The same for the
/// window, the app running in the background and a scheduled run without window; while one of them works on a
/// repository, the others are turned away (<see cref="BackupSkipReason.RepositoryBusy"/>).
/// </summary>
/// <param name="describe">Turns a borg error into the text kept with the run (localized by the app).</param>
/// <param name="hooks">Runs the jobs' commands (a real shell unless a test says otherwise).</param>
/// <param name="describeHook">Turns a failed command into the text kept with the run (localized by the app).</param>
/// <param name="sourceDrives">Finds the drives of jobs backing up a <see cref="SourceDrive"/>; without it they are never found.</param>
/// <param name="reports">Where backups, prunes and compacts are reported for providers that want to know (BorgStudio Server).</param>
public sealed class BackupRunner(BorgClient borg, BackupJobStore jobs, DurationHistory durations, Func<BorgError, string> describe,
    TimeProvider? time = null, HookRunner? hooks = null, Func<HookProblem, string>? describeHook = null, ISourceDriveLocator? sourceDrives = null,
    Repositories.RunReports? reports = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    private readonly HookRunner _hooks = hooks ?? new HookRunner();

    private string HookLog => Path.Combine(jobs.DataDirectory, "logs", "hooks.log");

    private string DescribeHook(HookProblem problem) => describeHook?.Invoke(problem)
        ?? $"Command {problem.Stage.ToString().ToLowerInvariant()} the backup failed"
           + (problem.ExitCode is { } code ? $" (exit code {code})" : problem.Error is { } error ? $" ({error})" : "")
           + (problem.Output.Count > 0 ? ": " + problem.Output[^1] : ".");

    public BackupRunHistory History { get; } = new(Path.Combine(jobs.DataDirectory, "history"));

    public string LockDirectory { get; } = Path.Combine(jobs.DataDirectory, "locks");

    public async Task<BackupRunResult> RunAsync(BackupRunRequest request, IBackupRunReport? report = null,
        CancellationToken cancellationToken = default)
    {
        var job = request.Job;
        var started = _time.GetUtcNow();

        // Kept with the job and in its history, marked as started by itself (schedule, webhook, attached drive) or by hand.
        BackupRunSummary Keep(BackupRunSummary run, bool asLastRun = true)
        {
            run = run with { Automatic = request.Scheduled };
            Record(job.Id, run, asLastRun);
            return run;
        }

        using var repositoryLock = RepositoryLock.TryAcquire(LockDirectory, job.RepositoryId);
        if (repositoryLock is null)
        {
            // By hand: the run that holds the lock records the job's outcome, this one only goes into the history.
            // Scheduled: a backup that did not happen is a failure of the job.
            var notRun = NotRun(BackupSkipReason.RepositoryBusy, request.Scheduled ? BackupOutcome.Failed : BackupOutcome.Skipped);
            notRun = Keep(notRun, asLastRun: request.Scheduled);
            return new BackupRunResult(notRun, []);
        }

        // A removable drive: wherever it is attached now – or nothing to back up.
        string? sourceRoot = null;
        if (job.Drive is { } drive)
        {
            sourceRoot = sourceDrives is null ? null : await sourceDrives.FindAsync(drive, cancellationToken);
            if (sourceRoot is null)
            {
                var missing = NotRun(BackupSkipReason.SourceMissing, BackupOutcome.Skipped);
                missing = Keep(missing);
                return new BackupRunResult(missing, []);
            }
        }

        // The command before the backup (e.g. a database dump).
        HookProblem? beforeProblem = null;
        if (job.Hooks.HasBefore)
        {
            report?.PhaseStarted(BackupRunPhase.HookBefore, null);
            var before = await _hooks.RunAsync(job.Hooks.EffectiveShell, job.Hooks.Before, Variables(request, null, null), HookLog,
                $"{job.Name}: before", cancellationToken);
            if (before.Cancelled)
            {
                var stopped = new BackupRunSummary { Started = started, Duration = _time.GetUtcNow() - started, Outcome = BackupOutcome.Cancelled };
                stopped = Keep(stopped);
                return new BackupRunResult(stopped, []);
            }
            if (!before.Succeeded)
            {
                beforeProblem = new HookProblem(HookStage.Before, before.ExitCode, before.Output, before.Error);
                if (!job.Hooks.ContinueOnBeforeFailure)
                {
                    var failed = new BackupRunSummary
                    {
                        Started = started,
                        Duration = _time.GetUtcNow() - started,
                        Outcome = BackupOutcome.Failed,
                        Message = DescribeHook(beforeProblem),
                        Hook = beforeProblem,
                    };
                    failed = await RunAfterAsync(request, failed, report);
                    failed = Keep(failed);
                    Report(job.RepositoryId, Repositories.RunReports.FromBackup(failed, null));
                    return new BackupRunResult(failed, []);
                }
            }
        }

        var tracker = new Tracker(report);
        var options = new List<string> { "--compression", job.Compression };
        options.AddRange(SourceArguments(job, request.Borg));
        var created = await borg.CreateAsync(request.Borg, request.Location, BackupNames.ArchiveName(job.Name),
            job.Drive?.BorgSources ?? job.Sources, options, request.Passphrase, request.Ssh, tracker, cancellationToken, request.Throttle,
            workingDirectory: sourceRoot);

        var summary = Summarize(created, tracker, started);
        if (summary.Outcome is BackupOutcome.Succeeded or BackupOutcome.Warnings && job.Retention is { HasRules: true } retention)
            summary = summary with { Cleanup = await PruneAndCompactAsync(request, retention, report, cancellationToken) };
        // The command before failed, the backup went ahead: a warning.
        if (beforeProblem is not null && summary.Outcome is BackupOutcome.Succeeded or BackupOutcome.Warnings)
            summary = summary with { Outcome = BackupOutcome.Warnings, Message = DescribeHook(beforeProblem), Hook = beforeProblem };
        summary = await RunAfterAsync(request, summary, report);

        summary = Keep(summary);
        // What compact freed is gone from the repository (the archives' totals shrink too, by an unknown amount).
        var repository = created.Value?.Repository is { } stats && summary.Cleanup?.FreedBytes is { } freed
            ? stats with { UniqueCompressedSize = Math.Max(0, stats.UniqueCompressedSize - freed) }
            : created.Value?.Repository;
        Report(job.RepositoryId, Repositories.RunReports.FromBackup(summary, repository));
        return new BackupRunResult(summary, created.Value?.Warnings ?? [], repository, sourceRoot);
    }

    /// <summary>
    /// The exclusions as borg options. A drive's exclusion folders are relative to its root, as the archive's paths are,
    /// and what the systems keep on a drive for themselves is always left out.
    /// </summary>
    public static List<string> SourceArguments(BackupJob job, BorgInstallation borgInstallation)
    {
        if (job.Drive is null)
            return BackupPatterns.Arguments(job.Exclusions, path => BorgClient.BorgPath(borgInstallation, path));
        var arguments = BackupPatterns.Arguments(job.Exclusions, path => SourceDrive.RelativePath(path) is { Length: > 0 } relative ? relative : null);
        foreach (var pattern in SourceDrive.SystemPatterns)
            arguments.AddRange(["--exclude", pattern]);
        return arguments;
    }

    /// <summary>
    /// The command after the backup, if the job has one and – unless it runs always – the backup worked. It runs to its end
    /// even if the run was stopped (it may have to start a service again). A failure makes a successful run a warning.
    /// </summary>
    private async Task<BackupRunSummary> RunAfterAsync(BackupRunRequest request, BackupRunSummary summary, IBackupRunReport? report)
    {
        var job = request.Job;
        var worked = summary.Outcome is BackupOutcome.Succeeded or BackupOutcome.Warnings;
        if (!job.Hooks.HasAfter || !(worked || job.Hooks.AfterAlways))
            return summary;

        report?.PhaseStarted(BackupRunPhase.HookAfter, null);
        var after = await _hooks.RunAsync(job.Hooks.EffectiveShell, job.Hooks.After, Variables(request, summary.Outcome, summary.ArchiveName),
            HookLog, $"{job.Name}: after ({summary.Outcome})", CancellationToken.None);
        if (after.Succeeded)
            return summary with { Duration = _time.GetUtcNow() - summary.Started };

        var problem = new HookProblem(HookStage.After, after.ExitCode, after.Output, after.Error);
        return summary with
        {
            Duration = _time.GetUtcNow() - summary.Started,
            Outcome = worked ? BackupOutcome.Warnings : summary.Outcome,
            // A failure before keeps its place; otherwise the command after is what to tell.
            Message = summary.Hook is null && (worked || summary.Message is null) ? DescribeHook(problem) : summary.Message,
            Hook = summary.Hook ?? problem,
        };
    }

    /// <summary>What the commands learn about the run (BORGSTUDIO_*).</summary>
    private static Dictionary<string, string> Variables(BackupRunRequest request, BackupOutcome? outcome, string? archive)
    {
        var variables = new Dictionary<string, string>
        {
            ["BORGSTUDIO_JOB"] = request.Job.Name,
            ["BORGSTUDIO_JOB_ID"] = request.Job.Id.ToString(),
            ["BORGSTUDIO_REPOSITORY"] = request.Location.Url,
            ["BORGSTUDIO_SCHEDULED"] = request.Scheduled ? "1" : "0",
        };
        if (outcome is { } done)
            variables["BORGSTUDIO_OUTCOME"] = done.ToString().ToLowerInvariant();
        if (archive is not null)
            variables["BORGSTUDIO_ARCHIVE"] = archive;
        return variables;
    }

    /// <summary>A run that did not start, as of now.</summary>
    public BackupRunSummary NotRun(BackupSkipReason reason, BackupOutcome outcome) => new()
    {
        Started = _time.GetUtcNow(),
        Duration = TimeSpan.Zero,
        Outcome = outcome,
        SkipReason = reason,
    };

    private void Report(Guid repositoryId, RunReport? run)
    {
        if (run is not null)
            reports?.Add(repositoryId, run);
    }

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
        var pruneStarted = _time.GetUtcNow();
        var pruned = await borg.PruneAsync(request.Borg, request.Location, BackupNames.ArchiveGlob(job.Name), retention.Arguments(),
            dryRun: false, request.Passphrase, request.Ssh, phase, cancellationToken);
        Report(job.RepositoryId, CleanupReport(RunReportKind.Prune, pruneStarted, pruned.Error));
        if (!pruned.Succeeded)
            return Failed(pruned.Error!);
        durations.Record(pruneKey, phase.Elapsed);
        var count = pruned.Value!.Pruned.Count;
        if (count == 0)
            return new CleanupSummary();

        var compactKey = DurationHistory.Key(job.RepositoryId, "compact");
        phase.Start(BackupRunPhase.Compacting, durations.Get(compactKey));
        var compactStarted = _time.GetUtcNow();
        var compacted = await borg.CompactAsync(request.Borg, request.Location, request.Ssh, phase, cancellationToken, request.Throttle);
        Report(job.RepositoryId, CleanupReport(RunReportKind.Compact, compactStarted, compacted.Error));
        if (compacted.Succeeded)
            durations.Record(compactKey, phase.Elapsed);
        return compacted.Succeeded
            ? new CleanupSummary { PrunedArchives = count, FreedBytes = compacted.Value!.FreedBytes }
            : Failed(compacted.Error!) with { PrunedArchives = count };
    }

    private RunReport CleanupReport(RunReportKind kind, DateTimeOffset started, BorgError? error) =>
        Repositories.RunReports.Cleanup(kind, started, _time.GetUtcNow() - started, error?.Kind == BorgErrorKind.Cancelled,
            error is null || error.Kind == BorgErrorKind.Cancelled ? null : describe(error));

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
            RepositoryLocked = result.Error.Kind == BorgErrorKind.Locked,
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
