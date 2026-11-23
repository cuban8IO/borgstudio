using BorgStudio.Core.Plugins;
using BorgStudio.Plugins;
using BorgStudio.Providers.BorgBase;
using BorgStudio.Providers.Hetzner;
using BorgStudio.Providers.Local;
using BorgStudio.Providers.NetworkDisk;
using BorgStudio.Providers.RsyncNet;
using BorgStudio.Providers.Sftp;
using BorgStudio.Providers.Ssh;
using BorgStudio.Providers.Usb;
using BorgStudio.Tests.TestSupport;

namespace BorgStudio.Tests.Providers;

/// <summary>Which built-in providers are marked as untested when the user picks where a repository is.</summary>
public sealed class UntestedProvidersTests
{
    [Fact]
    public void Only_rsync_net_and_the_network_disk_are_marked_as_untested()
    {
        using var directory = new TemporaryDirectory();
        var catalog = PluginCatalog.Load(
            [new LocalProviderPlugin(), new UsbProviderPlugin(), new NetworkDiskProviderPlugin(), new SshProviderPlugin(), new SftpProviderPlugin(),
             new HetznerProviderPlugin(), new RsyncNetProviderPlugin(), new BorgBaseProviderPlugin()],
            directory.Combine("plugins"));

        var untested = catalog.RepositoryProviders.Where(provider => provider.IsUntested).Select(provider => provider.Id).Order();

        Assert.Equal(["network-disk", "rsync-net"], untested);
    }

    [Fact]
    public void Providers_count_as_tested_unless_they_say_otherwise()
    {
        IRepositoryProvider provider = new MinimalProvider();

        Assert.False(provider.IsUntested);
    }

    private sealed class MinimalProvider : IRepositoryProvider
    {
        public string Id => "stub";
        public string DisplayName => "Stub";
        public string Description => "";
        public IReadOnlyList<ProviderField> Fields => [];
        public IReadOnlyList<string> Validate(IReadOnlyDictionary<string, string> values) => [];
        public RepositoryLocation GetLocation(IReadOnlyDictionary<string, string> values) => throw new NotSupportedException();
    }
}
