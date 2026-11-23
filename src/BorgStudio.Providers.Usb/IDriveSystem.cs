namespace BorgStudio.Providers.Usb;

/// <summary>A drive (Windows) or mount point (Linux, macOS) to search for repositories.</summary>
/// <param name="Path">Its root folder, e.g. <c>E:\</c> or <c>/media/me/Backup</c>.</param>
/// <param name="IsNetwork">A network drive or share.</param>
public sealed record DriveRoot(string Path, bool IsNetwork);

/// <summary>The drives of this computer; abstracted so the search and ejecting can be tested without real drives.</summary>
public interface IDriveSystem
{
    /// <summary>All attached drives and mount points worth searching – network drives included.</summary>
    IReadOnlyList<DriveRoot> Roots();

    /// <summary>Whether <paramref name="path"/> is on a removable or external drive (USB, FireWire, SD card).</summary>
    Task<bool> IsExternalAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>Ejects the drive holding <paramref name="path"/> safely; a message why not, or <c>null</c> when it is ejected.</summary>
    Task<string?> EjectAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// The identity of the file system on the drive at <paramref name="root"/>: its UUID or serial number, as the system
    /// reports it (e.g. "8A3F-12C4"); <c>null</c> if it has none that can be read.
    /// </summary>
    Task<string?> VolumeIdAsync(string root, CancellationToken cancellationToken = default);

    /// <summary>The drive's name ("KAMERA"); empty if it has none.</summary>
    string Label(string root);

    /// <summary>The drive's size in bytes; <c>null</c> if unknown.</summary>
    long? Size(string root);
}
