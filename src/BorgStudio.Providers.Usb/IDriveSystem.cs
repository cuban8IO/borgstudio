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
}
