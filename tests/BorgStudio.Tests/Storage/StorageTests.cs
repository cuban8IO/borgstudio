using BorgStudio.Core.Backups;
using BorgStudio.Core.Borg;
using BorgStudio.Core.Notifications;
using BorgStudio.Core.Processes;
using BorgStudio.Core.Repositories;
using BorgStudio.Core.Storage;
using BorgStudio.Plugins;
using BorgStudio.Tests.TestSupport;

namespace BorgStudio.Tests.Storage;

/// <summary>Measuring a repository's storage, and when it counts as almost full or full.</summary>
public sealed class StorageTests : IDisposable
{
    private const long Gigabyte = 1024L * 1024 * 1024;

    private readonly TemporaryDirectory _directory = new();

    public void Dispose() => _directory.Dispose();

    private static RepositoryConfig Repository(int warnPercent = 10, long? capacity = null, bool restricted = false) => new()
    {
        Id = Guid.NewGuid(),
        Name = "Box",
        ProviderId = "ssh",
        ProviderValues = new Dictionary<string, string>(),
        StorageWarnPercent = warnPercent,
        StorageCapacity = capacity,
        SshKeyRestricted = restricted,
    };

    private static StorageUsage Usage(long total, long free) =>
        new() { Measured = DateTimeOffset.Now, Source = StorageSource.Disk, Total = total, Free = free };

    // --- df ---

    [Fact]
    public void Df_output_is_read_in_its_block_size()
    {
        const string gnu = """
            Filesystem     1024-blocks      Used Available Capacity Mounted on
            /dev/sdb1       1000000000 900000000 100000000      90% /home
            """;
        Assert.Equal((1000000000L * 1024, 100000000L * 1024), StorageProbe.ParseDf(gnu));

        const string macOs = """
            Filesystem    512-blocks      Used Available Capacity iused ifree %iused  Mounted on
            /dev/disk3s5   976490576 600000000 376490576    62%  1000  2000    1%   /System/Volumes/Data
            """;
        Assert.Equal((976490576L * 512, 376490576L * 512), StorageProbe.ParseDf(macOs));

        // A long file system name wraps onto its own line.
        const string wrapped = "Filesystem 1K-blocks Used Available Use% Mounted on\nserver:/very/long/export/name\n  2048 1024 1024 50% /mnt\n";
        Assert.Equal((2048L * 1024, 1024L * 1024), StorageProbe.ParseDf(wrapped));

        Assert.Null(StorageProbe.ParseDf(""));
        Assert.Null(StorageProbe.ParseDf("borg serve: invalid command"));
    }

    // --- Measuring ---

    [Fact]
    public async Task A_local_repository_is_measured_on_its_disk()
    {
        var processes = new RecordingProcesses();
        var probe = new StorageProbe(new BorgClient(processes)) { DiskSpace = _ => (1000 * Gigabyte, 50 * Gigabyte) };

        var usage = await probe.MeasureAsync(Repository(), FakeBorg.Installation, new RepositoryLocation(_directory.Path), null, null);

        Assert.Equal(StorageSource.Disk, usage!.Source);
        Assert.Equal(0.05, usage.FreeFraction!.Value, 3);
        Assert.Empty(processes.Calls);
    }

    [Fact]
    public async Task A_server_is_asked_with_df_unless_the_key_may_only_run_borg()
    {
        var processes = new RecordingProcesses();
        processes.Results["ssh"] = new ProcessResult(0, "Filesystem 1024-blocks Used Available Capacity Mounted on\nbox 1048576 524288 524288 50% /home\n", "");
        processes.Results[Path.GetFileName(FakeBorg.Installation.Path)] =
            new ProcessResult(0, """{"encryption": {"mode": "none"}, "repository": {"id": "1"}, "cache": {"stats": {"unique_csize": 1073741824}}}""", "");
        var probe = new StorageProbe(new BorgClient(processes));
        var location = new RepositoryLocation("ssh://u1@box.example:23/./backups/pc") { Ssh = new SshEndpoint("box.example", 23, "u1", "backups/pc") };
        var ssh = new SshAccess("KEY", _directory.Combine("known_hosts"));

        var server = await probe.MeasureAsync(Repository(), FakeBorg.Installation, location, null, ssh);
        Assert.Equal(StorageSource.Server, server!.Source);
        Assert.Equal(Gigabyte, server.Total);
        Assert.Equal("df -Pk backups/pc", processes.Calls.Single(call => call.FileName == "ssh").Arguments[^1]);

        // Restricted: borg's size against the capacity, or alone.
        processes.Calls.Clear();
        var capacity = await probe.MeasureAsync(Repository(capacity: 4 * Gigabyte, restricted: true), FakeBorg.Installation, location, null, ssh);
        Assert.Equal(StorageSource.Capacity, capacity!.Source);
        Assert.Equal(3 * Gigabyte, capacity.Free);
        Assert.Equal(Gigabyte, capacity.RepositorySize);
        Assert.DoesNotContain(processes.Calls, call => call.FileName == "ssh");

        var sizeOnly = await probe.MeasureAsync(Repository(restricted: true), FakeBorg.Installation, location, null, ssh);
        Assert.Equal(StorageSource.RepositoryOnly, sizeOnly!.Source);
        Assert.Null(sizeOnly.FreeFraction);

        // df answered by borg serve (key restricted on the server anyway): borg it is.
        processes.Results["ssh"] = new ProcessResult(0, "", "");
        Assert.Equal(StorageSource.RepositoryOnly, (await probe.MeasureAsync(Repository(), FakeBorg.Installation, location, null, ssh))!.Source);
    }

    // --- Levels ---

    private static BackupJob JobWith(RepositoryConfig repository, BackupRunSummary? lastRun) => new()
    {
        Id = Guid.NewGuid(),
        Name = "Docs",
        RepositoryId = repository.Id,
        LastRun = lastRun,
    };

    private static BackupRunSummary Run(BackupOutcome outcome, long added = 0, bool outOfSpace = false) => new()
    {
        Started = DateTimeOffset.Now,
        Duration = TimeSpan.Zero,
        Outcome = outcome,
        DeduplicatedSize = added,
        OutOfSpace = outOfSpace,
    };

    [Fact]
    public void Storage_is_almost_full_below_the_threshold_and_full_by_the_chosen_rules()
    {
        var repository = Repository(warnPercent: 10);
        var all = new StorageFullCriteria();
        var none = new StorageFullCriteria { BelowOnePercent = false, NextBackupWontFit = false, BackupFailed = false };
        BackupJob[] noJobs = [];

        Assert.Equal(StorageLevel.Ok, StorageLevels.Evaluate(Usage(100, 20), repository, noJobs, all));
        Assert.Equal(StorageLevel.Low, StorageLevels.Evaluate(Usage(100, 9), repository, noJobs, all));
        Assert.Equal(StorageLevel.Ok, StorageLevels.Evaluate(Usage(100, 9), Repository(warnPercent: 0), noJobs, all));
        Assert.Equal(StorageLevel.Ok, StorageLevels.Evaluate(null, repository, noJobs, all));

        // Below 1 %.
        Assert.Equal(StorageLevel.Full, StorageLevels.Evaluate(Usage(1000, 9), repository, noJobs, all));
        Assert.Equal(StorageLevel.Low, StorageLevels.Evaluate(Usage(1000, 9), repository, noJobs, none));

        // The next backup would not fit.
        BackupJob[] big = [JobWith(repository, Run(BackupOutcome.Succeeded, added: 60))];
        Assert.Equal(StorageLevel.Full, StorageLevels.Evaluate(Usage(1000, 50), repository, big, all));
        Assert.Equal(StorageLevel.Low, StorageLevels.Evaluate(Usage(1000, 50), repository, big, none));

        // A backup ran out of space – also without any measurement.
        BackupJob[] failed = [JobWith(repository, Run(BackupOutcome.Failed, outOfSpace: true))];
        Assert.Equal(StorageLevel.Full, StorageLevels.Evaluate(null, repository, failed, all));
        Assert.Equal(StorageLevel.Ok, StorageLevels.Evaluate(null, repository, failed, none));
        // Other repositories' jobs don't count.
        Assert.Equal(StorageLevel.Ok, StorageLevels.Evaluate(null, Repository(), failed, all));
    }

    [Fact]
    public void Storage_is_reported_when_it_gets_worse_and_once_while_it_stays()
    {
        var policy = NotificationPolicy.ForDataDirectory(_directory.Path);
        var repository = Guid.NewGuid();

        Assert.Null(policy.DecideStorage(repository, StorageLevel.Ok));
        Assert.Equal(new NotificationDecision(NotificationEvent.StorageLow, false), policy.DecideStorage(repository, StorageLevel.Low));
        Assert.Equal(new NotificationDecision(NotificationEvent.StorageLow, true), policy.DecideStorage(repository, StorageLevel.Low));
        Assert.Equal(new NotificationDecision(NotificationEvent.StorageFull, false), policy.DecideStorage(repository, StorageLevel.Full));
        // Better, but not good: nothing to report.
        Assert.Null(policy.DecideStorage(repository, StorageLevel.Low));
        Assert.Null(policy.DecideStorage(repository, StorageLevel.Ok));
        Assert.False(policy.DecideStorage(repository, StorageLevel.Low)!.IsRepeat);
    }

    [Fact]
    public void Measurements_are_kept_per_repository()
    {
        var store = StorageUsageStore.ForDataDirectory(_directory.Path);
        var repository = Guid.NewGuid();
        store.Set(repository, Usage(100, 40));

        Assert.Equal(40, StorageUsageStore.ForDataDirectory(_directory.Path).Get(repository)!.Free);
        store.Remove(repository);
        Assert.Null(store.Get(repository));
    }

    // --- borg ---

    [Fact]
    public void Borg_reports_a_full_disk_and_the_repository_size()
    {
        Assert.Equal(BorgErrorKind.NoSpace, BorgClient.ParseError(
            """{"type": "log_message", "levelname": "ERROR", "message": "[Errno 28] No space left on device: '/repo/data/1/123'"}""").Kind);
        Assert.Equal(BorgErrorKind.NoSpace, BorgClient.ParseError(
            """{"type": "log_message", "levelname": "ERROR", "msgid": "Repository.InsufficientFreeSpaceError", "message": "Insufficient free space"}""").Kind);

        var info = BorgClient.ParseInfo("""{"encryption": {"mode": "none"}, "repository": {"id": "1"}, "cache": {"stats": {"unique_csize": 4096}}}""");
        Assert.Equal(4096, info.UniqueCompressedSize);
        Assert.Null(BorgClient.ParseInfo("""{"encryption": {"mode": "none"}, "repository": {"id": "1"}}""").UniqueCompressedSize);
    }
}
