using BorgStudio.Core.Borg;

namespace BorgStudio.Tests.Borg;

/// <summary>Versions of a file across archives (only where it changed) and the search over several archives.</summary>
public sealed class FileVersionsTests
{
    private static readonly DateTimeOffset Monday = new(2026, 11, 2, 9, 0, 0, TimeSpan.FromHours(1));

    private static BorgArchive Archive(int day) =>
        new($"Docs-host-{day}", $"{day:x2}aa11bb", Monday.AddDays(day));

    private static ArchiveEntryState File(long size, int minute) =>
        new(ArchiveEntryKind.File, size, Monday.AddMinutes(minute), 0);

    [Fact]
    public void Archives_in_a_row_with_the_same_file_are_one_version()
    {
        // Newest first: deleted in the newest, changed once before, missing in the oldest (not there yet).
        var versions = FileVersions.Group(
        [
            (Archive(6), null),
            (Archive(5), File(200, 30)),
            (Archive(4), File(200, 30)),
            (Archive(3), File(100, 10)),
            (Archive(2), File(100, 10)),
            (Archive(1), File(100, 10)),
            (Archive(0), null),
        ]);

        Assert.Equal([true, false, false, true], versions.Select(version => version.IsMissing));
        Assert.Equal([1, 2, 3, 1], versions.Select(version => version.Archives.Count));
        Assert.Equal(Archive(5), versions[1].Newest);
        Assert.Equal(Archive(4), versions[1].Oldest);
        Assert.Equal(100, versions[2].State!.Size);
        Assert.Empty(FileVersions.Group([]));
    }

    [Fact]
    public void Files_differ_by_size_or_date_folders_by_their_contents()
    {
        Assert.True(File(100, 10).IsSameAs(File(100, 10)));
        Assert.False(File(100, 10).IsSameAs(File(101, 10)));
        Assert.False(File(100, 10).IsSameAs(File(100, 11)));

        var folder = new ArchiveEntryState(ArchiveEntryKind.Directory, 500, Monday, 3);
        Assert.True(folder.IsSameAs(folder with { Modified = Monday.AddDays(1) })); // a folder's date says little
        Assert.False(folder.IsSameAs(folder with { FileCount = 4 }));
        Assert.False(folder.IsSameAs(File(500, 0)));
    }

    [Fact]
    public void The_search_collects_each_path_once_with_its_state_in_every_archive()
    {
        var newer = Tree(("mnt/c/Users/me/Docs/letter.txt", 200), ("mnt/c/Users/me/Docs/invoice.pdf", 50));
        var older = Tree(("mnt/c/Users/me/Docs/letter.txt", 100), ("mnt/c/Users/me/Docs/old-letter.txt", 10));
        var search = new ArchiveSearch(name => name.Contains("letter", StringComparison.OrdinalIgnoreCase), maximumHits: 10);

        search.Add(Archive(2), newer);
        search.Add(Archive(1), older);

        Assert.Equal([Archive(2), Archive(1)], search.Searched);
        Assert.Equal(["letter.txt", "old-letter.txt"], search.Hits.Select(hit => hit.Name));
        var letter = search.Hits[0];
        Assert.Equal("mnt/c/Users/me/Docs/letter.txt", letter.BorgPath);
        Assert.Equal(@"C:\Users\me\Docs", letter.FolderDisplayPath);
        Assert.Equal([Archive(2), Archive(1)], letter.Archives);
        Assert.Equal(200, letter.Newest.Size);
        Assert.Equal(2, letter.Versions(search.Searched).Count);

        var old = search.Hits[1];
        Assert.Null(old.StateIn(Archive(2)));
        Assert.Equal([true, false], old.Versions(search.Searched).Select(version => version.IsMissing));
        Assert.False(search.TooMany);
    }

    [Fact]
    public void Beyond_the_maximum_new_paths_are_left_out()
    {
        var search = new ArchiveSearch(_ => true, maximumHits: 2);
        search.Add(Archive(1), Tree(("home/a", 1), ("home/b", 1), ("home/c", 1)));

        Assert.Equal(2, search.Hits.Count); // "home" and "a"
        Assert.True(search.TooMany);
    }

    private static ArchiveTree Tree(params (string Path, long Size)[] files)
    {
        var tree = new ArchiveTree();
        foreach (var (path, size) in files)
            tree.Add(new BorgItem(path, ArchiveEntryKind.File, size, Monday.AddMinutes(size)));
        return tree.Complete();
    }
}
