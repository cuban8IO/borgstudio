using System.Text.RegularExpressions;

namespace BorgStudio.Core.Backups;

/// <summary>Turns <see cref="BackupExclusions"/> into borg's exclude options (see <c>borg help patterns</c>).</summary>
public static class BackupPatterns
{
    // Shell-style patterns: "**/" matches any number of folders, a matching folder is skipped with all its contents.
    public static readonly IReadOnlyList<string> CachesAndTemp =
    [
        "sh:**/AppData/Local/Temp",
        "sh:**/AppData/Local/Microsoft/Windows/INetCache",
        "sh:**/AppData/Local/CrashDumps",
        "sh:**/.cache",
        "sh:**/Library/Caches",
    ];

    public static readonly IReadOnlyList<string> DevelopmentFolders =
    [
        "sh:**/node_modules",
        "sh:**/bin/Debug",
        "sh:**/bin/Release",
        "sh:**/obj",
        "sh:**/.venv",
        "sh:**/__pycache__",
    ];

    public static readonly IReadOnlyList<string> RecycleBin =
    [
        "sh:**/$RECYCLE.BIN",
        "sh:**/.Trash",
        "sh:**/.Trash-*",
        "sh:**/.local/share/Trash",
    ];

    /// <summary>
    /// The exclude options for borg create. <paramref name="borgPath"/> turns a folder on this computer into the path
    /// borg sees (inside WSL: "/mnt/c/…"); folders it cannot translate are left out.
    /// </summary>
    public static List<string> Arguments(BackupExclusions exclusions, Func<string, string?> borgPath)
    {
        var arguments = new List<string>();
        if (exclusions.CacheDirTag)
            arguments.Add("--exclude-caches");
        foreach (var pattern in Patterns(exclusions, borgPath))
            arguments.AddRange(["--exclude", pattern]);
        return arguments;
    }

    /// <summary>All exclude patterns, e.g. for a preview in the job editor.</summary>
    public static List<string> Patterns(BackupExclusions exclusions, Func<string, string?> borgPath)
    {
        var patterns = new List<string>();
        if (exclusions.CachesAndTemp)
            patterns.AddRange(CachesAndTemp);
        if (exclusions.DevelopmentFolders)
            patterns.AddRange(DevelopmentFolders);
        if (exclusions.RecycleBin)
            patterns.AddRange(RecycleBin);

        foreach (var extension in exclusions.Extensions.Select(NormalizeExtension).Where(extension => extension.Length > 1).Distinct())
            patterns.Add($"re:(?i){Regex.Escape(extension)}$");

        foreach (var folder in exclusions.Folders)
            if (borgPath(folder) is { } path)
                patterns.Add("pp:" + path.TrimEnd('/'));

        patterns.AddRange(exclusions.Patterns.Select(pattern => pattern.Trim()).Where(pattern => pattern.Length > 0));
        return patterns;
    }

    /// <summary>"iso", "*.iso" and ".ISO" all become ".iso".</summary>
    public static string NormalizeExtension(string extension)
    {
        var trimmed = extension.Trim().TrimStart('*').ToLowerInvariant();
        return trimmed.Length == 0 ? "" : trimmed.StartsWith('.') ? trimmed : "." + trimmed;
    }

    /// <summary>"iso, .tmp; *.log" → [".iso", ".tmp", ".log"].</summary>
    public static List<string> ParseExtensions(string text) =>
        [.. text.Split([',', ';', ' ', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(NormalizeExtension).Where(extension => extension.Length > 1).Distinct()];
}

/// <summary>Size of what a job would back up, for estimating the remaining time of its first run.</summary>
public sealed record SourceSize(long Bytes, long Files);

/// <summary>
/// Walks the sources the way the job's exclusions would (presets, extensions, folders, CACHEDIR.TAG – not the free
/// patterns), summing up file sizes. Only an estimate: unreadable folders are skipped.
/// </summary>
public static class SourceScanner
{
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static SourceSize Scan(IEnumerable<string> sources, BackupExclusions exclusions, CancellationToken cancellationToken = default)
    {
        var extensions = exclusions.Extensions.Select(BackupPatterns.NormalizeExtension).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var folders = exclusions.Folders.Select(folder => Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar)).ToList();
        long bytes = 0, files = 0;

        var pending = new Stack<string>(sources.Where(Directory.Exists));
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var folder = pending.Pop();
            if (IsExcludedFolder(folder, exclusions, folders))
                continue;

            try
            {
                var directory = new DirectoryInfo(folder);
                foreach (var entry in directory.EnumerateFileSystemInfos())
                {
                    if (entry is DirectoryInfo sub)
                    {
                        // Junctions and symlinks: borg stores the link, not what it points to.
                        if (!sub.Attributes.HasFlag(FileAttributes.ReparsePoint))
                            pending.Push(sub.FullName);
                    }
                    else if (entry is FileInfo file && !extensions.Contains(file.Extension))
                    {
                        bytes += file.Length;
                        files++;
                    }
                }
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
            {
                // Not readable: borg will report it as a warning.
            }
        }

        return new SourceSize(bytes, files);
    }

    /// <summary>
    /// Whether <paramref name="source"/> itself lies in something the exclusions leave out (e.g. a source under
    /// AppData\Local\Temp with "caches and temp" ticked) – then nothing of it would be backed up.
    /// </summary>
    public static bool IsExcluded(string source, BackupExclusions exclusions)
    {
        var folders = exclusions.Folders.Select(folder => Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar)).ToList();
        for (var path = Path.GetFullPath(source); !string.IsNullOrEmpty(path); path = Path.GetDirectoryName(path))
        {
            if (IsExcludedFolder(path, exclusions with { CacheDirTag = false }, folders))
                return true;
        }
        return false;
    }

    private static bool IsExcludedFolder(string folder, BackupExclusions exclusions, List<string> excludedFolders)
    {
        var full = folder.TrimEnd(Path.DirectorySeparatorChar);
        if (excludedFolders.Any(excluded => full.Equals(excluded, PathComparison) || full.StartsWith(excluded + Path.DirectorySeparatorChar, PathComparison)))
            return true;

        var name = Path.GetFileName(full);
        var parent = Path.GetFileName(Path.GetDirectoryName(full) ?? "");
        if (exclusions.CacheDirTag && File.Exists(Path.Combine(full, "CACHEDIR.TAG")))
            return true;
        if (exclusions.CachesAndTemp && (name is ".cache" or "INetCache" or "CrashDumps" || (name == "Temp" && parent == "Local") || (name == "Caches" && parent == "Library")))
            return true;
        if (exclusions.DevelopmentFolders && (name is "node_modules" or "obj" or ".venv" or "__pycache__" || (name is "Debug" or "Release" && parent == "bin")))
            return true;
        return exclusions.RecycleBin && (name.Equals("$RECYCLE.BIN", StringComparison.OrdinalIgnoreCase) || name.StartsWith(".Trash", StringComparison.Ordinal)
            || (name == "Trash" && parent == "share"));
    }
}
