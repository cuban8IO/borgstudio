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
                record = await RunAsync(repository, unattended, CheckDepth.Normal, automatic: true, report: null, LockWait, cancellationToken);
            }

            try
            {
                runner.OpenVaultIfLocked();
                await new RunNotifications(services).NotifyCheckAsync(repository, record, services.Vault, cancellationToken);
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
                cancellationToken);
            var duration = DateTimeOffset.Now - started;
            if (result.Succeeded)
                services.Durations.Record(key, duration);
            record = ToRecord(depth, started, duration, result);
        }
        record = record with { Automatic = automatic };
        Store.Add(repository.Id, record);
        return record;
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
