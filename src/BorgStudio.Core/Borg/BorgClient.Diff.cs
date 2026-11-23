using System.Text.Json;
using BorgStudio.Plugins;

namespace BorgStudio.Core.Borg;

/// <summary>What changed about a path between two archives.</summary>
public enum DiffKind
{
    /// <summary>Only in the newer archive.</summary>
    Added,

    /// <summary>Only in the older archive.</summary>
    Removed,

    /// <summary>Its content changed (for links: their target).</summary>
    Modified,

    /// <summary>Only its metadata changed (permissions, owner, times).</summary>
    Metadata,
}

/// <summary>A path that differs between two archives (one line of <c>borg diff --json-lines</c>).</summary>
/// <param name="Size">The size of an added or removed file.</param>
/// <param name="Added">For a modified file: the data borg had to add (compressed chunks are not counted).</param>
/// <param name="Removed">For a modified file: the data no longer in it.</param>
/// <param name="Details">The metadata changes, e.g. "mode: -rw-r--r-- → -rw-------".</param>
public sealed record ArchiveDiffEntry(string Path, DiffKind Kind, bool IsDirectory, long? Size, long Added, long Removed,
    IReadOnlyList<string> Details)
{
    /// <summary>One line of <c>borg diff --json-lines</c>; <c>null</c> for anything else.</summary>
    public static ArchiveDiffEntry? Parse(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return null;
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (!root.TryGetProperty("path", out var pathElement) || pathElement.GetString() is not { Length: > 0 } path
                || !root.TryGetProperty("changes", out var changes) || changes.ValueKind != JsonValueKind.Array)
                return null;

            var kind = DiffKind.Metadata;
            var directory = false;
            long? size = null;
            long added = 0, removed = 0;
            var details = new List<string>();
            foreach (var change in changes.EnumerateArray())
            {
                var type = change.TryGetProperty("type", out var typeElement) ? typeElement.GetString() ?? "" : "";
                long Number(string name) => change.TryGetProperty(name, out var value) && value.TryGetInt64(out var number) ? number : 0;
                switch (type)
                {
                    case "added" or "added link":
                        kind = DiffKind.Added;
                        size = Number("size");
                        break;
                    case "removed" or "removed link":
                        kind = DiffKind.Removed;
                        size = Number("size");
                        break;
                    case "added directory":
                        kind = DiffKind.Added;
                        directory = true;
                        break;
                    case "removed directory":
                        kind = DiffKind.Removed;
                        directory = true;
                        break;
                    case "modified" or "changed link":
                        kind = DiffKind.Modified;
                        added = Number("added");
                        removed = Number("removed");
                        break;
                    default:
                        // mode, owner, ctime, mtime, …: "old_<x>" → "new_<x>".
                        var old = change.EnumerateObject().FirstOrDefault(property => property.Name.StartsWith("old_", StringComparison.Ordinal));
                        var @new = change.EnumerateObject().FirstOrDefault(property => property.Name.StartsWith("new_", StringComparison.Ordinal));
                        details.Add(old.Value.ValueKind != JsonValueKind.Undefined && @new.Value.ValueKind != JsonValueKind.Undefined
                            ? $"{type}: {old.Value} → {@new.Value}"
                            : type);
                        break;
                }
            }
            return new ArchiveDiffEntry(path.TrimStart('/'), kind, directory, size, added, removed, details);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            return null;
        }
    }
}

public sealed partial class BorgClient
{
    /// <summary>
    /// <c>borg diff --json-lines</c>: what changed from <paramref name="older"/> to <paramref name="newer"/>, path by path,
    /// as borg reads it (<paramref name="entriesRead"/> counts them, every 100 and at the end). Archives of the same
    /// repository only; borg compares their chunks, so this is fast even for large archives.
    /// </summary>
    public async Task<BorgResult<IReadOnlyList<ArchiveDiffEntry>>> DiffAsync(BorgInstallation borg, RepositoryLocation location, string older,
        string newer, string? passphrase, SshAccess? ssh = null, IProgress<int>? entriesRead = null, CancellationToken cancellationToken = default)
    {
        var entries = new List<ArchiveDiffEntry>();
        void OnLine(string line)
        {
            if (ArchiveDiffEntry.Parse(line) is not { } entry)
                return;
            entries.Add(entry);
            if (entries.Count % 100 == 0)
                entriesRead?.Report(entries.Count);
        }

        var result = await RunAsync(borg, location, ssh, ["diff"], ["--json-lines"], [newer], passphrase, CreateTimeout, cancellationToken,
            older, standardOutputLine: OnLine);
        if (!result.Succeeded)
            return BorgResult<IReadOnlyList<ArchiveDiffEntry>>.Failure(result.Error!);
        entriesRead?.Report(entries.Count);
        return BorgResult<IReadOnlyList<ArchiveDiffEntry>>.Success(entries);
    }
}
