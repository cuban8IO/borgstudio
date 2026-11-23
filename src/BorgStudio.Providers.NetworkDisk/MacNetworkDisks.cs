using BorgStudio.Plugins;
using BorgStudio.Providers.NetworkDisk.Resources;

namespace BorgStudio.Providers.NetworkDisk;

/// <summary>macOS has no built-in iSCSI or NVMe/TCP initiator: network disks are not supported there.</summary>
public sealed class MacNetworkDisks : INetworkDiskSystem
{
    public string? Unsupported(NetworkDiskProtocol protocol) => Strings.UnsupportedMac;

    public Task SetUpAsync(NetworkDiskTarget target, string id, string? chapSecret, CancellationToken cancellationToken) =>
        throw new RepositoryUnavailableException(Strings.UnsupportedMac);

    public Task ConnectAsync(NetworkDiskTarget target, string id, CancellationToken cancellationToken) =>
        throw new RepositoryUnavailableException(Strings.UnsupportedMac);

    public Task<string?> DisconnectAsync(NetworkDiskTarget target, string id, CancellationToken cancellationToken) =>
        Task.FromResult<string?>(null);
}
