namespace BorgStudio.Core.Borg;

/// <summary>One <c>borg extract</c>: these borg paths into this folder, dropping that many leading path segments.</summary>
/// <param name="Label">For progress with several runs, e.g. "C:".</param>
public sealed record RestoreRun(IReadOnlyList<string> Paths, string TargetDirectory, int StripComponents, string? Label = null);

/// <summary>Where a file would land, and whether something is there already.</summary>
public sealed record RestoreConflict(string Destination, bool IsNewer);

/// <summary>
/// How to restore a selection of an archive: into a folder (directly, or with the full path as in the archive) or back
/// to where it came from. Works out the <c>borg extract</c> runs and the destination of every file – to tell beforehand
/// what would be overwritten.
/// </summary>
public sealed class RestorePlan
{
    private readonly List<(ArchiveNode Item, RestoreRun Run)> _items = [];

    private RestorePlan(IReadOnlyList<RestoreRun> runs) => Runs = runs;

    public IReadOnlyList<RestoreRun> Runs { get; }

    /// <summary>Into <paramref name="folder"/> (a path on this computer).</summary>
    /// <param name="keepFullPath">Keep the whole path as in the archive ("mnt/c/Users/…"); otherwise the items land
    /// directly in the folder, below their common parent.</param>
    public static RestorePlan IntoFolder(IReadOnlyList<ArchiveNode> items, string folder, bool keepFullPath)
    {
        var strip = keepFullPath ? 0 : Segments(CommonParent(items).BorgPath);
        var run = new RestoreRun(Paths(items), folder, strip);
        var plan = new RestorePlan([run]);
        plan._items.AddRange(items.Select(item => (item, run)));
        return plan;
    }

    /// <summary>
    /// Back where the items came from; <c>null</c> if that's not possible (see <see cref="CanRestoreToOriginal"/>). With
    /// borg in WSL, Windows drives are written per drive ("C:\" with "mnt/c" dropped); natively, from the root.
    /// </summary>
    public static RestorePlan? ToOriginal(IReadOnlyList<ArchiveNode> items, BorgRuntime runtime)
    {
        if (!CanRestoreToOriginal(items, runtime))
            return null;
        if (runtime == BorgRuntime.Native)
        {
            var run = new RestoreRun(Paths(items), "/", 0);
            var plan = new RestorePlan([run]);
            plan._items.AddRange(items.Select(item => (item, run)));
            return plan;
        }

        var byDrive = items.GroupBy(item => Drive(item)!.Name).OrderBy(group => group.Key, StringComparer.Ordinal).ToList();
        var runs = byDrive.Select(group => new RestoreRun(Paths([.. group]), group.Key + "\\", 2, group.Key)).ToList();
        var result = new RestorePlan(runs);
        for (var index = 0; index < byDrive.Count; index++)
            result._items.AddRange(byDrive[index].Select(item => (item, runs[index])));
        return result;
    }

    /// <summary>
    /// With borg in WSL only Windows drives can be written back ("mnt/c/…"); natively on Linux/macOS any path. A native
    /// borg on Windows has no way back.
    /// </summary>
    public static bool CanRestoreToOriginal(IReadOnlyList<ArchiveNode> items, BorgRuntime runtime) =>
        items.Count > 0 && runtime switch
        {
            BorgRuntime.Wsl => items.All(item => Drive(item) is not null),
            BorgRuntime.Native => !OperatingSystem.IsWindows(),
            _ => false,
        };

    /// <summary>The backed-up folders and files of an archive (the topmost entries borg stored itself) – "the whole archive".</summary>
    public static IReadOnlyList<ArchiveNode> Sources(ArchiveTree tree)
    {
        var sources = new List<ArchiveNode>();
        var stack = new Stack<ArchiveNode>([tree.Root]);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            foreach (var child in node.Children.Reverse())
                if (child is { IsDirectory: true, IsImplicit: true })
                    stack.Push(child);
                else
                    sources.Add(child);
        }
        return [.. sources.OrderBy(node => node.BorgPath, StringComparer.Ordinal)];
    }

    /// <summary>Where a file of the selection lands (a path on this computer).</summary>
    public string Destination(ArchiveNode node)
    {
        var run = _items.FirstOrDefault(entry => node == entry.Item || IsBelow(node, entry.Item)).Run
            ?? throw new ArgumentException("Not part of the selection.", nameof(node));
        var segments = node.BorgPath.Split('/').Skip(run.StripComponents);
        var separator = run.TargetDirectory.Contains('\\') ? '\\'
            : run.TargetDirectory.Contains('/') ? '/'
            : Path.DirectorySeparatorChar;
        return run.TargetDirectory.TrimEnd('\\', '/') + separator + string.Join(separator, segments);
    }

    /// <summary>All files (and links) of the selection, with every folder's contents.</summary>
    public IEnumerable<ArchiveNode> Files()
    {
        foreach (var (item, _) in _items)
        {
            var stack = new Stack<ArchiveNode>([item]);
            while (stack.Count > 0)
            {
                var node = stack.Pop();
                if (!node.IsDirectory)
                    yield return node;
                foreach (var child in node.Children)
                    stack.Push(child);
            }
        }
    }

    /// <summary>The files that exist at their destination already (would be overwritten); <paramref name="progress"/> counts the files checked.</summary>
    public IReadOnlyList<RestoreConflict> FindConflicts(IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        var conflicts = new List<RestoreConflict>();
        long checkedFiles = 0;
        foreach (var file in Files())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destination = Destination(file);
            if (File.Exists(destination))
            {
                // Newer here than in the archive: probably changed since the backup – worth a second thought.
                var newer = file.Modified != DateTimeOffset.MinValue && File.GetLastWriteTimeUtc(destination) > file.Modified.UtcDateTime.AddSeconds(2);
                conflicts.Add(new RestoreConflict(destination, newer));
            }
            if (++checkedFiles % 500 == 0)
                progress?.Report(checkedFiles);
        }
        progress?.Report(checkedFiles);
        return conflicts;
    }

    private static List<string> Paths(IReadOnlyList<ArchiveNode> items) => [.. items.Select(item => item.BorgPath)];

    /// <summary>The deepest folder holding all items (their parents).</summary>
    private static ArchiveNode CommonParent(IReadOnlyList<ArchiveNode> items)
    {
        var common = Chain(items[0].Parent!);
        foreach (var item in items.Skip(1))
        {
            var chain = Chain(item.Parent!);
            var length = 0;
            while (length < common.Count && length < chain.Count && common[length] == chain[length])
                length++;
            common = common[..length];
        }
        return common[^1];
    }

    /// <summary>From the root down to <paramref name="node"/>.</summary>
    private static List<ArchiveNode> Chain(ArchiveNode node)
    {
        var chain = new List<ArchiveNode>();
        for (var current = node; current is not null; current = current.Parent)
            chain.Add(current);
        chain.Reverse();
        return chain;
    }

    private static int Segments(string borgPath) => borgPath.Length == 0 ? 0 : borgPath.Split('/').Length;

    /// <summary>The drive ("C:") an entry is on; <c>null</c> if it isn't on one.</summary>
    private static ArchiveNode? Drive(ArchiveNode node)
    {
        var top = Chain(node)[1];
        return ArchiveTree.IsDrive(top.Name) ? top : null;
    }

    private static bool IsBelow(ArchiveNode node, ArchiveNode ancestor)
    {
        for (var parent = node.Parent; parent is not null; parent = parent.Parent)
            if (parent == ancestor)
                return true;
        return false;
    }
}
