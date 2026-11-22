namespace BorgStudio.Providers.Usb;

/// <summary>
/// Finds a borg repository by its ID: first where it was last seen, then at the same folder path on every drive, then
/// up to two folder levels deep on every drive.
/// </summary>
public static class RepositoryFinder
{
    /// <summary>How long one drive may take to answer (a sleeping network drive must not hold up the others for long).</summary>
    public static TimeSpan DriveTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Levels below a drive's root that are searched.</summary>
    public const int SearchDepth = 2;

    /// <summary>
    /// The folder of the repository; <c>null</c> if no attached drive has it. Without a known
    /// <paramref name="repositoryId"/> only <paramref name="lastPath"/> counts (any repository there).
    /// </summary>
    public static async Task<string?> FindAsync(IDriveSystem drives, string lastPath, string? repositoryId,
        CancellationToken cancellationToken = default)
    {
        if (await ProbeAsync(() => IsRepository(lastPath, repositoryId) ? lastPath : null, cancellationToken) is { } there)
            return there;
        if (string.IsNullOrEmpty(repositoryId))
            return null;

        var tails = Tails(lastPath);
        var roots = drives.Roots();
        // Every drive at the same time, the result in drive order: the drive it was on before comes first.
        var searches = roots
            .OrderBy(root => lastPath.StartsWith(root.Path, PathComparison) ? 0 : 1)
            .Select(root => ProbeAsync(() => Search(root.Path, tails, repositoryId, cancellationToken), cancellationToken))
            .ToList();
        foreach (var search in searches)
            if (await search is { } found)
                return found;
        return null;
    }

    /// <summary>Whether <paramref name="folder"/> holds a borg repository – with that ID, if one is given.</summary>
    public static bool IsRepository(string folder, string? repositoryId)
    {
        try
        {
            var config = Path.Combine(folder, "config");
            var file = new FileInfo(config);
            if (!file.Exists || file.Length > 64 * 1024)
                return false;
            var lines = File.ReadAllLines(config);
            if (!lines.Any(line => line.Trim().Equals("[repository]", StringComparison.OrdinalIgnoreCase)))
                return false;
            if (string.IsNullOrEmpty(repositoryId))
                return true;
            return lines.Select(line => line.Split('=', 2)).Any(parts => parts.Length == 2
                && parts[0].Trim().Equals("id", StringComparison.OrdinalIgnoreCase)
                && parts[1].Trim().Equals(repositoryId, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static string? Search(string root, IReadOnlyList<string> tails, string repositoryId, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(root))
            return null;
        foreach (var tail in tails)
        {
            var candidate = Path.Combine(root, tail);
            if (IsRepository(candidate, repositoryId))
                return candidate;
        }

        var level = new List<string> { root };
        for (var depth = 0; depth <= SearchDepth && level.Count > 0; depth++)
        {
            var next = new List<string>();
            foreach (var folder in level)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsRepository(folder, repositoryId))
                    return folder;
                if (depth < SearchDepth)
                    next.AddRange(SubFolders(folder));
            }
            level = next;
        }
        return null;
    }

    private static IEnumerable<string> SubFolders(string folder)
    {
        try
        {
            return new DirectoryInfo(folder)
                .EnumerateDirectories("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System })
                .Select(directory => directory.FullName)
                .ToList();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return [];
        }
    }

    /// <summary>
    /// The last parts of the path, longest first ("E:\Backups\pc\repo" → "Backups\pc\repo", "pc\repo", "repo"): the
    /// same folder on a drive that got another letter or mount point (whose path may be longer or shorter).
    /// </summary>
    internal static IReadOnlyList<string> Tails(string path)
    {
        var root = Path.GetPathRoot(path) ?? "";
        var parts = path[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        return Enumerable.Range(0, parts.Length).Select(skip => Path.Combine(parts[skip..])).ToList();
    }

    /// <summary>Runs a look at the disk in the background, so a hanging drive ends as "not found" after <see cref="DriveTimeout"/>.</summary>
    private static async Task<string?> ProbeAsync(Func<string?> probe, CancellationToken cancellationToken)
    {
        try
        {
            return await Task.Run(probe, cancellationToken).WaitAsync(DriveTimeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            return null;
        }
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
