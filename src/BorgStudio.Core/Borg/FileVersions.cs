namespace BorgStudio.Core.Borg;

/// <summary>What an entry looks like in one archive – enough to tell whether it changed between two archives.</summary>
/// <param name="FileCount">For folders: the files inside (a folder's own date says little).</param>
public sealed record ArchiveEntryState(ArchiveEntryKind Kind, long Size, DateTimeOffset Modified, long FileCount)
{
    public static ArchiveEntryState Of(ArchiveNode node) =>
        new(node.Kind, node.Size, node.Modified, node.IsDirectory ? node.FileCount : 0);

    /// <summary>Unchanged: files by size and date, folders by size and number of files.</summary>
    public bool IsSameAs(ArchiveEntryState other) =>
        Kind == other.Kind && Size == other.Size
        && (Kind == ArchiveEntryKind.Directory ? FileCount == other.FileCount : Modified == other.Modified);
}

/// <summary>
/// One state of an entry over several archives in a row – or a stretch of archives it is missing from
/// (<see cref="State"/> <c>null</c>).
/// </summary>
/// <param name="Archives">Newest first.</param>
public sealed record FileVersion(ArchiveEntryState? State, IReadOnlyList<BorgArchive> Archives)
{
    public bool IsMissing => State is null;

    /// <summary>The newest archive with this state – the one to restore it from.</summary>
    public BorgArchive Newest => Archives[0];

    public BorgArchive Oldest => Archives[^1];
}

/// <summary>The versions of an entry across the archives of a repository: only where it changed.</summary>
public static class FileVersions
{
    /// <summary>
    /// Groups archives in a row (newest first, as given) in which the entry looks the same; archives without it form
    /// stretches of their own (deleted, or not there yet).
    /// </summary>
    public static IReadOnlyList<FileVersion> Group(IEnumerable<(BorgArchive Archive, ArchiveEntryState? State)> archives)
    {
        var versions = new List<FileVersion>();
        ArchiveEntryState? state = null;
        List<BorgArchive>? run = null;
        foreach (var (archive, current) in archives)
        {
            if (run is not null && Same(state, current))
            {
                run.Add(archive);
                continue;
            }
            if (run is not null)
                versions.Add(new FileVersion(state, run));
            (state, run) = (current, [archive]);
        }
        if (run is not null)
            versions.Add(new FileVersion(state, run));
        return versions;
    }

    private static bool Same(ArchiveEntryState? a, ArchiveEntryState? b) =>
        a is null ? b is null : b is not null && a.IsSameAs(b);
}

/// <summary>An entry found in one or more archives, with how it looks in each.</summary>
public sealed class ArchiveSearchHit(string borgPath, string name, ArchiveEntryKind kind)
{
    private readonly Dictionary<string, ArchiveEntryState> _states = new(StringComparer.Ordinal);

    public string BorgPath { get; } = borgPath;

    public string Name { get; } = name;

    public ArchiveEntryKind Kind { get; } = kind;

    public bool IsDirectory => Kind == ArchiveEntryKind.Directory;

    /// <summary>"C:\Users\me\Dokumente" – where it is.</summary>
    public string FolderDisplayPath => BorgPath.LastIndexOf('/') is var slash and > 0 ? ArchiveTree.DisplayPathOf(BorgPath[..slash]) : "";

    /// <summary>The archives it is in, newest first (as searched).</summary>
    public List<BorgArchive> Archives { get; } = [];

    public ArchiveEntryState? StateIn(BorgArchive archive) => _states.GetValueOrDefault(archive.Id);

    /// <summary>How it looks in the newest archive it is in.</summary>
    public ArchiveEntryState Newest => _states[Archives[0].Id];

    internal void Add(BorgArchive archive, ArchiveEntryState state)
    {
        if (_states.TryAdd(archive.Id, state))
            Archives.Add(archive);
    }

    /// <summary>Its versions in <paramref name="searched"/> (newest first).</summary>
    public IReadOnlyList<FileVersion> Versions(IEnumerable<BorgArchive> searched) =>
        FileVersions.Group(searched.Select(archive => (archive, StateIn(archive))));
}

/// <summary>
/// Searching the names in many archives, one archive after the other (only one archive's contents in memory at a time):
/// one hit per path, with its state in every archive searched.
/// </summary>
public sealed class ArchiveSearch(Func<string, bool> matches, int maximumHits)
{
    private readonly Dictionary<string, ArchiveSearchHit> _hits = new(StringComparer.Ordinal);

    /// <summary>The archives searched so far, in order.</summary>
    public List<BorgArchive> Searched { get; } = [];

    /// <summary>In the order found (newest archives first).</summary>
    public List<ArchiveSearchHit> Hits { get; } = [];

    /// <summary>More paths matched than <c>maximumHits</c>: those beyond were left out.</summary>
    public bool TooMany { get; private set; }

    public void Add(BorgArchive archive, ArchiveTree tree, CancellationToken cancellationToken = default)
    {
        Searched.Add(archive);
        var checkedNodes = 0;
        foreach (var node in tree.All())
        {
            if (++checkedNodes % 4096 == 0)
                cancellationToken.ThrowIfCancellationRequested();
            if (!matches(node.Name))
                continue;
            var path = node.BorgPath;
            if (!_hits.TryGetValue(path, out var hit))
            {
                if (Hits.Count >= maximumHits)
                {
                    TooMany = true;
                    continue;
                }
                hit = _hits[path] = new ArchiveSearchHit(path, node.Name, node.Kind);
                Hits.Add(hit);
            }
            hit.Add(archive, ArchiveEntryState.Of(node));
        }
    }
}
