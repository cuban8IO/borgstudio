using BorgStudio.Core.Borg;
using BorgStudio.Core.Processes;
using BorgStudio.Core.Security;
using BorgStudio.Plugins;
using BorgStudio.Tests.TestSupport;

namespace BorgStudio.Tests.Borg;

/// <summary>Archive contents: reading borg's list, the folder tree (Windows drives through WSL), caching it.</summary>
public sealed class ArchiveTreeTests : IDisposable
{
    private readonly TemporaryDirectory _directory = new();

    public void Dispose() => _directory.Dispose();

    private static readonly DateTimeOffset Modified = new(new DateTime(2026, 11, 5, 10, 20, 30));

    private static BorgItem File(string path, long size) => new(path, ArchiveEntryKind.File, size, Modified);

    private static BorgItem Folder(string path) => new(path, ArchiveEntryKind.Directory, 0, Modified);

    /// <summary>Like a backup of C:\Users\me\Documents through WSL.</summary>
    private static ArchiveTree WindowsTree()
    {
        var tree = new ArchiveTree();
        tree.Add(Folder("mnt/c/Users/me/Documents"));
        tree.Add(File("mnt/c/Users/me/Documents/b.txt", 20));
        tree.Add(Folder("mnt/c/Users/me/Documents/Invoices"));
        tree.Add(File("mnt/c/Users/me/Documents/Invoices/2026.pdf", 300));
        tree.Add(File("mnt/c/Users/me/Documents/A.txt", 10));
        tree.Add(new BorgItem("mnt/c/Users/me/Documents/link", ArchiveEntryKind.Symlink, 0, Modified, "A.txt"));
        tree.Add(File("mnt/d/Photos/x.jpg", 4000));
        return tree.Complete();
    }

    [Fact]
    public void A_line_of_borg_list_becomes_an_item()
    {
        var item = BorgItem.Parse(
            """{"type": "-", "mode": "-rw-r--r--", "user": "me", "group": "me", "uid": 1000, "gid": 1000, "path": "tmp/src/sub/b c.txt", "healthy": true, "source": "", "linktarget": "", "flags": 0, "size": 2, "mtime": "2026-11-06T22:36:09.331000"}""");
        Assert.Equal(new BorgItem("tmp/src/sub/b c.txt", ArchiveEntryKind.File, 2, new DateTimeOffset(new DateTime(2026, 11, 6, 22, 36, 9, 331))), item);

        Assert.Equal(ArchiveEntryKind.Directory, BorgItem.Parse("""{"type": "d", "path": "tmp/src", "size": 0}""")!.Kind);
        Assert.Equal(("a", ArchiveEntryKind.Symlink), BorgItem.Parse("""{"type": "l", "path": "tmp/link", "linktarget": "a"}""") is { } link
            ? (link.LinkTarget, link.Kind) : default);
        Assert.Equal(ArchiveEntryKind.Other, BorgItem.Parse("""{"type": "p", "path": "tmp/fifo"}""")!.Kind);
        Assert.Null(BorgItem.Parse(""));
        Assert.Null(BorgItem.Parse("""{"type": "log_message", "message": "x"}"""));
        Assert.Null(BorgItem.Parse("not json"));
    }

    [Fact]
    public void Windows_drives_appear_as_drive_letters_with_folders_first_and_sizes_summed_up()
    {
        var tree = WindowsTree();

        Assert.Equal(["C:", "D:"], tree.Root.Children.Select(node => node.Name));
        var documents = tree.Find("mnt/c/Users/me/Documents")!;
        Assert.Equal(["Invoices", "A.txt", "b.txt", "link"], documents.Children.Select(node => node.Name)); // folders first, then by name
        Assert.Equal(330, documents.Size);
        Assert.Equal(4, documents.FileCount);
        Assert.Equal(4330, tree.Root.Size);
        Assert.Equal(5, tree.Root.FileCount);
        Assert.Equal(7, tree.EntryCount);

        Assert.True(tree.Find("mnt/c/Users")!.IsImplicit); // not stored by borg, only above the backed-up folder
        Assert.False(documents.IsImplicit);
        var pdf = tree.Find("mnt/c/Users/me/Documents/Invoices/2026.pdf")!;
        Assert.Equal("mnt/c/Users/me/Documents/Invoices/2026.pdf", pdf.BorgPath);
        Assert.Equal(@"C:\Users\me\Documents\Invoices\2026.pdf", pdf.DisplayPath);
        Assert.Equal(@"C:\", tree.Root.Children[0].DisplayPath);
        Assert.Equal("mnt/c", tree.Root.Children[0].BorgPath);
        Assert.Equal("A.txt", tree.Find("mnt/c/Users/me/Documents/link")!.LinkTarget);
        Assert.Null(tree.Find("mnt/c/Nope"));
    }

    [Fact]
    public void Other_paths_stay_as_they_are()
    {
        var tree = new ArchiveTree();
        tree.Add(File("home/me/a.txt", 1));
        tree.Add(File("mnt/backup/b.txt", 2)); // "mnt/backup" is no drive
        tree.Add(Folder("home/me")); // listed after its contents: no longer implicit, no duplicate
        tree.Complete();

        Assert.Equal(["home", "mnt"], tree.Root.Children.Select(node => node.Name));
        Assert.Equal("/home/me/a.txt", tree.Find("home/me/a.txt")!.DisplayPath);
        Assert.Equal("mnt/backup/b.txt", tree.Find("mnt/backup/b.txt")!.BorgPath);
        Assert.False(tree.Find("home/me")!.IsImplicit);
        Assert.Single(tree.Find("home")!.Children);
        Assert.Equal(["home", "home/me", "home/me/a.txt", "mnt", "mnt/backup", "mnt/backup/b.txt"], tree.All().Select(node => node.BorgPath));
        Assert.Throws<InvalidOperationException>(() => tree.Add(File("late.txt", 1)));
    }

    [Fact]
    public void Contents_are_cached_plain_without_a_PIN_and_encrypted_with_one()
    {
        var vault = new Vault(_directory.Combine("vault.json"), TestServices.FastKdf);
        var cache = new ArchiveCache(_directory.Combine("archives"), vault);
        var repository = Guid.NewGuid();
        const string archive = "3289a50a66be17669eff18e6acf5730576c02eec768e0bf0ddda075857d112e8";
        Assert.Null(cache.LoadContents(repository, archive));

        cache.SaveContents(repository, archive, WindowsTree());
        var folder = Path.Combine(cache.Directory, "contents", repository.ToString("N"));
        Assert.Equal([archive + ".gz"], Directory.GetFiles(folder).Select(Path.GetFileName));
        AssertSameTree(WindowsTree(), cache.LoadContents(repository, archive)!);

        vault.SetUp("123456");
        cache.Secure();
        Assert.Equal([archive + ".vault"], Directory.GetFiles(folder).Select(Path.GetFileName));
        AssertSameTree(WindowsTree(), cache.LoadContents(repository, archive)!);
        vault.Lock();
        Assert.Null(cache.LoadContents(repository, archive));
        Assert.True(vault.Unlock("123456"));

        // Deleted archives lose their contents; so does a removed repository.
        cache.KeepContents(repository, ["aa11bb22cc33"]);
        Assert.Empty(Directory.GetFiles(folder));
        cache.SaveContents(repository, archive, WindowsTree());
        cache.Remove(repository);
        Assert.False(Directory.Exists(folder));

        Assert.Null(cache.LoadContents(repository, "../../evil")); // ids are hex only
    }

    private static void AssertSameTree(ArchiveTree expected, ArchiveTree actual)
    {
        Assert.Equal(expected.All().Select(Describe), actual.All().Select(Describe));
        Assert.Equal(expected.Root.Size, actual.Root.Size);

        static string Describe(ArchiveNode node) =>
            $"{node.BorgPath}|{node.Kind}|{node.Size}|{node.Modified.ToUnixTimeMilliseconds()}|{node.LinkTarget}|{node.IsImplicit}";
    }

    /// <summary>Hands borg's lines out on standard output.</summary>
    private sealed class OutputRunner(params string[] lines) : IProcessRunner
    {
        public IReadOnlyList<string> Arguments { get; private set; } = [];

        public Task<ProcessResult?> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout,
            IReadOnlyDictionary<string, string?>? environment = null, string? standardInput = null,
            CancellationToken cancellationToken = default, Action<string>? standardErrorLine = null, Action<string>? standardOutputLine = null, string? workingDirectory = null)
        {
            Arguments = arguments;
            foreach (var line in lines)
                standardOutputLine?.Invoke(line);
            return Task.FromResult<ProcessResult?>(new ProcessResult(0, standardOutputLine is null ? string.Join('\n', lines) : "", ""));
        }
    }

    [Fact]
    public async Task The_contents_are_read_line_by_line_into_a_tree()
    {
        var lines = Enumerable.Range(0, 2500)
            .Select(index => $$"""{"type": "-", "path": "home/me/file{{index}}.txt", "size": 10, "mtime": "2026-11-05T10:20:30.000000"}""")
            .Prepend("""{"type": "d", "path": "home/me", "size": 0, "mtime": "2026-11-05T10:20:30.000000"}""")
            .ToArray();
        var runner = new OutputRunner(lines);
        var read = new List<long>();

        var result = await new BorgClient(runner).ListContentsAsync(
            new BorgInstallation("/usr/bin/borg", new BorgVersion(1, 4, 1), BorgRuntime.Native), new RepositoryLocation("/repo"),
            "Docs-host-1", "secret", entriesRead: new SynchronousProgress<long>(read.Add));

        Assert.True(result.Succeeded);
        Assert.Equal(["list", "--log-json", "--json-lines", "/repo::Docs-host-1"], runner.Arguments);
        Assert.Equal(2501, result.Value!.EntryCount);
        Assert.Equal(25_000, result.Value.Root.Size);
        Assert.Equal([1000, 2000, 2501], read); // every 1000 and at the end
    }

    private sealed class SynchronousProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
