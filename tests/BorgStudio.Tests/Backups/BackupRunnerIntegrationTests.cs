using BorgStudio.Core.Backups;
using BorgStudio.Core.Borg;
using BorgStudio.Core.Progress;
using BorgStudio.Plugins;
using BorgStudio.Tests.TestSupport;

namespace BorgStudio.Tests.Backups;

/// <summary>The runner against a real borg (Linux CI, or borg in WSL); skipped where none is installed.</summary>
public sealed class BackupRunnerIntegrationTests : IDisposable
{
    private const string Passphrase = "correct horse battery staple";

    private readonly TemporaryDirectory _directory = new();

    public void Dispose() => _directory.Dispose();

    [BorgFact]
    public async Task Backs_up_cleans_up_and_records_the_runs()
    {
        var borg = BorgFactAttribute.Borg.Value!;
        var client = BorgClient.CreateDefault();
        var location = new RepositoryLocation(_directory.Combine("repo"));
        Assert.True((await client.InitAsync(borg, location, BorgEncryption.RepokeyBlake2, Passphrase)).Succeeded);

        var source = _directory.Combine("Documents");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "letter.txt"), "Dear borg");
        var job = new BackupJob
        {
            Id = Guid.NewGuid(),
            Name = "Documents",
            RepositoryId = Guid.NewGuid(),
            Sources = [source],
            Exclusions = new BackupExclusions { CachesAndTemp = false },
            Retention = new RetentionPolicy { Last = 1 },
        };
        var jobs = new BackupJobStore(_directory.Combine("data", "jobs.json"));
        jobs.Save([job]);
        var runner = new BackupRunner(client, jobs, new DurationHistory(_directory.Combine("data", "durations.json")), error => error.ToString());
        var request = new BackupRunRequest(borg, location, job, Passphrase, null);

        var first = await runner.RunAsync(request);
        Assert.Equal(BackupOutcome.Succeeded, first.Summary.Outcome);
        Assert.Equal(1, first.Summary.Files);

        // borg names archives to the second: the second backup needs another second.
        await Task.Delay(TimeSpan.FromSeconds(1.2));
        var second = await runner.RunAsync(request);
        Assert.Equal(BackupOutcome.Succeeded, second.Summary.Outcome);
        // Keep the last one: the first archive went.
        Assert.Equal(1, second.Summary.Cleanup!.PrunedArchives);
        var archives = await client.ListArchivesAsync(borg, location, Passphrase);
        Assert.Equal(second.Summary.ArchiveName, Assert.Single(archives.Value!).Name);

        Assert.Equal(second.Summary, jobs.Load().Single().LastRun);
        Assert.Equal([second.Summary, first.Summary], runner.History.Get(job.Id));

        using (RepositoryLock.TryAcquire(runner.LockDirectory, job.RepositoryId))
            Assert.Equal(BackupOutcome.Skipped, (await runner.RunAsync(request)).Summary.Outcome);
    }
}
