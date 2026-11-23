using BorgStudio.Core.Repositories;

namespace BorgStudio.Core.Backups;

/// <summary>
/// A removable drive (USB stick, external disk, memory card) a job backs up – this very drive, found by its identity
/// wherever it is attached. borg works from the drive's root folder, so the archives hold paths like "DCIM/100CANON/…"
/// whatever letter or mount point the drive had.
/// </summary>
public sealed record SourceDrive
{
    /// <summary>
    /// The drive's identity: "volume:&lt;UUID or serial number&gt;", or "file:&lt;id&gt;" for one without (BorgStudio's own id in a
    /// file on the drive).
    /// </summary>
    public required string Id { get; init; }

    /// <summary>Its name when it was chosen ("KAMERA"), to show it.</summary>
    public string Label { get; init; } = "";

    /// <summary>Folders on the drive to back up, relative to its root ("DCIM", "Projects/2026"); none: the whole drive.</summary>
    public List<string> Folders { get; init; } = [];

    /// <summary>Eject the drive after automatic runs (scheduled, on attaching), if nothing else uses it.</summary>
    public bool Eject { get; init; }

    /// <summary>Ask "Back up now?" when the drive is attached (BorgStudio in the background).</summary>
    public PlugInBackup PlugIn { get; init; } = new();

    public bool IsWholeDrive => Folders.Count == 0;

    /// <summary>What borg gets as sources, relative to the drive's root: the folders, or "." for all of it.</summary>
    public IReadOnlyList<string> BorgSources => IsWholeDrive ? ["."] : [.. Folders.Select(RelativePath)];

    /// <summary>"DCIM\Thumbs\" → "DCIM/Thumbs": a path below the drive's root as borg sees it.</summary>
    public static string RelativePath(string path) => path.Replace('\\', '/').Trim('/');

    /// <summary>
    /// <paramref name="path"/> (a folder on this computer) relative to the drive's <paramref name="root"/>, e.g.
    /// "E:\DCIM\Thumbs" → "DCIM\Thumbs"; <c>null</c> if it is not on that drive (or is the root itself).
    /// </summary>
    public static string? Below(string root, string path)
    {
        var comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!full.StartsWith(fullRoot, comparison) || full.Length <= fullRoot.Length)
            return null;
        var rest = full[fullRoot.Length..];
        // "E:\" trimmed stays "E:\"; "/media/me/KAMERA" needs the separator after it.
        if (!fullRoot.EndsWith(Path.DirectorySeparatorChar) && rest[0] != Path.DirectorySeparatorChar)
            return null;
        return rest.TrimStart(Path.DirectorySeparatorChar);
    }

    /// <summary>
    /// What borg leaves out on any drive besides the job's exclusions: what the systems keep there for themselves
    /// (unreadable for borg anyway) and BorgStudio's own id file.
    /// </summary>
    public static readonly IReadOnlyList<string> SystemPatterns =
    [
        "pp:System Volume Information",
        "pp:.Spotlight-V100",
        "pp:.fseventsd",
        "pp:.Trashes",
        "pp:.TemporaryItems",
        "pp:.borgstudio-id",
    ];
}

/// <summary>Finds a job's drive (implemented by the app, which knows the drives of this computer).</summary>
public interface ISourceDriveLocator
{
    /// <summary>The root folder the drive is attached at now ("F:\", "/media/me/KAMERA"); <c>null</c> if it is not attached.</summary>
    Task<string?> FindAsync(SourceDrive drive, CancellationToken cancellationToken = default);
}
