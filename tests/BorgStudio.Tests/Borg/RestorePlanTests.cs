using BorgStudio.Core.Borg;
using BorgStudio.Core.Processes;
using BorgStudio.Plugins;
using BorgStudio.Tests.TestSupport;

namespace BorgStudio.Tests.Borg;

/// <summary>Planning a restore (target paths, runs per drive, what would be overwritten) and running borg extract.</summary>
public sealed class RestorePlanTests : IDisposable
{
    private readonly TemporaryDirectory _directory = new();

    public void Dispose() => _directory.Dispose();

    private static readonly DateTimeOffset Modified = new(new DateTime(2026, 11, 5, 10, 0, 0));

    /// <summary>C:\Users\me\Documents (with Invoices\2026\a.pdf, b.txt) and D:\Photos\x.jpg, backed up through WSL.</summary>
    private static ArchiveTree Tree()
    {
        var tree = new ArchiveTree();
        tree.Add(new BorgItem("mnt/c/Users/me/Documents", ArchiveEntryKind.Directory, 0, Modified));
        tree.Add(new BorgItem("mnt/c/Users/me/Documents/Invoices", ArchiveEntryKind.Directory, 0, Modified));
        tree.Add(new BorgItem("mnt/c/Users/me/Documents/Invoices/2026", ArchiveEntryKind.Directory, 0, Modified));
        tree.Add(new BorgItem("mnt/c/Users/me/Documents/Invoices/2026/a.pdf", ArchiveEntryKind.File, 100, Modified));
        tree.Add(new BorgItem("mnt/c/Users/me/Documents/b.txt", ArchiveEntryKind.File, 10, Modified));
        tree.Add(new BorgItem("mnt/d/Photos", ArchiveEntryKind.Directory, 0, Modified));
        tree.Add(new BorgItem("mnt/d/Photos/x.jpg", ArchiveEntryKind.File, 1000, Modified));
        return tree.Complete();
    }

    private static string Sep(params string[] parts) => string.Join('\\', parts);

    [Fact]
    public void Into_a_folder_the_items_land_below_their_common_parent_or_with_the_full_path()
    {
        var tree = Tree();
        var invoices = tree.Find("mnt/c/Users/me/Documents/Invoices")!;
        var letter = tree.Find("mnt/c/Users/me/Documents/b.txt")!;

        var direct = RestorePlan.IntoFolder([invoices, letter], @"E:\Restore", keepFullPath: false);
        var run = Assert.Single(direct.Runs);
        Assert.Equal(["mnt/c/Users/me/Documents/Invoices", "mnt/c/Users/me/Documents/b.txt"], run.Paths);
        Assert.Equal(5, run.StripComponents); // mnt, c, Users, me, Documents
        Assert.Equal(@"E:\Restore", run.TargetDirectory);
        Assert.Equal(Sep("E:", "Restore", "Invoices", "2026", "a.pdf"), direct.Destination(tree.Find("mnt/c/Users/me/Documents/Invoices/2026/a.pdf")!));
        Assert.Equal(Sep("E:", "Restore", "b.txt"), direct.Destination(letter));
        Assert.Equal(["a.pdf", "b.txt"], direct.Files().Select(file => file.Name).Order());

        var full = RestorePlan.IntoFolder([invoices], @"E:\Restore", keepFullPath: true);
        Assert.Equal(0, full.Runs[0].StripComponents);
        Assert.Equal(Sep("E:", "Restore", "mnt", "c", "Users", "me", "Documents", "Invoices", "2026", "a.pdf"),
            full.Destination(tree.Find("mnt/c/Users/me/Documents/Invoices/2026/a.pdf")!));

        // From different places (e.g. search results): below the deepest folder they share.
        var spread = RestorePlan.IntoFolder([tree.Find("mnt/c/Users/me/Documents/Invoices/2026/a.pdf")!, letter], "/restore", keepFullPath: false);
        Assert.Equal(5, spread.Runs[0].StripComponents);
        Assert.Equal("/restore/Invoices/2026/a.pdf", spread.Destination(tree.Find("mnt/c/Users/me/Documents/Invoices/2026/a.pdf")!));
    }

    [Fact]
    public void Back_to_the_original_location_with_WSL_runs_per_drive()
    {
        var tree = Tree();
        var items = RestorePlan.Sources(tree);
        Assert.Equal(["mnt/c/Users/me/Documents", "mnt/d/Photos"], items.Select(item => item.BorgPath)); // what was backed up

        var plan = RestorePlan.ToOriginal(items, BorgRuntime.Wsl)!;
        Assert.Equal(
            [new RestoreRun(["mnt/c/Users/me/Documents"], @"C:\", 2, "C:"), new RestoreRun(["mnt/d/Photos"], @"D:\", 2, "D:")],
            plan.Runs.Select(run => run with { Paths = [.. run.Paths] }),
            new RunComparer());
        Assert.Equal(Sep("C:", "Users", "me", "Documents", "b.txt"), plan.Destination(tree.Find("mnt/c/Users/me/Documents/b.txt")!));
        Assert.Equal(Sep("D:", "Photos", "x.jpg"), plan.Destination(tree.Find("mnt/d/Photos/x.jpg")!));

        // Linux paths can't be written back through WSL; natively on Linux/macOS from the root.
        var linux = new ArchiveTree();
        linux.Add(new BorgItem("home/me/a.txt", ArchiveEntryKind.File, 1, Modified));
        linux.Complete();
        var home = RestorePlan.Sources(linux);
        Assert.False(RestorePlan.CanRestoreToOriginal(home, BorgRuntime.Wsl));
        Assert.Null(RestorePlan.ToOriginal(home, BorgRuntime.Wsl));
        Assert.Equal(!OperatingSystem.IsWindows(), RestorePlan.CanRestoreToOriginal(home, BorgRuntime.Native));
        if (!OperatingSystem.IsWindows())
        {
            var native = RestorePlan.ToOriginal(home, BorgRuntime.Native)!;
            Assert.Equal(("/", 0), (native.Runs[0].TargetDirectory, native.Runs[0].StripComponents));
            Assert.Equal("/home/me/a.txt", native.Destination(linux.Find("home/me/a.txt")!));
        }
    }

    [Fact]
    public void Files_that_exist_already_are_found_and_newer_ones_marked()
    {
        var tree = Tree();
        var target = _directory.Combine("restore");
        var plan = RestorePlan.IntoFolder([tree.Find("mnt/c/Users/me/Documents")!], target, keepFullPath: false);
        Directory.CreateDirectory(Path.Combine(target, "Documents", "Invoices", "2026"));
        var older = Path.Combine(target, "Documents", "b.txt");
        var newer = Path.Combine(target, "Documents", "Invoices", "2026", "a.pdf");
        File.WriteAllText(older, "old");
        File.SetLastWriteTimeUtc(older, Modified.UtcDateTime.AddDays(-1));
        File.WriteAllText(newer, "changed since");
        var checkedFiles = new List<long>();

        var conflicts = plan.FindConflicts(new SynchronousProgress<long>(checkedFiles.Add));

        Assert.Equal([(newer, true), (older, false)],
            conflicts.Select(conflict => (conflict.Destination, conflict.IsNewer)).OrderBy(conflict => conflict.Destination.Length).Reverse());
        Assert.Equal(2, checkedFiles[^1]);
        Assert.Empty(RestorePlan.IntoFolder([tree.Find("mnt/d/Photos")!], target, keepFullPath: false).FindConflicts());
    }

    /// <summary>Records the call and reports borg 1.4's extract progress.</summary>
    private sealed class ExtractRunner : IProcessRunner
    {
        public IReadOnlyList<string> Arguments { get; private set; } = [];

        public string? WorkingDirectory { get; private set; }

        public Task<ProcessResult?> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout,
            IReadOnlyDictionary<string, string?>? environment = null, string? standardInput = null,
            CancellationToken cancellationToken = default, Action<string>? standardErrorLine = null,
            Action<string>? standardOutputLine = null, string? workingDirectory = null)
        {
            Arguments = fileName == "sh" ? [.. arguments.Skip(3)] : arguments;
            WorkingDirectory = workingDirectory;
            string[] lines =
            [
                """{"message": "Calculating total archive size for the progress indicator (might take long for large archives)", "current": 0, "total": 0, "info": null, "operation": 1, "msgid": "extract", "type": "progress_percent", "finished": false, "time": 1.0}""",
                """{"message": " 67.6% Extracting: mnt/c/Users/me/big", "current": 2000, "total": 3000, "info": ["mnt/c/Users/me/big"], "operation": 1, "msgid": "extract", "type": "progress_percent", "finished": false, "time": 1.0}""",
                """{"type": "log_message", "levelname": "WARNING", "message": "mnt/c/Users/me/x: Permission denied", "msgid": null}""",
                """{"operation": 1, "msgid": "extract", "type": "progress_percent", "finished": true, "time": 1.0}""",
            ];
            foreach (var line in lines)
                standardErrorLine?.Invoke(line);
            return Task.FromResult<ProcessResult?>(new ProcessResult(1, "", string.Join('\n', lines)));
        }
    }

    [Fact]
    public async Task Extract_runs_in_the_target_folder_with_literal_paths_and_reports_bytes()
    {
        var runner = new ExtractRunner();
        var updates = new List<BorgExtractProgress>();

        var result = await new BorgClient(runner).ExtractAsync(
            new BorgInstallation("/usr/bin/borg", new BorgVersion(1, 4, 1), BorgRuntime.Native), new RepositoryLocation("/repo"),
            "Docs-1", ["mnt/c/Users/me/we[ir]d*"], "/restore", 4, "secret", progress: new SynchronousProgress<BorgExtractProgress>(updates.Add));

        Assert.True(result.Succeeded); // exit code 1: warnings
        Assert.Equal(["extract", "--log-json", "--progress", "--strip-components", "4", "/repo::Docs-1", "pp:mnt/c/Users/me/we[ir]d*"],
            runner.Arguments);
        Assert.Equal("/restore", runner.WorkingDirectory);
        Assert.Equal([new BorgExtractProgress(0, 0, null, Preparing: true), new BorgExtractProgress(2000, 3000, "mnt/c/Users/me/big")], updates);
        Assert.Equal(["mnt/c/Users/me/x: Permission denied"], result.Value!.Warnings);

        // borg in WSL can't reach network paths.
        var unreachable = await new BorgClient(runner, @"C:\Windows\system32\wsl.exe").ExtractAsync(
            new BorgInstallation("/usr/bin/borg", new BorgVersion(1, 4, 1), BorgRuntime.Wsl), new RepositoryLocation("/repo"),
            "Docs-1", [], @"\\server\share", 0, null);
        Assert.Equal(BorgErrorKind.UnsupportedPath, unreachable.Error!.Kind);
        Assert.Equal(@"C:\Users\me", ArchiveTree.DisplayPathOf("mnt/c/Users/me"));
        Assert.Equal(@"D:\", ArchiveTree.DisplayPathOf("mnt/d"));
        Assert.Equal("/home/me", ArchiveTree.DisplayPathOf("home/me"));
    }

    private sealed class SynchronousProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class RunComparer : IEqualityComparer<RestoreRun>
    {
        public bool Equals(RestoreRun? x, RestoreRun? y) =>
            x is not null && y is not null && x.Paths.SequenceEqual(y.Paths) && x.TargetDirectory == y.TargetDirectory
            && x.StripComponents == y.StripComponents && x.Label == y.Label;

        public int GetHashCode(RestoreRun obj) => obj.TargetDirectory.GetHashCode(StringComparison.Ordinal);
    }
}
