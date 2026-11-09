using System.Globalization;
using System.Text.Json;

namespace BorgStudio.Core.Borg;

/// <summary>What an entry in an archive is.</summary>
public enum ArchiveEntryKind
{
    File,
    Directory,
    Symlink,

    /// <summary>Devices, FIFOs, sockets.</summary>
    Other,
}

/// <summary>An entry of <c>borg list --json-lines</c>.</summary>
/// <param name="Path">As borg stores it, without leading slash, e.g. "mnt/c/Users/me/a.txt" or "home/me/a.txt".</param>
/// <param name="Modified">Local time, as borg 1.x reports it.</param>
public sealed record BorgItem(string Path, ArchiveEntryKind Kind, long Size, DateTimeOffset Modified, string? LinkTarget = null)
{
    /// <summary>One line of <c>borg list --json-lines</c>; <c>null</c> if it is no entry.</summary>
    public static BorgItem? Parse(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return null;
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (!root.TryGetProperty("path", out var path) || path.GetString() is not { Length: > 0 } text)
                return null;
            var kind = root.TryGetProperty("type", out var type) ? type.GetString() switch
            {
                "-" => ArchiveEntryKind.File,
                "d" => ArchiveEntryKind.Directory,
                "l" => ArchiveEntryKind.Symlink,
                _ => ArchiveEntryKind.Other,
            } : ArchiveEntryKind.File;
            var size = root.TryGetProperty("size", out var bytes) && bytes.ValueKind == JsonValueKind.Number ? bytes.GetInt64() : 0;
            var modified = root.TryGetProperty("mtime", out var mtime)
                && DateTimeOffset.TryParse(mtime.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed)
                ? parsed
                : DateTimeOffset.MinValue;
            var link = root.TryGetProperty("linktarget", out var target) && target.GetString() is { Length: > 0 } linked ? linked : null;
            return new BorgItem(text.TrimStart('/'), kind, size, modified, link);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            return null;
        }
    }
}

/// <summary>A file or folder in an <see cref="ArchiveTree"/>.</summary>
public sealed class ArchiveNode
{
    private List<ArchiveNode>? _children;

    internal ArchiveNode(string name, ArchiveEntryKind kind, ArchiveNode? parent)
    {
        Name = name;
        Kind = kind;
        Parent = parent;
    }

    /// <summary>Shown name: a path segment, or "C:" for a Windows drive (borg in WSL stores it as "mnt/c").</summary>
    public string Name { get; }

    public ArchiveEntryKind Kind { get; internal set; }

    public ArchiveNode? Parent { get; }

    /// <summary>For files: their size; for folders: the size of everything in them.</summary>
    public long Size { get; internal set; }

    /// <summary>For folders: how many files are in them (all levels).</summary>
    public long FileCount { get; internal set; }

    public DateTimeOffset Modified { get; internal set; } = DateTimeOffset.MinValue;

    public string? LinkTarget { get; internal set; }

    /// <summary>A folder borg didn't store itself (e.g. "C:" or "Users" above the backed-up folder).</summary>
    public bool IsImplicit { get; internal set; }

    public bool IsDirectory => Kind == ArchiveEntryKind.Directory;

    public IReadOnlyList<ArchiveNode> Children => _children ?? (IReadOnlyList<ArchiveNode>)[];

    public bool HasSubdirectories => _children?.Any(child => child.IsDirectory) == true;

    internal void Add(ArchiveNode child) => (_children ??= []).Add(child);

    internal void Sort() =>
        _children?.Sort((a, b) => a.IsDirectory != b.IsDirectory ? (a.IsDirectory ? -1 : 1) : StringComparer.CurrentCultureIgnoreCase.Compare(a.Name, b.Name));

    /// <summary>The path as borg knows it (to restore it), e.g. "mnt/c/Users/me".</summary>
    public string BorgPath => Parent is null ? "" : Parent.Parent is null ? ArchiveTree.ToBorgSegment(Name) : $"{Parent.BorgPath}/{Name}";

    /// <summary>The path as the user knows it, e.g. "C:\Users\me" or "/home/me".</summary>
    public string DisplayPath
    {
        get
        {
            if (Parent is null)
                return "";
            var segments = new List<string>();
            for (var node = this; node.Parent is not null; node = node.Parent)
                segments.Add(node.Name);
            segments.Reverse();
            return ArchiveTree.IsDrive(segments[0])
                ? segments.Count == 1 ? segments[0] + "\\" : string.Join('\\', segments)
                : "/" + string.Join('/', segments);
        }
    }

    public override string ToString() => DisplayPath;
}

/// <summary>
/// The contents of an archive as a folder tree, built while <c>borg list</c> reads them (millions of entries: only the
/// names are kept per node, not full paths). Windows drives backed up through WSL ("mnt/c/…") appear as "C:".
/// </summary>
public sealed class ArchiveTree
{
    // Folders by their borg path while building; dropped afterwards.
    private Dictionary<string, ArchiveNode>? _folders = new(StringComparer.Ordinal);

    public ArchiveNode Root { get; } = new("", ArchiveEntryKind.Directory, null) { IsImplicit = true };

    /// <summary>Entries borg listed.</summary>
    public long EntryCount { get; private set; }

    // borg lists folder by folder: the parent of the last entry is usually the next one's too.
    private string? _lastParentKey;
    private ArchiveNode? _lastParent;

    /// <summary>Adds one entry (while building).</summary>
    public void Add(BorgItem item)
    {
        var folders = _folders ?? throw new InvalidOperationException("The tree is complete.");
        var key = Normalize(item.Path);
        if (key.Length == 0)
            return;

        var slash = key.LastIndexOf('/');
        var parentKey = slash < 0 ? "" : key[..slash];
        var name = key[(slash + 1)..];
        ArchiveNode parent;
        if (parentKey == _lastParentKey && _lastParent is not null)
            parent = _lastParent;
        else
        {
            parent = Folder(folders, parentKey);
            (_lastParentKey, _lastParent) = (parentKey, parent);
        }

        if (item.Kind == ArchiveEntryKind.Directory && folders.TryGetValue(key, out var existing))
        {
            // Came implicitly with an earlier entry inside it.
            existing.IsImplicit = false;
            existing.Modified = item.Modified;
            EntryCount++;
            return;
        }

        var node = new ArchiveNode(name, item.Kind, parent)
        {
            Size = item.Kind == ArchiveEntryKind.Directory ? 0 : item.Size,
            Modified = item.Modified,
            LinkTarget = item.LinkTarget,
        };
        parent.Add(node);
        if (node.IsDirectory)
            folders[key] = node;
        EntryCount++;
    }

    /// <summary>Done adding: sorts (folders first, then by name) and sums up folder sizes and file counts.</summary>
    public ArchiveTree Complete()
    {
        _folders = null;
        Summarize(Root);
        return this;
    }

    /// <summary>All entries, depth first (e.g. to search or to save the tree).</summary>
    public IEnumerable<ArchiveNode> All()
    {
        var stack = new Stack<ArchiveNode>();
        stack.Push(Root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (node != Root)
                yield return node;
            for (var index = node.Children.Count - 1; index >= 0; index--)
                stack.Push(node.Children[index]);
        }
    }

    /// <summary>The folder at a borg path ("" = the root); <c>null</c> if there is none.</summary>
    public ArchiveNode? Find(string borgPath)
    {
        var node = Root;
        foreach (var segment in Segments(borgPath))
        {
            node = node.Children.FirstOrDefault(child => child.Name == segment);
            if (node is null)
                return null;
        }
        return node;
    }

    /// <summary>A borg path as the user knows it: "mnt/c/Users/me" → "C:\Users\me", "home/me" → "/home/me".</summary>
    public static string DisplayPathOf(string borgPath)
    {
        var path = Normalize(borgPath);
        return path.Length >= 2 && IsDrive(path[..2]) ? path.Replace('/', '\\') + (path.Length == 2 ? "\\" : "") : "/" + path;
    }

    internal static bool IsDrive(string segment) => segment.Length == 2 && segment[1] == ':' && char.IsAsciiLetter(segment[0]);

    /// <summary>"C:" → "mnt/c" (the first segment of a borg path).</summary>
    internal static string ToBorgSegment(string name) => IsDrive(name) ? $"mnt/{char.ToLowerInvariant(name[0])}" : name;

    /// <summary>"mnt/c/Users/me" → ["C:", "Users", "me"].</summary>
    private static string[] Segments(string borgPath) => Normalize(borgPath).Split('/', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>"mnt/c/Users/me/" → "C:/Users/me": the drive as shown, no empty segments.</summary>
    private static string Normalize(string borgPath)
    {
        var path = borgPath.Trim('/');
        if (path.Contains("//", StringComparison.Ordinal))
            path = string.Join('/', path.Split('/', StringSplitOptions.RemoveEmptyEntries));
        if (path.Length >= 5 && path.StartsWith("mnt/", StringComparison.Ordinal) && char.IsAsciiLetter(path[4])
            && (path.Length == 5 || path[5] == '/'))
            path = $"{char.ToUpperInvariant(path[4])}:{path[5..]}";
        return path;
    }

    /// <summary>The folder with this (normalized) key, created with the ones above it if borg didn't list them (yet).</summary>
    private ArchiveNode Folder(Dictionary<string, ArchiveNode> folders, string key)
    {
        if (key.Length == 0)
            return Root;
        if (folders.TryGetValue(key, out var folder))
            return folder;

        var slash = key.LastIndexOf('/');
        var parent = Folder(folders, slash < 0 ? "" : key[..slash]);
        folder = new ArchiveNode(key[(slash + 1)..], ArchiveEntryKind.Directory, parent) { IsImplicit = true };
        parent.Add(folder);
        folders[key] = folder;
        return folder;
    }

    private static void Summarize(ArchiveNode folder)
    {
        // Iterative post-order: archives can be deep.
        var order = new List<ArchiveNode>();
        var stack = new Stack<ArchiveNode>();
        stack.Push(folder);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            order.Add(node);
            foreach (var child in node.Children)
                if (child.IsDirectory)
                    stack.Push(child);
        }
        for (var index = order.Count - 1; index >= 0; index--)
        {
            var node = order[index];
            node.Sort();
            long size = 0, files = 0;
            foreach (var child in node.Children)
            {
                size += child.Size;
                files += child.IsDirectory ? child.FileCount : 1;
            }
            node.Size = size;
            node.FileCount = files;
        }
    }
}
