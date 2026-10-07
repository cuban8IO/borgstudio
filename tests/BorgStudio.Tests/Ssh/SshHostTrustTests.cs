using BorgStudio.Core.Ssh;
using BorgStudio.Tests.TestSupport;

namespace BorgStudio.Tests.Ssh;

public sealed class SshHostTrustTests : IDisposable
{
    private readonly TemporaryDirectory _directory = new();
    private readonly FakeSsh _ssh = new();
    private readonly KnownHostsFile _knownHosts;
    private readonly SshHostTrust _trust;

    public SshHostTrustTests()
    {
        _knownHosts = new KnownHostsFile(_directory.Combine("known_hosts"));
        _trust = new SshHostTrust(_ssh, _knownHosts);
    }

    public void Dispose() => _directory.Dispose();

    [Fact]
    public async Task A_new_host_key_is_stored_only_after_confirmation()
    {
        SshHostKey? shown = null;

        var (rejected, _) = await _trust.EnsureTrustedAsync("nas", 22, key => { shown = key; return Task.FromResult(false); });
        Assert.Equal(HostTrust.Rejected, rejected);
        Assert.Same(FakeSsh.ServerKey, shown);
        Assert.False(_trust.IsKnown("nas", 22));

        var (accepted, key) = await _trust.EnsureTrustedAsync("nas", 22, _ => Task.FromResult(true));
        Assert.Equal(HostTrust.Trusted, accepted);
        Assert.Same(FakeSsh.ServerKey, key);
        Assert.True(_trust.IsKnown("nas", 22));
    }

    [Fact]
    public async Task A_known_key_is_trusted_without_asking()
    {
        _knownHosts.Add("nas", 22, FakeSsh.ServerKey);

        var (trust, _) = await _trust.EnsureTrustedAsync("nas", 22, _ => throw new InvalidOperationException("must not ask"));

        Assert.Equal(HostTrust.Trusted, trust);
    }

    [Fact]
    public async Task A_changed_key_is_refused_without_asking()
    {
        _knownHosts.Add("nas", 22, new SshHostKey(FakeSsh.HostKeyBlob("ssh-ed25519", 99)));

        var (trust, _) = await _trust.EnsureTrustedAsync("nas", 22, _ => throw new InvalidOperationException("must not ask"));

        Assert.Equal(HostTrust.Changed, trust);
        Assert.Single(_knownHosts.Find("nas", 22));
    }
}
