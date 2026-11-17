using BorgStudio.Core.Backups;
using BorgStudio.Plugins;
using BorgStudio.Tests.TestSupport;

namespace BorgStudio.Tests.Backups;

/// <summary>The backup run shared by the window and (later) scheduled runs: outcome, history, lock, saving.</summary>
public sealed class BackupRunnerTests : IDisposable
{
    private readonly TestServices _test = new();
    private readonly Guid _repositoryId = Guid.NewGuid();

    public void Dispose() => _test.Dispose();

    private BackupJob Job(string name = "Documents", RetentionPolicy? retention = null) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        RepositoryId = _repositoryId,
        Sources = [_test.Directory.Combine("src")],
        Retention = retention,
    };

    private BackupRunner NewRunner() => new(_test.Services.BorgClient, _test.Jobs, _test.Durations, error => $"error: {error.Kind}");

    private BackupRunRequest Request(BackupJob job) =>
        new(FakeBorg.Installation, new RepositoryLocation(_test.Directory.Combine("repo")), job, "secret", null);

    [Fact]
    public async Task A_run_is_saved_with_the_job_and_in_its_history()
    {
        var job = Job();
        var other = Job("Photos");
        _test.Jobs.Save([job, other]);
        var runner = NewRunner();

        var result = await runner.RunAsync(Request(job));

        Assert.Equal(BackupOutcome.Succeeded, result.Summary.Outcome);
        Assert.Equal("Documents-host-2026-11-06T10:00:00", result.Summary.ArchiveName);
        Assert.Equal(3000, result.Summary.OriginalSize);
        var saved = _test.Jobs.Load();
        Assert.Equal(result.Summary, saved.Single(j => j.Id == job.Id).LastRun);
        Assert.Null(saved.Single(j => j.Id == other.Id).LastRun);
        Assert.Equal([result.Summary], runner.History.Get(job.Id));

        var second = await runner.RunAsync(Request(job));
        Assert.Equal([second.Summary, result.Summary], runner.History.Get(job.Id));
    }

    [Fact]
    public async Task Progress_and_the_clean_up_are_reported()
    {
        _test.Borg.Archives.Add(("Documents-host-2026-11-01T10:00:00", new DateTime(2026, 11, 1)));
        var job = Job(retention: new RetentionPolicy { Last = 1 });
        _test.Jobs.Save([job]);
        var report = new Report();

        var result = await NewRunner().RunAsync(Request(job), report);

        Assert.Equal(3, report.Created);
        Assert.Equal([BackupRunPhase.Pruning, BackupRunPhase.Compacting], report.Phases);
        Assert.Equal(1, result.Summary.Cleanup!.PrunedArchives);
        Assert.Equal(["create", "prune", "compact"], _test.Borg.Commands);
        Assert.NotNull(_test.Durations.Get(Core.Progress.DurationHistory.Key(_repositoryId, "prune")));
    }

    [Fact]
    public async Task A_stopped_run_keeps_what_borg_had_read()
    {
        var job = Job();
        _test.Jobs.Save([job]);
        using var cancellation = new CancellationTokenSource();
        _test.Borg.AfterCreateProgress = step =>
        {
            if (step == 2)
                cancellation.Cancel();
        };

        var result = await NewRunner().RunAsync(Request(job), cancellationToken: cancellation.Token);

        Assert.Equal(BackupOutcome.Cancelled, result.Summary.Outcome);
        Assert.Equal(2000, result.Summary.OriginalSize);
        Assert.Equal(20, result.Summary.Files);
        Assert.Equal(BackupOutcome.Cancelled, _test.Jobs.Load().Single().LastRun!.Outcome);
    }

    [Fact]
    public async Task A_busy_repository_turns_the_run_away()
    {
        var job = Job();
        var earlier = new BackupRunSummary { Started = DateTimeOffset.UnixEpoch, Duration = TimeSpan.FromSeconds(1), Outcome = BackupOutcome.Succeeded };
        _test.Jobs.Save([job with { LastRun = earlier }]);
        var runner = NewRunner();

        using (RepositoryLock.TryAcquire(runner.LockDirectory, _repositoryId))
        {
            var result = await runner.RunAsync(Request(job));

            Assert.Equal(BackupOutcome.Skipped, result.Summary.Outcome);
            Assert.Equal(BackupSkipReason.RepositoryBusy, result.Summary.SkipReason);
            Assert.Empty(_test.Borg.Calls);
            // The run that holds the lock records the job's outcome; the history knows about the skipped one.
            Assert.Equal(earlier, _test.Jobs.Load().Single().LastRun);
            Assert.Equal([result.Summary], runner.History.Get(job.Id));
        }

        // Released: the next run works (and releases it again).
        Assert.Equal(BackupOutcome.Succeeded, (await runner.RunAsync(Request(job))).Summary.Outcome);
        using var free = RepositoryLock.TryAcquire(runner.LockDirectory, _repositoryId);
        Assert.NotNull(free);
    }

    [Fact]
    public void Each_repository_has_its_own_lock()
    {
        var directory = _test.Directory.Combine("locks");
        using var first = RepositoryLock.TryAcquire(directory, _repositoryId);
        Assert.NotNull(first);
        Assert.Null(RepositoryLock.TryAcquire(directory, _repositoryId));
        using var other = RepositoryLock.TryAcquire(directory, Guid.NewGuid());
        Assert.NotNull(other);
    }

    [Fact]
    public void Saving_keeps_a_newer_run_recorded_by_another_process()
    {
        var job = Job();
        var old = new BackupRunSummary { Started = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero), Duration = TimeSpan.Zero, Outcome = BackupOutcome.Succeeded };
        var newer = old with { Started = old.Started.AddDays(1), Outcome = BackupOutcome.Warnings };
        _test.Jobs.Save([job with { LastRun = old }]);

        // A scheduled run records its outcome …
        _test.Jobs.UpdateLastRun(job.Id, newer);
        // … while the app still has the old one in memory and saves an edit of the job.
        _test.Jobs.Save([job with { LastRun = old, Name = "Renamed" }]);

        var saved = _test.Jobs.Load().Single();
        Assert.Equal("Renamed", saved.Name);
        Assert.Equal(newer, saved.LastRun);
    }

    [Fact]
    public void The_history_keeps_the_newest_runs_and_goes_with_the_job()
    {
        var history = new BackupRunHistory(_test.Directory.Combine("history"));
        var jobId = Guid.NewGuid();
        for (var i = 0; i < BackupRunHistory.Limit + 5; i++)
        {
            history.Add(jobId, new BackupRunSummary
            {
                Started = DateTimeOffset.UnixEpoch.AddMinutes(i),
                Duration = TimeSpan.Zero,
                Outcome = BackupOutcome.Succeeded,
            });
        }

        var runs = history.Get(jobId);
        Assert.Equal(BackupRunHistory.Limit, runs.Count);
        Assert.Equal(DateTimeOffset.UnixEpoch.AddMinutes(BackupRunHistory.Limit + 4), runs[0].Started);

        history.Remove(jobId);
        Assert.Empty(history.Get(jobId));
    }

    private sealed class Report : IBackupRunReport
    {
        public int Created { get; private set; }

        public List<BackupRunPhase> Phases { get; } = [];

        void IBackupRunReport.Created(Core.Borg.BorgCreateProgress progress) => Created++;

        public void PhaseStarted(BackupRunPhase phase, TimeSpan? expected) => Phases.Add(phase);

        public void PhaseProgress(double fraction)
        {
        }
    }
}
