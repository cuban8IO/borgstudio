using BorgStudio.App.Services;
using BorgStudio.App.ViewModels;
using BorgStudio.Core.Backups;
using BorgStudio.Core.Repositories;
using BorgStudio.Core.Security;
using BorgStudio.Core.Ssh;
using BorgStudio.Tests.TestSupport;

namespace BorgStudio.Tests.ViewModels;

/// <summary>A backup job run without window (as a schedule starts it): nothing may be asked, everything is recorded.</summary>
public sealed class ScheduledJobRunnerTests : IDisposable
{
    private const string Pin = "123456";

    private readonly TestServices _test = new();

    public void Dispose() => _test.Dispose();

    private string RepositoryFolder => _test.Directory.Combine("repo");

    private RepositoryConfig Local(PassphraseMode mode = PassphraseMode.None) => new()
    {
        Id = Guid.NewGuid(),
        Name = "Disk",
        ProviderId = "local",
        ProviderValues = new Dictionary<string, string> { ["path"] = RepositoryFolder },
        PassphraseMode = mode,
        EncryptionMode = mode == PassphraseMode.None ? "none" : "repokey-blake2",
    };

    private BackupJob JobFor(RepositoryConfig repository)
    {
        var source = _test.Directory.Combine("Documents");
        Directory.CreateDirectory(source);
        var job = new BackupJob
        {
            Id = Guid.NewGuid(),
            Name = "Documents",
            RepositoryId = repository.Id,
            Sources = [source],
            Exclusions = new BackupExclusions { CachesAndTemp = false },
        };
        _test.Store.Save([repository]);
        _test.Jobs.Save([job]);
        return job;
    }

    private ScheduledJobRunner NewRunner(out ScheduledAccess access)
    {
        access = MainViewModel.CreateScheduledAccess(_test.Services);
        return new ScheduledJobRunner(_test.Services, access);
    }

    private ScheduledJobRunner NewRunner() => NewRunner(out _);

    private BackupRunSummary? LastRun => _test.Jobs.Load().Single().LastRun;

    [Fact]
    public async Task Backs_up_and_records_the_run()
    {
        Directory.CreateDirectory(RepositoryFolder);
        var job = JobFor(Local());
        var runner = NewRunner();

        var result = await runner.RunAsync(job.Id);

        Assert.Equal(BackupOutcome.Succeeded, result.Summary.Outcome);
        Assert.Equal(result.Summary, LastRun);
        Assert.Equal([result.Summary], runner.Runner.History.Get(job.Id));
        Assert.Contains("create", _test.Borg.Commands);
    }

    [Fact]
    public async Task A_missing_disk_is_a_failed_run()
    {
        var job = JobFor(Local());

        var result = await NewRunner().RunAsync(job.Id);

        Assert.Equal(BackupOutcome.Failed, result.Summary.Outcome);
        Assert.Equal(BackupSkipReason.TargetMissing, result.Summary.SkipReason);
        Assert.Equal(result.Summary, LastRun);
        Assert.DoesNotContain("create", _test.Borg.Commands);
    }

    [Fact]
    public async Task A_busy_repository_is_a_failed_run()
    {
        Directory.CreateDirectory(RepositoryFolder);
        var repository = Local();
        var job = JobFor(repository);
        var runner = NewRunner();

        using (RepositoryLock.TryAcquire(runner.Runner.LockDirectory, repository.Id))
        {
            var result = await runner.RunAsync(job.Id);
            Assert.Equal(BackupOutcome.Failed, result.Summary.Outcome);
            Assert.Equal(BackupSkipReason.RepositoryBusy, result.Summary.SkipReason);
        }

        Assert.Equal(BackupSkipReason.RepositoryBusy, LastRun!.SkipReason);
    }

    [Fact]
    public async Task A_passphrase_asked_every_time_skips_the_run()
    {
        Directory.CreateDirectory(RepositoryFolder);
        var job = JobFor(Local(PassphraseMode.Ask));

        var result = await NewRunner().RunAsync(job.Id);

        Assert.Equal(BackupOutcome.Skipped, result.Summary.Outcome);
        Assert.Equal(BackupSkipReason.PassphraseAsked, result.Summary.SkipReason);
        Assert.Empty(_test.Dialogs.Shown);
    }

    [Fact]
    public async Task A_passphrase_from_the_keychain_is_used()
    {
        Directory.CreateDirectory(RepositoryFolder);
        var repository = Local(PassphraseMode.Stored);
        var job = JobFor(repository);

        Assert.Equal(BackupSkipReason.PassphraseMissing, (await NewRunner().RunAsync(job.Id)).Summary.SkipReason);

        _test.Secrets.Secrets[repository.SecretKey] = "secret";
        var result = await NewRunner().RunAsync(job.Id);
        Assert.Equal(BackupOutcome.Succeeded, result.Summary.Outcome);
        Assert.Equal("secret", _test.Borg.Calls.Single(call => call.Arguments[0] == "create").Passphrase);
    }

    [Fact]
    public async Task The_vault_opens_only_with_the_key_for_scheduled_runs_and_is_locked_again()
    {
        Directory.CreateDirectory(RepositoryFolder);
        var repository = Local(PassphraseMode.Vault);
        var job = JobFor(repository);
        _test.Vault.SetUp(Pin);
        _test.Vault.SetPassphrase(repository.Id, "from-the-vault");
        _test.Vault.Lock();

        var runner = NewRunner(out var access);
        var denied = await runner.RunAsync(job.Id);
        Assert.Equal(BackupOutcome.Failed, denied.Summary.Outcome);
        Assert.Equal(BackupSkipReason.ScheduledAccessMissing, denied.Summary.SkipReason);

        Assert.True(_test.Vault.Unlock(Pin));
        access.Enable();
        _test.Vault.Lock();

        var result = await runner.RunAsync(job.Id);
        Assert.Equal(BackupOutcome.Succeeded, result.Summary.Outcome);
        Assert.Equal("from-the-vault", _test.Borg.Calls.Single(call => call.Arguments[0] == "create").Passphrase);
        Assert.Equal(VaultState.Locked, _test.Vault.State);
    }

    [Fact]
    public async Task Ssh_repositories_need_their_key_and_a_confirmed_server()
    {
        var repository = new RepositoryConfig
        {
            Id = Guid.NewGuid(),
            Name = "NAS",
            ProviderId = "ssh",
            ProviderValues = new Dictionary<string, string> { ["host"] = "nas.local", ["port"] = "22", ["user"] = "backup", ["path"] = "/backups" },
            PassphraseMode = PassphraseMode.None,
            EncryptionMode = "none",
        };
        var job = JobFor(repository);
        Assert.Equal(BackupSkipReason.SshKeyMissing, (await NewRunner().RunAsync(job.Id)).Summary.SkipReason);

        _test.Vault.SetUp(Pin);
        var key = new SshKeyEntry { Id = Guid.NewGuid(), Name = "nas", PrivateKey = "private", PublicKey = "ssh-ed25519 AAAA" };
        _test.Vault.SetKey(key);
        _test.Store.Save([repository with { SshKeyId = key.Id }]);
        MainViewModel.CreateScheduledAccess(_test.Services).Enable();
        _test.Vault.Lock();

        var result = await NewRunner().RunAsync(job.Id);
        Assert.Equal(BackupSkipReason.HostUnknown, result.Summary.SkipReason);
        Assert.DoesNotContain("create", _test.Borg.Commands);
    }

    [Fact]
    public async Task An_unknown_job_is_refused()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => NewRunner().RunAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task The_window_shows_why_a_scheduled_run_did_not_happen()
    {
        var job = JobFor(Local());
        await NewRunner().RunAsync(job.Id);

        var main = new MainViewModel(_test.Services);
        await main.CheckBorgCommand.ExecuteAsync(null);
        Assert.True(main.SelectedJob!.LastRunFailed);
        Assert.Equal(BorgTexts.SkipReason(BackupSkipReason.TargetMissing), App.Resources.Strings.SkipTargetMissing);
    }
}
