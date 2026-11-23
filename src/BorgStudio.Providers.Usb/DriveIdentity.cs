namespace BorgStudio.Providers.Usb;

/// <summary>
/// Tells a drive apart from all others, wherever it is attached: by its file system's UUID or serial number
/// ("volume:8A3F-12C4") or – for a drive without one – by a small file BorgStudio leaves on it ("file:&lt;id&gt;").
/// </summary>
public static class DriveIdentity
{
    /// <summary>The file in the drive's root folder holding BorgStudio's own id for it.</summary>
    public const string MarkerFile = ".borgstudio-id";

    private const string VolumePrefix = "volume:";
    private const string FilePrefix = "file:";

    /// <summary>All ids the drive at <paramref name="root"/> answers to (its UUID, its marker file); none if it has neither.</summary>
    public static async Task<IReadOnlyList<string>> IdsAsync(IDriveSystem drives, string root, CancellationToken cancellationToken = default)
    {
        var ids = new List<string>();
        if (await drives.VolumeIdAsync(root, cancellationToken) is { Length: > 0 } volume)
            ids.Add(VolumePrefix + volume);
        if (ReadMarker(root) is { } marker)
            ids.Add(FilePrefix + marker);
        return ids;
    }

    /// <summary>
    /// The id to remember the drive by: its UUID or serial number; for a drive without one, the id in its marker file –
    /// written now if there is none yet.
    /// </summary>
    /// <exception cref="IOException">The marker file could not be written (e.g. a write-protected card).</exception>
    /// <exception cref="UnauthorizedAccessException" />
    public static async Task<string> EnsureAsync(IDriveSystem drives, string root, CancellationToken cancellationToken = default)
    {
        if (await drives.VolumeIdAsync(root, cancellationToken) is { Length: > 0 } volume)
            return VolumePrefix + volume;
        if (ReadMarker(root) is { } existing)
            return FilePrefix + existing;
        var id = Guid.NewGuid().ToString("N");
        var file = Path.Combine(root, MarkerFile);
        File.WriteAllText(file, id + Environment.NewLine);
        if (OperatingSystem.IsWindows())
            File.SetAttributes(file, File.GetAttributes(file) | FileAttributes.Hidden);
        return FilePrefix + id;
    }

    /// <summary>Whether <paramref name="id"/> needs the marker file (the drive has no UUID of its own).</summary>
    public static bool IsMarker(string id) => id.StartsWith(FilePrefix, StringComparison.Ordinal);

    private static string? ReadMarker(string root)
    {
        try
        {
            var file = Path.Combine(root, MarkerFile);
            if (!File.Exists(file))
                return null;
            var id = File.ReadAllText(file).Trim();
            return id.Length is > 0 and <= 64 && id.All(char.IsAsciiLetterOrDigit) ? id : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
