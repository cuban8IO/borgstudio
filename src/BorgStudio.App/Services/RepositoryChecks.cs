using BorgStudio.App.Resources;
using BorgStudio.App.ViewModels;
using BorgStudio.Core.Backups;
using BorgStudio.Core.Borg;
using BorgStudio.Core.Checks;
using BorgStudio.Core.Progress;
using BorgStudio.Core.Repositories;
using BorgStudio.Core.Security;

namespace BorgStudio.App.Services;

/// <summary>
/// Checks a repository (<c>borg check</c>) and keeps the record – for the window, after backups and for scheduled checks.
/// Checks started by themselves are always normal ones; they wait for a backup on the repository to finish.
/// </summary>
public sealed class RepositoryChecks(AppServices services)
{
    /// <summary>How long a scheduled check waits for a backup on the repository before it gives up.</summary>
    public static TimeSpan LockWait { get; set; } = TimeSpan.FromHours(6);

    /// <summary>How often a waiting check looks whether the repository is free.</summary>
    public static TimeSpan LockPoll { get; set; } = TimeSpan.FromSeconds(30);

    public RepositoryCheckStore Store => RepositoryCheckStore.ForDataDirectory(services.Jobs.DataDirectory);

    public RestoreTestStore RestoreTests => RestoreTestStore.ForDataDirectory(services.Jobs.DataDirectory);

    /// <summary>The restore tests the last automatic check ran (for the jobs on the repository that have one).</summary>
    public IReadOnlyList<(BackupJob Job, RestoreTestRecord Record)> LastRestoreTests { get; private set; } = [];

    /// <summary>Where samples are restored to; tests point it elsewhere.</summary>
    public static string TemporaryRoot { get; set; } = Path.GetTempPath();

    /// <summary>After a backup: a normal check if the repository's policy says one is due; <c>null</c> if not.</summary>
    public async Task<CheckRecord?> CheckAfterBackupAsync(RepositoryConfig repository, UnattendedAccess access, IBackupRunReport? report,
        CancellationToken cancellationToken = default)
    {
        if (!repository.Check.IsDueAfterBackup(Store.LastVerdict(repository.Id), DateTimeOffset.Now))
            return null;
        return await RunAsync(repository, access, CheckDepth.Normal, automatic: true, report, TimeSpan.Zero, cancellationToken);
    }

    /// <summary>
    /// A scheduled check without window: prepares what it needs (as a scheduled backup does), checks and reports. What is
    /// missing ends it as a check that could not run.
    /// </summary>
    public async Task<CheckRecord> RunScheduledAsync(RepositoryConfig repository, ScheduledAccess access, CancellationToken cancellationToken = default)
    {
        var runner = new ScheduledJobRunner(services, access);
        using var marker = Core.Progress.RunMarkers.ForDataDirectory(services.Jobs.DataDirectory)
            .Mark(Core.Progress.RunMarkerKind.Check, repository.Name, repository.Id);
        try
        {
            var (unattended, problem) = await runner.PrepareAsync(repository, cancellationToken);
            CheckRecord record;
            if (unattended is null)
            {
                record = new CheckRecord(DateTimeOffset.Now, TimeSpan.Zero, CheckDepth.Normal, CheckOutcome.Failed)
                {
                    Automatic = true,
                    Error = BorgTexts.SkipReason(problem!.Value),
                };
                Store.Add(repository.Id, record);
            }
            else
            {
                try
                {
                    record = await RunAsync(unattended.Lease?.Repository ?? repository, unattended, CheckDepth.Normal, automatic: true, report: null,
                        LockWait, cancellationToken);
                }
                finally
                {
                    await runner.ReleaseAsync(unattended, repository);
                }
            }

            try
            {
                runner.OpenVaultIfLocked();
                var notifications = new RunNotifications(services);
                await notifications.NotifyCheckAsync(repository, record, services.Vault, cancellationToken);
                foreach (var (job, test) in LastRestoreTests)
                    await notifications.NotifyRestoreTestAsync(job, repository.Name, test, services.Vault, cancellationToken);
                await services.SendReportsAsync();
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                AppLog.Write(RunNotifications.LogFile, $"{repository.Name}: {exception}", services.Jobs.DataDirectory);
            }
            return record;
        }
        finally
        {
            services.Vault.Lock();
        }
    }

    /// <summary>
    /// Checks the repository and keeps the record. Waits up to <paramref name="lockWait"/> for another run on the
    /// repository; still busy then, the check counts as not run. Progress goes to <paramref name="report"/> (the run's card).
    /// </summary>
    public async Task<CheckRecord> RunAsync(RepositoryConfig repository, UnattendedAccess access, CheckDepth depth, bool automatic,
        IBackupRunReport? report, TimeSpan lockWait, CancellationToken cancellationToken = default)
    {
        var started = DateTimeOffset.Now;
        using var repositoryLock = await AcquireLockAsync(repository.Id, lockWait, cancellationToken);
        CheckRecord record;
        if (repositoryLock is null)
        {
            record = new CheckRecord(started, TimeSpan.Zero, depth, CheckOutcome.Failed) { Error = Strings.CheckBusy };
        }
        else
        {
            var key = DurationKey(repository.Id, depth);
            report?.PhaseStarted(BackupRunPhase.Checking, services.Durations.Get(key));
            var progress = report is null ? null : new Progress(update => report.PhaseProgress(Overall(depth, update)));
            var result = await services.BorgClient.CheckAsync(access.Borg, access.Location, depth, access.Passphrase, access.Ssh, progress,
                cancellationToken, access.Throttle);
            var duration = DateTimeOffset.Now - started;
            if (result.Succeeded)
                services.Durations.Record(key, duration);
            record = ToRecord(depth, started, duration, result);
            // With an automatic check, the jobs' restore tests – while the repository is still locked for this.
            services.Report(repository.Id, RunReports.FromCheck(record));
            if (automatic && record.IsVerdict)
            {
                Store.Add(repository.Id, record with { Automatic = true });
                LastRestoreTests = await RunRestoreTestsAsync(repository, access, report, cancellationToken);
                return record with { Automatic = true };
            }
        }
        record = record with { Automatic = automatic };
        Store.Add(repository.Id, record);
        return record;
    }

    /// <summary>
    /// The restore tests of the jobs on <paramref name="repository"/> that have one – if restore tests are switched on at
    /// all. Each restores the job's newest archive on trial; the outcome is kept with the job.
    /// </summary>
    private async Task<IReadOnlyList<(BackupJob Job, RestoreTestRecord Record)>> RunRestoreTestsAsync(RepositoryConfig repository,
        UnattendedAccess access, IBackupRunReport? report, CancellationToken cancellationToken)
    {
        var settings = services.Settings.Load().RestoreTest;
        if (!settings.Enabled)
            return [];
        var results = new List<(BackupJob, RestoreTestRecord)>();
        foreach (var job in services.Jobs.Load().Where(job => job.RepositoryId == repository.Id && job.RestoreTest != RestoreTestMode.Off))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var record = await TestRestoreAsync(job, access, job.RestoreTest, settings, automatic: true, report, cancellationToken);
            results.Add((job, record));
        }
        return results;
    }

    /// <summary>
    /// Restores the job's newest archive on trial and keeps the outcome with the job. The caller holds the repository
    /// (no lock is taken here). Progress goes to <paramref name="report"/>.
    /// </summary>
    public async Task<RestoreTestRecord> TestRestoreAsync(BackupJob job, UnattendedAccess access, RestoreTestMode mode,
        RestoreTestSettings settings, bool automatic, IBackupRunReport? report, CancellationToken cancellationToken = default)
    {
        RestoreTestRecord record;
        if (NewestArchive(job) is not { } archive)
        {
            record = new RestoreTestRecord(DateTimeOffset.Now, TimeSpan.Zero, mode, RestoreTestOutcome.NotRun) { Error = Strings.RestoreTestNoArchive };
        }
        else
        {
            report?.PhaseStarted(BackupRunPhase.RestoreTesting, services.Durations.Get(DurationHistory.Key(job.Id, "restore-test")));
            var progress = report is null ? null : new FractionProgress(report.PhaseProgress);
            record = await new RestoreTester(services.BorgClient, BorgTexts.Describe).RunAsync(access.Borg, access.Location, archive,
                access.Passphrase, access.Ssh, mode, job.RestoreTestFiles, settings, TemporaryRoot, progress, cancellationToken: cancellationToken,
                throttle: access.Throttle);
            if (record.Outcome is RestoreTestOutcome.Ok or RestoreTestOutcome.Failed)
                services.Durations.Record(DurationHistory.Key(job.Id, "restore-test"), record.Duration);
        }
        record = record with { Automatic = automatic };
        RestoreTests.Add(job.Id, record);
        return record;
    }

    /// <summary>The archive of the job's newest backup that worked; <c>null</c> if it has none.</summary>
    private string? NewestArchive(BackupJob job) =>
        new BackupRunHistory(Path.Combine(services.Jobs.DataDirectory, "history")).Get(job.Id).Append(job.LastRun).OfType<BackupRunSummary>()
            .Where(run => run.Outcome is BackupOutcome.Succeeded or BackupOutcome.Warnings && run.ArchiveName is not null)
            .MaxBy(run => run.Started)?.ArchiveName;

    private sealed class FractionProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }

    private async Task<RepositoryLock?> AcquireLockAsync(Guid repositoryId, TimeSpan wait, CancellationToken cancellationToken)
    {
        var directory = Path.Combine(services.Jobs.DataDirectory, "locks");
        var until = DateTime.UtcNow + wait;
        while (true)
        {
            if (RepositoryLock.TryAcquire(directory, repositoryId) is { } acquired)
                return acquired;
            if (DateTime.UtcNow >= until)
                return null;
            await Task.Delay(LockPoll, cancellationToken);
        }
    }

    /// <summary>Under this name the duration of a check is remembered (shown as the expected time next time).</summary>
    public static string DurationKey(Guid repositoryId, CheckDepth depth) =>
        DurationHistory.Key(repositoryId, "check-" + depth.ToString().ToLowerInvariant());

    /// <summary>borg's outcome as a record (damage is a result, a borg error a check that could not run).</summary>
    public static CheckRecord ToRecord(CheckDepth depth, DateTimeOffset started, TimeSpan duration, BorgResult<BorgCheckResult> result) => result switch
    {
        { Value: { } checkResult } => new CheckRecord(started, duration, depth,
            depth == CheckDepth.Repair ? CheckOutcome.Repaired : checkResult.ProblemsFound ? CheckOutcome.ProblemsFound : CheckOutcome.Ok)
        {
            Messages = checkResult.Messages,
        },
        { Error.Kind: BorgErrorKind.Cancelled } => new CheckRecord(started, duration, depth, CheckOutcome.Cancelled),
        _ => new CheckRecord(started, duration, depth, CheckOutcome.Failed) { Error = BorgTexts.Describe(result.Error!) },
    };

    /// <summary>The whole check as one bar: each phase gets the share it usually takes.</summary>
    public static double Overall(CheckDepth depth, BorgCheckProgress update)
    {
        var (start, share) = (depth, update.Phase) switch
        {
            (CheckDepth.Full, CheckPhase.Segments) => (0.0, 0.2),
            (CheckDepth.Full, CheckPhase.Data) => (0.2, 0.7),
            (CheckDepth.Full, _) => (0.9, 0.1),
            (_, CheckPhase.Segments) => (0.0, 0.6),
            _ => (0.6, 0.4),
        };
        return start + share * update.Fraction;
    }

    /// <summary>Reports right away, on borg's thread (the run's card moves it to the window's thread itself).</summary>
    private sealed class Progress(Action<BorgCheckProgress> report) : IProgress<BorgCheckProgress>
    {
        public void Report(BorgCheckProgress value) => report(value);
    }
}
