using BorgStudio.App.ViewModels;
using BorgStudio.Core.Backups;
using BorgStudio.Providers.Usb;

namespace BorgStudio.App.Services;

/// <summary>A removable drive attached now, as the job editor offers it.</summary>
/// <param name="Id">The id it would be remembered by; <c>null</c> if it has no UUID and no id file yet (one is written when chosen).</param>
public sealed record AttachedDrive(string Root, string Label, long? Size, string? Id);

/// <summary>
/// Finds the removable drives jobs back up (by the id they were chosen with, wherever they are attached), lists the
/// attached ones for the job editor and ejects them after automatic runs – unless a run still holds the drive.
/// </summary>
public sealed class SourceDriveLocator(IDriveSystem drives) : ISourceDriveLocator
{
    // Drives a run holds (e.g. all jobs of a drive just attached): ejected only when the last one lets go.
    private static readonly Dictionary<string, int> Holds = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The drives of this computer, through the USB drive plugin; <c>null</c> without it.</summary>
    public static SourceDriveLocator? For(AppServices services) =>
        services.Plugins.RepositoryProviders.OfType<UsbRepositoryProvider>().FirstOrDefault()?.Drives is { } drives
            ? new SourceDriveLocator(drives)
            : null;

    public IDriveSystem Drives => drives;

    public async Task<string?> FindAsync(SourceDrive drive, CancellationToken cancellationToken = default)
    {
        foreach (var root in Roots())
            if ((await DriveIdentity.IdsAsync(drives, root, cancellationToken)).Contains(drive.Id))
                return root;
        return null;
    }

    /// <summary>The ids of the drive at <paramref name="root"/> (see <see cref="DriveIdentity.IdsAsync"/>).</summary>
    public Task<IReadOnlyList<string>> IdsAsync(string root, CancellationToken cancellationToken = default) =>
        DriveIdentity.IdsAsync(drives, root, cancellationToken);

    /// <summary>The removable and external drives attached now (no network drives, no internal disks).</summary>
    public async Task<IReadOnlyList<AttachedDrive>> AttachedAsync(CancellationToken cancellationToken = default)
    {
        var attached = new List<AttachedDrive>();
        foreach (var root in Roots())
        {
            if (!await drives.IsExternalAsync(root, cancellationToken))
                continue;
            var ids = await DriveIdentity.IdsAsync(drives, root, cancellationToken);
            attached.Add(new AttachedDrive(root, drives.Label(root), drives.Size(root), ids.FirstOrDefault()));
        }
        return attached;
    }

    /// <summary>The id to remember the drive at <paramref name="root"/> by – writing its id file if it has no UUID.</summary>
    /// <exception cref="IOException" />
    /// <exception cref="UnauthorizedAccessException" />
    public Task<string> EnsureIdAsync(string root, CancellationToken cancellationToken = default) =>
        DriveIdentity.EnsureAsync(drives, root, cancellationToken);

    /// <summary>Keeps the drive at <paramref name="root"/> from being ejected until the returned hold is disposed.</summary>
    public static IDisposable Hold(string root)
    {
        lock (Holds)
            Holds[root] = Holds.GetValueOrDefault(root) + 1;
        return new Release(root);
    }

    /// <summary>Ejects the drive at <paramref name="root"/> unless a run holds it; a message why it did not work, or <c>null</c>.</summary>
    public async Task<string?> EjectAsync(string root, CancellationToken cancellationToken = default)
    {
        lock (Holds)
        {
            if (Holds.GetValueOrDefault(root) > 0)
                return null;
        }
        return await drives.EjectAsync(root, cancellationToken);
    }

    private IEnumerable<string> Roots()
    {
        try
        {
            return [.. drives.Roots().Where(root => !root.IsNetwork).Select(root => root.Path)];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private sealed class Release(string root) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            lock (Holds)
            {
                if (Holds.GetValueOrDefault(root) <= 1)
                    Holds.Remove(root);
                else
                    Holds[root]--;
            }
        }
    }
}
