using BorgStudio.Core.Borg;
using BorgStudio.Core.Ssh;
using BorgStudio.Plugins;
using BorgStudio.Tests.TestSupport;

namespace BorgStudio.Tests.Ssh;

/// <summary>
/// The complete SSH flow against a real OpenSSH server and a real borg (Linux CI job):
/// confirm host key, install a restricted key with the password, then run borg with that key.
/// </summary>
public sealed class SshServerIntegrationTests : IDisposable
{
    private const string Passphrase = "correct horse battery staple";

    private readonly TemporaryDirectory _directory = new();
    private readonly SshNetService _ssh = new();
    private readonly SshKeyStore _keys;
    private readonly KnownHostsFile _knownHosts;

    public SshServerIntegrationTests()
    {
        _keys = new SshKeyStore(_directory.Combine("ssh"));
        _knownHosts = new KnownHostsFile(_keys.KnownHostsFile);
    }

    public void Dispose() => _directory.Dispose();

    private static string Host => SshServerFactAttribute.Host!;
    private static int Port => SshServerFactAttribute.Port;
    private static BorgInstallation Borg => BorgFactAttribute.Borg.Value!;

    private static (SshEndpoint Endpoint, RepositoryLocation Location) Repository(string? name = null)
    {
        var path = $"borgstudio-it/{name ?? Guid.NewGuid().ToString("N")}";
        var endpoint = new SshEndpoint(Host, Port, SshServerFactAttribute.User, path);
        return (endpoint, new RepositoryLocation($"ssh://{endpoint.User}@{Host}:{Port}/./{path}") { Ssh = endpoint });
    }

    private async Task<SshHostKey> TrustServerAsync()
    {
        var (trust, key) = await new SshHostTrust(_ssh, _knownHosts).EnsureTrustedAsync(Host, Port, _ => Task.FromResult(true));
        Assert.Equal(HostTrust.Trusted, trust);
        return key;
    }

    [SshServerFact]
    public async Task Restricted_key_installed_with_the_password_works_for_borg()
    {
        var hostKey = await TrustServerAsync();
        var (endpoint, location) = Repository();
        var key = SshKeyEntry.Generate("it-key", "borgstudio-integration-test", DateTimeOffset.Now);

        await _ssh.RunWithPasswordAsync(endpoint, SshServerFactAttribute.Password, hostKey,
            AuthorizedKeys.AppendCommand(AuthorizedKeys.Line(key.PublicKey, endpoint.RepositoryPath)));

        var client = BorgClient.CreateDefault();
        var access = new SshAccess(key.PrivateKey, _knownHosts.Path);
        var created = await client.InitAsync(Borg, location, BorgEncryption.RepokeyBlake2, Passphrase, access);
        Assert.True(created.Succeeded, created.Error?.ToString());

        var info = await client.InfoAsync(Borg, location, Passphrase, access);
        Assert.True(info.Succeeded, info.Error?.ToString());
        Assert.Equal("repokey-blake2", info.Value!.EncryptionMode);

        // The key is bound to its repository: any other path on the same account is refused.
        var (_, otherLocation) = Repository();
        var other = await client.InitAsync(Borg, otherLocation, BorgEncryption.None, null, access);
        Assert.False(other.Succeeded);
    }

    [StorageBoxEmulationFact]
    public async Task Restricted_keys_written_over_SFTP_work_like_on_a_storage_box()
    {
        var hostKey = await TrustServerAsync();
        var (endpoint, location) = Repository();
        endpoint = endpoint with { KeyInstallation = SshKeyInstallation.Sftp };
        location = location with { BorgArguments = ["--remote-path=borg-1.4"], Ssh = endpoint };
        var key = SshKeyEntry.Generate("it-key", "borgstudio-integration-test", DateTimeOffset.Now);
        var otherKey = SshKeyEntry.Generate("it-other-key", "borgstudio-integration-test", DateTimeOffset.Now);

        await SshKeyInstaller.InstallAsync(_ssh, endpoint, SshServerFactAttribute.Password, hostKey, key.PublicKey,
            restrict: true, remoteBorg: "borg-1.4");
        // Appending is idempotent and keeps what is already there.
        await SshKeyInstaller.InstallAsync(_ssh, endpoint, SshServerFactAttribute.Password, hostKey, key.PublicKey,
            restrict: true, remoteBorg: "borg-1.4");
        await SshKeyInstaller.InstallAsync(_ssh, endpoint, SshServerFactAttribute.Password, hostKey, otherKey.PublicKey,
            restrict: true, remoteBorg: "borg-1.4");

        var authorizedKeys = ReadAuthorizedKeys(endpoint);
        Assert.Single(authorizedKeys, line => line.EndsWith(key.PublicKey, StringComparison.Ordinal));
        Assert.Contains($"command=\"borg-1.4 serve --restrict-to-repository {endpoint.RepositoryPath}\",restrict {otherKey.PublicKey}",
            authorizedKeys);

        var client = BorgClient.CreateDefault();
        var created = await client.InitAsync(Borg, location, BorgEncryption.RepokeyBlake2, Passphrase,
            new SshAccess(key.PrivateKey, _knownHosts.Path));
        Assert.True(created.Succeeded, created.Error?.ToString());
        var info = await client.InfoAsync(Borg, location, Passphrase, new SshAccess(otherKey.PrivateKey, _knownHosts.Path));
        Assert.True(info.Succeeded, info.Error?.ToString());
    }

    [StorageBoxEmulationFact]
    public async Task Unrestricted_keys_installed_through_the_install_command_work_for_borg()
    {
        var hostKey = await TrustServerAsync();
        var (endpoint, location) = Repository();
        endpoint = endpoint with { KeyInstallation = SshKeyInstallation.Sftp, InstallKeyCommand = "install-ssh-key" };
        var key = SshKeyEntry.Generate("it-key", "borgstudio-integration-test", DateTimeOffset.Now);

        await SshKeyInstaller.InstallAsync(_ssh, endpoint, SshServerFactAttribute.Password, hostKey, key.PublicKey,
            restrict: false, remoteBorg: "borg-1.4");

        Assert.Contains(key.PublicKey, ReadAuthorizedKeys(endpoint));
        var created = await BorgClient.CreateDefault().InitAsync(Borg, location with { Ssh = endpoint }, BorgEncryption.None,
            null, new SshAccess(key.PrivateKey, _knownHosts.Path));
        Assert.True(created.Succeeded, created.Error?.ToString());
    }

    /// <summary>The test account's authorized_keys, read with the password.</summary>
    private static string[] ReadAuthorizedKeys(SshEndpoint endpoint)
    {
        using var sftp = new Renci.SshNet.SftpClient(endpoint.Host, endpoint.Port, endpoint.User, SshServerFactAttribute.Password);
        sftp.Connect();
        return sftp.ReadAllText(".ssh/authorized_keys").Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    [SshServerFact]
    public async Task Wrong_password_is_reported_as_failed_login()
    {
        var hostKey = await TrustServerAsync();
        var (endpoint, _) = Repository();

        var failure = await Assert.ThrowsAsync<SshOperationException>(() =>
            _ssh.RunWithPasswordAsync(endpoint, "definitely-wrong", hostKey, "true"));

        Assert.Equal(SshFailureKind.AuthenticationFailed, failure.Kind);
    }

    [SshServerFact]
    public async Task Login_is_refused_when_the_server_does_not_present_the_trusted_key()
    {
        var (endpoint, _) = Repository();
        var someOtherKey = new SshHostKey(FakeSsh.HostKeyBlob("ssh-ed25519", 3));

        var failure = await Assert.ThrowsAsync<SshOperationException>(() =>
            _ssh.RunWithPasswordAsync(endpoint, SshServerFactAttribute.Password, someOtherKey, "true"));

        Assert.Equal(SshFailureKind.HostKeyChanged, failure.Kind);
    }

    [SshServerFact]
    public async Task Borg_refuses_a_server_whose_host_key_is_not_the_confirmed_one()
    {
        var hostKey = await TrustServerAsync();
        var (endpoint, location) = Repository();
        var key = SshKeyEntry.Generate("it-key", "borgstudio-integration-test", DateTimeOffset.Now);
        await _ssh.RunWithPasswordAsync(endpoint, SshServerFactAttribute.Password, hostKey,
            AuthorizedKeys.AppendCommand(AuthorizedKeys.Line(key.PublicKey, endpoint.RepositoryPath)));

        // A known_hosts that only knows a different key for this server.
        var wrongKnownHosts = new KnownHostsFile(_directory.Combine("wrong_known_hosts"));
        wrongKnownHosts.Add(Host, Port, new SshHostKey(FakeSsh.HostKeyBlob(hostKey.Type, 4)));

        // Several times: whether borg itself passes on ssh's message is a race, the result must not be.
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var info = await BorgClient.CreateDefault().InfoAsync(Borg, location, Passphrase,
                new SshAccess(key.PrivateKey, wrongKnownHosts.Path));

            Assert.Equal(BorgErrorKind.SshHostKeyFailed, info.Error?.Kind);
        }
    }
}
