using BorgStudio.App.ViewModels;
using BorgStudio.Core.Backups;
using BorgStudio.Core.Borg;
using BorgStudio.Core.Repositories;
using BorgStudio.Core.Secrets;
using BorgStudio.Core.Security;
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
    private readonly BackupRunner _runner = new(services.BorgClient, services.Jobs, services.Durations, BorgTexts.Describe);

    public BackupRunner Runner => _runner;

    /// <exception cref="ArgumentException">There is no job <paramref name="jobId"/>.</exception>
    public async Task<BackupRunResult> RunAsync(Guid jobId, IBackupRunReport? report = null, CancellationToken cancellationToken = default)
    {
        var job = services.Jobs.Load().FirstOrDefault(candidate => candidate.Id == jobId)
            ?? throw new ArgumentException($"There is no backup job {jobId}.", nameof(jobId));

        var repository = services.RepositoryStore.Load().Repositories.FirstOrDefault(candidate => candidate.Id == job.RepositoryId);
        if (repository is null || Location(repository) is not { } location)
            return NotRun(job, BackupSkipReason.RepositoryMissing);

        if (await services.BorgDetector.DetectAsync(cancellationToken) is not { Support: BorgSupport.Supported } borg)
            return NotRun(job, BackupSkipReason.BorgMissing);

        // A local repository on a drive that is not there (USB disk): no need to ask borg.
        if (location.Ssh is null && !Directory.Exists(location.Url))
            return NotRun(job, BackupSkipReason.TargetMissing);

        var (passphrase, passphraseProblem) = GetPassphrase(repository);
        if (passphraseProblem is { } problem)
            return NotRun(job, problem, problem == BackupSkipReason.PassphraseAsked ? BackupOutcome.Skipped : BackupOutcome.Failed);

        SshAccess? ssh = null;
        if (location.Ssh is { } endpoint)
        {
            if (repository.HasLegacySshKey || repository.SshKeyId is not { } keyId)
                return NotRun(job, BackupSkipReason.SshKeyMissing);
            if (!OpenVault())
                return NotRun(job, BackupSkipReason.ScheduledAccessMissing);
            if (services.Vault.GetKey(keyId) is not { } key)
                return NotRun(job, BackupSkipReason.SshKeyMissing);
            if (!services.SshTrust.IsKnown(endpoint.Host, endpoint.Port))
                return NotRun(job, BackupSkipReason.HostUnknown);
            ssh = new SshAccess(key.PrivateKey, services.SshKeys.KnownHostsFile);
        }

        try
        {
            return await _runner.RunAsync(new BackupRunRequest(borg, location, job, passphrase, ssh, Scheduled: true),
                report, cancellationToken);
        }
        finally
        {
            services.Vault.Lock();
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
