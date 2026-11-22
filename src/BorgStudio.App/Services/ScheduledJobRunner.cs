using BorgStudio.App.ViewModels;
using BorgStudio.Core.Backups;
using BorgStudio.Core.Borg;
using BorgStudio.Core.Repositories;
using BorgStudio.Core.Secrets;
using BorgStudio.Core.Security;
using BorgStudio.Core.Storage;
using BorgStudio.Plugins;

namespace BorgStudio.App.Services;

/// <summary>
/// Runs a backup job without window – for schedules. Nobody can answer questions, so everything the window would ask
/// for has to be there: borg, the repository's folder or server, the passphrase (from the vault, opened with the key
/// for scheduled runs, or the keychain) and a confirmed server. Whatever is missing ends the run with its reason,
/// recorded with the job like any other run.
/// </summary>
public sealed class ScheduledJobRunner(AppServices services, ScheduledAccess access)
{
    private readonly BackupRunner _runner = new(services.BorgClient, services.Jobs, services.Durations, BorgTexts.Describe,
        hooks: services.Hooks, describeHook: BorgTexts.HookProblem);

    public BackupRunner Runner => _runner;

    /// <exception cref="ArgumentException">There is no job <paramref name="jobId"/>.</exception>
    public async Task<BackupRunResult> RunAsync(Guid jobId, IBackupRunReport? report = null, CancellationToken cancellationToken = default)
    {
        var job = services.Jobs.Load().FirstOrDefault(candidate => candidate.Id == jobId)
            ?? throw new ArgumentException($"There is no backup job {jobId}.", nameof(jobId));

        try
        {
            var (result, storage) = await RunJobAsync(job, report, cancellationToken);
            await NotifyAsync(job, result.Summary, storage);
            return result;
        }
        finally
        {
            services.Vault.Lock();
        }
    }

    /// <summary>What the conditions of scheduled runs ask (battery, connection, target).</summary>
    private ISystemState System => services.SystemState ?? new SystemState();

    /// <summary>
    /// Why a scheduled run of <paramref name="job"/> may not start now (a condition of the job); <c>null</c> if it may – also
    /// when its repository is gone (the run then fails with that reason).
    /// </summary>
    public async Task<BackupSkipReason?> CheckConditionsAsync(BackupJob job, CancellationToken cancellationToken = default)
    {
        if (!job.Conditions.Any
            || services.RepositoryStore.Load().Repositories.FirstOrDefault(candidate => candidate.Id == job.RepositoryId) is not { } repository
            || await ConditionLocationAsync(repository, cancellationToken) is not { } location)
            return null;
        return await job.Conditions.CheckAsync(System, location, cancellationToken);
    }

    /// <summary>
    /// A scheduled run without window: while a condition of the job holds it back, it looks again every
    /// <see cref="RunConditions.RetryInterval"/> – until the job's next scheduled time, then it is recorded as skipped.
    /// </summary>
    /// <param name="wait">Waits between the looks (tests replace it).</param>
    public async Task<BackupRunResult> RunWhenAllowedAsync(Guid jobId, Func<TimeSpan, CancellationToken, Task>? wait = null,
        Func<DateTime>? clock = null, CancellationToken cancellationToken = default)
    {
        var job = services.Jobs.Load().FirstOrDefault(candidate => candidate.Id == jobId)
            ?? throw new ArgumentException($"There is no backup job {jobId}.", nameof(jobId));
        wait ??= Task.Delay;
        clock ??= () => DateTime.Now;
        var deadline = ScheduleCalendar.Next(job.Schedules, clock()) ?? clock().AddDays(1);
        while (await CheckConditionsAsync(job, cancellationToken) is { } reason)
        {
            if (clock() + RunConditions.RetryInterval >= deadline)
                return await SkipAsync(job, reason);
            ScheduledRun.Log($"{job.Name}: waiting – {BorgTexts.SkipReason(reason)}", services.Jobs.DataDirectory);
            await wait(RunConditions.RetryInterval, cancellationToken);
        }
        return await RunAsync(jobId, cancellationToken: cancellationToken);
    }

    /// <summary>A scheduled run a condition held back until its time was over: recorded as skipped and reported.</summary>
    public async Task<BackupRunResult> SkipAsync(BackupJob job, BackupSkipReason reason)
    {
        try
        {
            var result = NotRun(job, reason, BackupOutcome.Skipped);
            await NotifyAsync(job, result.Summary, null);
            return result;
        }
        finally
        {
            services.Vault.Lock();
        }
    }

    /// <summary>Opens the vault with the key for scheduled runs, if it is locked (for the notification channels' secrets).</summary>
    internal void OpenVaultIfLocked()
    {
        if (services.Vault.State == VaultState.Locked)
            OpenVault();
    }

    /// <summary>
    /// What followed the backup: the repository's storage, measured now (<c>null</c> if that did not work), and its check
    /// (<c>null</c> if none was due).
    /// </summary>
    private sealed record MeasuredStorage(RepositoryConfig Repository, StorageUsage? Usage, Core.Checks.CheckRecord? Check = null,
        IReadOnlyList<(BackupJob Job, Core.Checks.RestoreTestRecord Record)>? RestoreTests = null);

    /// <summary>Reports the run – and the storage, if it was measured – through the notification channels; their secrets may be in the vault.</summary>
    private async Task NotifyAsync(BackupJob job, BackupRunSummary summary, MeasuredStorage? storage)
    {
        try
        {
            if (services.Vault.State == VaultState.Locked)
                OpenVault();
            var notifications = new RunNotifications(services);
            await notifications.NotifyAsync(job, summary, services.Vault);
            if (storage is not null)
                await notifications.NotifyStorageAsync(storage.Repository, storage.Usage, services.Vault);
            if (storage?.Check is { } check)
                await notifications.NotifyCheckAsync(storage.Repository, check, services.Vault);
            foreach (var (tested, test) in storage?.RestoreTests ?? [])
                await notifications.NotifyRestoreTestAsync(tested, storage!.Repository.Name, test, services.Vault);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            AppLog.Write(RunNotifications.LogFile, $"{job.Name}: {exception}", services.Jobs.DataDirectory);
        }
    }

    private async Task<(BackupRunResult Result, MeasuredStorage? Storage)> RunJobAsync(BackupJob job, IBackupRunReport? report,
        CancellationToken cancellationToken)
    {
        var repository = services.RepositoryStore.Load().Repositories.FirstOrDefault(candidate => candidate.Id == job.RepositoryId);
        if (repository is null)
            return (NotRun(job, BackupSkipReason.RepositoryMissing), null);
        var (access, problem) = await PrepareAsync(repository, cancellationToken);
        if (access is null)
            return (NotRun(job, problem!.Value, problem == BackupSkipReason.PassphraseAsked ? BackupOutcome.Skipped : BackupOutcome.Failed), null);
        try
        {
            return await RunJobAsync(job, access.Lease?.Repository ?? repository, access, report, cancellationToken);
        }
        finally
        {
            await ReleaseAsync(access, repository);
        }
    }

    private async Task<(BackupRunResult Result, MeasuredStorage? Storage)> RunJobAsync(BackupJob job, RepositoryConfig repository,
        UnattendedAccess access, IBackupRunReport? report, CancellationToken cancellationToken)
    {
        var result = await _runner.RunAsync(new BackupRunRequest(access.Borg, access.Location, job, access.Passphrase, access.Ssh, Scheduled: true,
                Throttle: access.Throttle),
            report, cancellationToken);
        if (result.Summary.SkipReason == BackupSkipReason.RepositoryBusy)
            return (result, null);

        // How full the storage is now (for the overview and the "almost full" notification).
        var usage = await MeasureStorageAsync(services, repository, access.Borg, access.Location, access.Passphrase, access.Ssh, result.Repository);
        // Checked after the backup, if it is due (the repository is reachable now anyway).
        var checks = new RepositoryChecks(services);
        var check = result.Summary.Outcome is BackupOutcome.Succeeded or BackupOutcome.Warnings
            ? await checks.CheckAfterBackupAsync(repository, access, report, cancellationToken)
            : null;
        return (result, new MeasuredStorage(repository, usage, check, checks.LastRestoreTests));
    }

    /// <summary>
    /// What a run without window needs for <paramref name="repository"/>: borg, its location (a local folder must be
    /// there; a USB drive is looked for), the passphrase and the SSH login – or why one of them is missing. The access
    /// holds the repository's connection: give it back with <see cref="ReleaseAsync"/>.
    /// </summary>
    internal async Task<(UnattendedAccess? Access, BackupSkipReason? Problem)> PrepareAsync(RepositoryConfig repository,
        CancellationToken cancellationToken = default)
    {
        if (await services.BorgDetector.DetectAsync(cancellationToken) is not { Support: BorgSupport.Supported } borg)
            return (null, BackupSkipReason.BorgMissing);

        RepositoryLease? lease;
        try
        {
            lease = await services.Connections.ConnectAsync(repository, unattended: true, cancellationToken);
        }
        catch (RepositoryUnavailableException exception)
        {
            ScheduledRun.Log($"{repository.Name}: {exception.Message}", services.Jobs.DataDirectory);
            return (null, BackupSkipReason.TargetMissing);
        }
        if (lease is null)
            return (null, BackupSkipReason.RepositoryMissing);

        var (prepared, problem) = Prepare(lease.Repository, lease.Location, borg);
        if (prepared is null)
        {
            await lease.ReleaseAsync();
            return (null, problem);
        }
        return (prepared with { Lease = lease }, null);
    }

    private (UnattendedAccess? Access, BackupSkipReason? Problem) Prepare(RepositoryConfig repository, RepositoryLocation location,
        BorgInstallation borg)
    {
        // A local repository on a drive that is not there (USB disk): no need to ask borg.
        if (location.Ssh is null && !Directory.Exists(location.Url))
            return (null, BackupSkipReason.TargetMissing);

        var (passphrase, passphraseProblem) = GetPassphrase(repository);
        if (passphraseProblem is { } problem)
            return (null, problem);

        SshAccess? ssh = null;
        if (location.Ssh is { } endpoint)
        {
            if (repository.HasLegacySshKey || repository.SshKeyId is not { } keyId)
                return (null, BackupSkipReason.SshKeyMissing);
            if (!OpenVault())
                return (null, BackupSkipReason.ScheduledAccessMissing);
            if (services.Vault.GetKey(keyId) is not { } key)
                return (null, BackupSkipReason.SshKeyMissing);
            if (!services.SshTrust.IsKnown(endpoint.Host, endpoint.Port))
                return (null, BackupSkipReason.HostUnknown);
            ssh = new SshAccess(key.PrivateKey, services.SshKeys.KnownHostsFile);
        }
        return (new UnattendedAccess(borg, location, passphrase, ssh, repository.Limits.ThrottleAt(DateTime.Now, location.Ssh is not null)), null);
    }

    /// <summary>
    /// Measures and keeps the repository's storage, with the statistics borg reported with the backup; <c>null</c> if that
    /// did not work (never fails the run).
    /// </summary>
    internal static async Task<StorageUsage?> MeasureStorageAsync(AppServices services, RepositoryConfig repository, BorgInstallation borg,
        RepositoryLocation location, string? passphrase, SshAccess? ssh, RepositoryStats? stats = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var store = StorageUsageStore.ForDataDirectory(services.Jobs.DataDirectory);
            var usage = await new StorageProbe(services.BorgClient).MeasureAsync(repository, borg, location, passphrase, ssh, cancellationToken);
            if (usage is not null)
            {
                usage = stats is null ? usage : usage with { Repository = stats };
                store.Set(repository.Id, usage);
            }
            else if (stats is not null)
            {
                store.SetStats(repository.Id, stats);
            }
            return usage;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            AppLog.Write(RunNotifications.LogFile, $"{repository.Name}: measuring the storage failed: {exception.Message}", services.Jobs.DataDirectory);
            return null;
        }
    }

    private (string? Passphrase, BackupSkipReason? Problem) GetPassphrase(RepositoryConfig repository)
    {
        try
        {
            switch (repository.PassphraseMode)
            {
                case PassphraseMode.None:
                    return (null, null);
                case PassphraseMode.Ask:
                    return (null, BackupSkipReason.PassphraseAsked);
                case PassphraseMode.Stored:
                    return services.SecretStore.Get(repository.SecretKey) is { } stored ? (stored, null) : (null, BackupSkipReason.PassphraseMissing);
                default:
                    if (services.Vault.State == VaultState.NotSetUp)
                        return (null, BackupSkipReason.PassphraseMissing);
                    if (!OpenVault())
                        return (null, BackupSkipReason.ScheduledAccessMissing);
                    return services.Vault.GetPassphrase(repository.Id) is { } fromVault ? (fromVault, null) : (null, BackupSkipReason.PassphraseMissing);
            }
        }
        catch (Exception exception) when (exception is SecretStoreException or SecretBoxException)
        {
            return (null, BackupSkipReason.PassphraseMissing);
        }
    }

    private bool OpenVault()
    {
        try
        {
            return access.TryUnlock();
        }
        catch (SecretBoxException)
        {
            return false;
        }
    }

    /// <summary>
    /// Where the conditions look: the place the repository is found now (a USB drive under another letter), otherwise
    /// where it was last – the condition "target reachable" then says it is not.
    /// </summary>
    private async Task<RepositoryLocation?> ConditionLocationAsync(RepositoryConfig repository, CancellationToken cancellationToken)
    {
        try
        {
            await using var lease = await services.Connections.ConnectAsync(repository, cancellationToken: cancellationToken);
            return lease?.Location;
        }
        catch (RepositoryUnavailableException)
        {
            return Location(repository);
        }
    }

    /// <summary>
    /// Ends the run's use of the repository: a drive is ejected now, if the repository asks for that and nothing else
    /// uses it. A problem with that goes to the log of scheduled runs.
    /// </summary>
    internal async Task ReleaseAsync(UnattendedAccess access, RepositoryConfig repository)
    {
        if (access.Lease is { } lease && await lease.ReleaseAsync() is { } problem)
            ScheduledRun.Log($"{repository.Name}: {problem}", services.Jobs.DataDirectory);
    }

    private RepositoryLocation? Location(RepositoryConfig repository)
    {
        try
        {
            return services.Plugins.FindRepositoryProvider(repository.ProviderId)?.GetLocation(repository.ProviderValues);
        }
        catch (Exception exception) when (exception is KeyNotFoundException or ArgumentException or FormatException)
        {
            return null;
        }
    }

    private BackupRunResult NotRun(BackupJob job, BackupSkipReason reason, BackupOutcome outcome = BackupOutcome.Failed)
    {
        var summary = _runner.NotRun(reason, outcome);
        _runner.Record(job.Id, summary);
        return new BackupRunResult(summary, []);
    }
}

/// <summary>What a run without window works with: borg, the repository's location, its passphrase and SSH login.</summary>
/// <param name="Throttle">The repository's limits for runs starting now.</param>
/// <param name="Lease">The repository's connection (scheduled runs); released when the run is done.</param>
public sealed record UnattendedAccess(BorgInstallation Borg, RepositoryLocation Location, string? Passphrase, SshAccess? Ssh,
    BorgThrottle? Throttle = null, RepositoryLease? Lease = null);
