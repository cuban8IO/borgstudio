namespace BorgStudio.Providers.NetworkDisk;

/// <summary>Logging in to network disks on this computer; abstracted so the provider can be tested without one.</summary>
public interface INetworkDiskSystem
{
    /// <summary>Why this computer cannot use <paramref name="protocol"/> (a message for the user), or <c>null</c> if it can.</summary>
    string? Unsupported(NetworkDiskProtocol protocol);

    /// <summary>
    /// Sets up, with administrator rights asked for once, what logging in and out of the target needs later without them.
    /// Throws <see cref="Plugins.RepositoryUnavailableException"/> with a message for the user when it cannot.
    /// </summary>
    /// <param name="id">Names what is set up for this repository (stable).</param>
    Task SetUpAsync(NetworkDiskTarget target, string id, string? chapSecret, CancellationToken cancellationToken);

    /// <summary>Logs in; the disk's volumes appear shortly after. Throws <see cref="Plugins.RepositoryUnavailableException"/>.</summary>
    Task ConnectAsync(NetworkDiskTarget target, string id, CancellationToken cancellationToken);

    /// <summary>Logs out (unmounting what was mounted for it first); a message for the log if that did not work.</summary>
    Task<string?> DisconnectAsync(NetworkDiskTarget target, string id, CancellationToken cancellationToken);
}
