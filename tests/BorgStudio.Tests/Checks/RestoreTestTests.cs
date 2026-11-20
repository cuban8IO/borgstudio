using BorgStudio.Core.Borg;
using BorgStudio.Core.Checks;
using BorgStudio.Core.Notifications;
using BorgStudio.Tests.TestSupport;

namespace BorgStudio.Tests.Checks;

/// <summary>Restore tests: picking the sample, judging what came back, mapping archive paths, reporting.</summary>
public sealed class RestoreTestTests : IDisposable
{
    private readonly TemporaryDirectory _directory = new();

    public void Dispose() => _directory.Dispose();

    private static readonly DateTimeOffset Modified = new(2026, 11, 5, 10, 20, 30, TimeSpan.FromHours(1));

    private static BorgItem File(string path, long size) => new(path, ArchiveEntryKind.File, size, Modified);

    /// <summary>The borg this computer would use: in WSL on Windows, natively elsewhere.</summary>
    private static BorgInstallation Borg => OperatingSystem.IsWindows()
        ? new BorgInstallation("/usr/bin/borg", new BorgVersion(1, 4, 4), BorgRuntime.Wsl)
        : new BorgInstallation("/usr/bin/borg", new BorgVersion(1, 4, 4), BorgRuntime.Native);

    private static string ArchivePath(string localPath) => BorgClient.BorgPath(Borg, localPath)!.TrimStart('/');

    [Fact]
    public void The_sample_leaves_out_large_files_folders_and_keeps_the_limits()
    {
        var sample = new RestoreSample(count: 5, maxFileBytes: 100, maxTotalBytes: 250, new Random(7));
        sample.Offer(new BorgItem("home/me", ArchiveEntryKind.Directory, 0, Modified));
        sample.Offer(File("home/me/huge.iso", 101));
        sample.Offer(File("home/me/empty", 0));
        for (var number = 0; number < 50; number++)
            sample.Offer(File($"home/me/{number}.txt", 60));

        var picked = sample.Pick();

        Assert.Equal(4, picked.Count);
        Assert.All(picked, item => Assert.Equal(60, item.Size));
        Assert.Equal(4, picked.Select(item => item.Path).Distinct().Count());
    }

    [Fact]
    public void The_sample_is_spread_over_the_whole_archive()
    {
        var seen = new HashSet<string>();
        for (var seed = 0; seed < 40; seed++)
        {
            var sample = new RestoreSample(count: 1, maxFileBytes: 1000, maxTotalBytes: 1000, new Random(seed));
            for (var number = 0; number < 1000; number++)
                sample.Offer(File($"f{number}", 10));
            seen.Add(sample.Pick().Single().Path);
        }

        Assert.True(seen.Count > 30, $"only {seen.Count} different files");
    }

    [Fact]
    public void Archive_paths_map_back_to_this_computer()
    {
        var wsl = new BorgInstallation("/usr/bin/borg", new BorgVersion(1, 4, 4), BorgRuntime.Wsl);
        var native = new BorgInstallation("/usr/bin/borg", new BorgVersion(1, 4, 4), BorgRuntime.Native);

        Assert.Equal(@"C:\Users\me\a b.txt", BorgClient.LocalPath(wsl, "mnt/c/Users/me/a b.txt"));
        Assert.Null(BorgClient.LocalPath(wsl, "home/me/a.txt"));
        Assert.Equal("/home/me/a.txt", BorgClient.LocalPath(native, "home/me/a.txt"));
    }

    private (BorgItem Item, string Source, string Target) Restored(string name, string content, string? restoredContent = null)
    {
        var source = _directory.Combine("source", name);
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        System.IO.File.WriteAllText(source, content);
        var modified = new DateTimeOffset(System.IO.File.GetLastWriteTimeUtc(source), TimeSpan.Zero);
        var item = new BorgItem(ArchivePath(source), ArchiveEntryKind.File, content.Length, modified);
        var target = _directory.Combine("target");
        var restored = Path.Combine(target, item.Path.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(restored)!);
        System.IO.File.WriteAllText(restored, restoredContent ?? content);
        return (item, source, target);
    }

    [Fact]
    public void An_unchanged_source_is_compared_byte_by_byte()
    {
        var (same, _, target) = Restored("same.txt", "hello world");
        Assert.Equal(RestoredFileResult.Identical,
            RestoreTester.Judge(Borg, same, target, RestoreTestMode.Compare, RestoreTestComparison.SizeAndTime).Result);

        var (different, _, _) = Restored("different.txt", "hello world", "hello WORLD");
        Assert.Equal(RestoredFileResult.Different,
            RestoreTester.Judge(Borg, different, target, RestoreTestMode.Compare, RestoreTestComparison.SizeAndTime).Result);

        // Only read, not compared.
        Assert.Equal(RestoredFileResult.Restored,
            RestoreTester.Judge(Borg, different, target, RestoreTestMode.Read, RestoreTestComparison.SizeAndTime).Result);
    }

    [Fact]
    public void A_source_changed_since_is_only_checked_for_coming_back_whole()
    {
        var (item, source, target) = Restored("changed.txt", "hello world", "hello WORLD");
        System.IO.File.SetLastWriteTimeUtc(source, item.Modified.UtcDateTime.AddMinutes(5));

        Assert.Equal(RestoredFileResult.Restored,
            RestoreTester.Judge(Borg, item, target, RestoreTestMode.Compare, RestoreTestComparison.SizeAndTime).Result);
        // By size alone it counts as unchanged – and differs.
        Assert.Equal(RestoredFileResult.Different,
            RestoreTester.Judge(Borg, item, target, RestoreTestMode.Compare, RestoreTestComparison.SizeOnly).Result);
    }

    [Fact]
    public void A_file_that_did_not_come_back_whole_is_missing()
    {
        var (item, _, target) = Restored("short.txt", "hello world", "hello");

        Assert.Equal(RestoredFileResult.Missing, RestoreTester.Judge(Borg, item, target, RestoreTestMode.Read, RestoreTestComparison.SizeAndTime).Result);
        Assert.Equal(RestoredFileResult.Missing,
            RestoreTester.Judge(Borg, item with { Path = item.Path + ".gone" }, target, RestoreTestMode.Read, RestoreTestComparison.SizeAndTime).Result);
    }

    [Fact]
    public void Restore_tests_are_reported_like_checks()
    {
        var policy = NotificationPolicy.ForDataDirectory(_directory.Path);
        var job = Guid.NewGuid();

        Assert.Null(policy.DecideRestoreTest(job, RestoreTestOutcome.Ok));
        Assert.Equal(NotificationEvent.CheckFailed, policy.DecideRestoreTest(job, RestoreTestOutcome.Failed)!.Event);
        Assert.True(policy.DecideRestoreTest(job, RestoreTestOutcome.Failed)!.IsRepeat);
        Assert.Null(policy.DecideRestoreTest(job, RestoreTestOutcome.Cancelled));
        Assert.Equal(NotificationEvent.Recovered, policy.DecideRestoreTest(job, RestoreTestOutcome.Ok)!.Event);
        Assert.Null(policy.DecideRestoreTest(job, RestoreTestOutcome.NotRun));
        Assert.Equal(NotificationEvent.CheckFailed, policy.DecideRestoreTest(job, RestoreTestOutcome.NotRun, automatic: true)!.Event);

        policy.Forget(job);
        Assert.Null(policy.DecideRestoreTest(job, RestoreTestOutcome.Ok));
    }

    [Fact]
    public void The_store_keeps_each_jobs_tests()
    {
        var store = RestoreTestStore.ForDataDirectory(_directory.Path);
        var job = Guid.NewGuid();
        store.Add(job, new RestoreTestRecord(Modified, TimeSpan.FromSeconds(9), RestoreTestMode.Compare, RestoreTestOutcome.Ok)
        {
            Files = [new RestoredFile("a", 10, RestoredFileResult.Identical), new RestoredFile("b", 5, RestoredFileResult.Restored)],
        });

        var loaded = Assert.Single(store.Get(job));
        Assert.Equal(1, loaded.Compared);
        Assert.Equal(15, loaded.Bytes);
        Assert.Empty(loaded.Problems);
        store.Remove(job);
        Assert.Empty(store.Get(job));
    }
}
