using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using BorgStudio.Plugins;
using BorgStudio.Providers.Server;
using BorgStudio.Tests.TestSupport;

namespace BorgStudio.Tests.Providers;

/// <summary>The BorgStudio Server provider against a fake of the server's interface: keys, repositories, quota, reports.</summary>
public sealed class ServerProviderTests : IDisposable
{
    private const string PublicKey = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIBorgStudioTestKeyData borgstudio-pc-1a2b3c4d";

    private readonly TemporaryDirectory _directory = new();
    private readonly FakeBorgStudioServer _server = new();
    private readonly MemoryServerSecrets _secrets = new();
    private readonly ServerAccounts _accounts;
    private readonly ServerProvider _provider;

    public ServerProviderTests()
    {
        _accounts = new ServerAccounts(_directory.Combine("servers.json"), _secrets);
        _provider = new ServerProvider(_accounts, new ServerHttp(_server));
        _accounts.Save(new ServerAccount { Url = FakeBorgStudioServer.Url, Name = "Backup Server", UserName = "alice" }, FakeBorgStudioServer.Token);
    }

    public void Dispose() => _directory.Dispose();

    private static Dictionary<string, string> Values(Guid? repository = null, string? newName = null) => new()
    {
        [ServerProvider.ServerKey] = FakeBorgStudioServer.Url,
        [ServerProvider.RepositoryIdKey] = repository?.ToString() ?? "",
        [ServerProvider.NewNameKey] = newName ?? "",
    };

    private Task<IReadOnlyDictionary<string, string>?> ProvisionAsync(Dictionary<string, string> values, bool createNew) =>
        _provider.ProvisionAsync(new SshKeyProvisionRequest(values, new Dictionary<string, string>(), PublicKey, "borgstudio-pc", "Laptop", createNew),
            CancellationToken.None);

    [Theory]
    [InlineData("backup.example.org:8443", "https://backup.example.org:8443/")]
    [InlineData("HTTPS://Backup.Example.org:8443/settings?x=1", "https://backup.example.org:8443/settings/")]
    [InlineData("https://backup.example.org", "https://backup.example.org/")]
    [InlineData("http://10.0.0.5:8080", "http://10.0.0.5:8080/")]
    [InlineData("https://example.org/borgstudio/", "https://example.org/borgstudio/")]
    [InlineData("ftp://example.org", null)]
    [InlineData("https://user@example.org", null)]
    [InlineData("   ", null)]
    public void Addresses_are_normalized(string typed, string? expected) =>
        Assert.Equal(expected, ServerAddress.Normalize(typed));

    [Fact]
    public void The_address_the_server_gives_becomes_a_location_with_its_host_keys()
    {
        var id = Guid.NewGuid();
        var values = Values(id);
        Assert.Throws<ArgumentException>(() => _provider.GetLocation(values));
        values[ServerProvider.AddressKey] = FakeBorgStudioServer.SshUrl(id);
        values[ServerProvider.HostKeysKey] = FakeBorgStudioServer.HostKey;

        var location = _provider.GetLocation(values);

        Assert.Equal(FakeBorgStudioServer.SshUrl(id), location.Url);
        Assert.Equal(("backup.test", 2222, "borgstudio", $"./{id:N}"), (location.Ssh!.Host, location.Ssh.Port, location.Ssh.User, location.Ssh.RepositoryPath));
        Assert.Equal(SshKeyInstallation.Provider, location.Ssh.KeyInstallation);
        Assert.Equal([FakeBorgStudioServer.HostKey], location.Ssh.PublishedHostKeyFingerprints);
        Assert.True(_provider.UsesSsh);
        Assert.Empty(_provider.Fields);

        Assert.Empty(_provider.Validate(values));
        Assert.Single(_provider.Validate(Values()));
        Assert.Empty(_provider.Validate(Values(newName: "laptop")));
        Assert.Single(_provider.Validate(new Dictionary<string, string> { [ServerProvider.RepositoryIdKey] = id.ToString() }));
    }

    [Fact]
    public async Task An_existing_repository_gets_this_computers_key()
    {
        var repository = _server.AddRepository("laptop");

        var values = await ProvisionAsync(Values(repository.Id), createNew: false);

        Assert.Equal(PublicKey, _server.Keys[repository.Id]);
        Assert.Equal(FakeBorgStudioServer.SshUrl(repository.Id), values![ServerProvider.AddressKey]);
        Assert.Equal(FakeBorgStudioServer.HostKey, values[ServerProvider.HostKeysKey]);
        Assert.All(_server.Requests, request => Assert.Equal(FakeBorgStudioServer.Token, request.Token));
    }

    [Fact]
    public async Task A_new_repository_is_created_on_the_server_once()
    {
        var values = Values(newName: "laptop");
        values[ServerProvider.NewDatastoreKey] = _server.DatastoreId.ToString();
        values[ServerProvider.NewQuotaKey] = (50L << 30).ToString();

        var provisioned = await ProvisionAsync(values, createNew: true);

        var created = Assert.Single(_server.Repositories);
        Assert.Equal(("laptop", 50L << 30), (created.Name, created.QuotaBytes));
        Assert.Equal(created.Id.ToString(), provisioned![ServerProvider.RepositoryIdKey]);
        Assert.Equal("alice/laptop", provisioned[ServerProvider.RepositoryNameKey]);
        Assert.Equal("", provisioned[ServerProvider.NewNameKey]);
        Assert.Equal(PublicKey, _server.Keys[created.Id]);

        // Tried again after borg failed: the repository created then, not a second one.
        Assert.Equal(created.Id.ToString(), (await ProvisionAsync(values, createNew: true))![ServerProvider.RepositoryIdKey]);
        Assert.Single(_server.Repositories);

        // Connecting needs a repository; creating is not allowed for every account.
        await Assert.ThrowsAsync<RepositoryUnavailableException>(() => ProvisionAsync(Values(newName: "other"), createNew: false));
        _server.MayCreate = false;
        var refused = await Assert.ThrowsAsync<RepositoryUnavailableException>(() => ProvisionAsync(Values(newName: "other"), createNew: true));
        Assert.Contains("may not create", refused.Message);
    }

    [Fact]
    public async Task Without_pairing_or_with_a_revoked_one_the_reason_is_told()
    {
        var repository = _server.AddRepository("laptop");
        _secrets.Locked = true;
        await Assert.ThrowsAsync<RepositoryUnavailableException>(() => ProvisionAsync(Values(repository.Id), createNew: false));
        _secrets.Locked = false;

        _server.Revoked = true;
        var revoked = await Assert.ThrowsAsync<ServerException>(() => _provider.GetRepositoryAsync(Values(repository.Id), CancellationToken.None));
        Assert.Equal(ServerProblem.Revoked, revoked.Problem);

        _server.Unreachable = true;
        var unreachable = await Assert.ThrowsAsync<ServerException>(() => _provider.GetRepositoryAsync(Values(repository.Id), CancellationToken.None));
        Assert.Equal(ServerProblem.Unreachable, unreachable.Problem);
        Assert.Null(await _provider.GetQuotaAsync(new StorageQuotaRequest(Values(repository.Id)), CancellationToken.None));

        _accounts.Remove(FakeBorgStudioServer.Url);
        var notPaired = Assert.Throws<ServerException>(() => _provider.ClientFor(FakeBorgStudioServer.Url));
        Assert.Equal(ServerProblem.NotPaired, notPaired.Problem);
        Assert.Empty(_secrets.Values);
    }

    [Fact]
    public async Task The_quota_is_the_smallest_limit_the_server_knows()
    {
        var withQuota = _server.AddRepository("laptop", used: 10L << 30, quota: 50L << 30);
        var withoutQuota = _server.AddRepository("nas", used: 10L << 30);

        Assert.Equal(new StorageQuota(10L << 30, 50L << 30),
            await _provider.GetQuotaAsync(new StorageQuotaRequest(Values(withQuota.Id)), CancellationToken.None));
        // No quota of its own: the datastore's free space (400 GB) limits it.
        Assert.Equal(new StorageQuota(10L << 30, 410L << 30),
            await _provider.GetQuotaAsync(new StorageQuotaRequest(Values(withoutQuota.Id)), CancellationToken.None));

        var owner = withoutQuota with { OwnerQuota = new ServerOwnerQuota(90L << 30, 100L << 30) };
        Assert.Equal(20L << 30, owner.Capacity);
    }

    [Fact]
    public async Task Reports_are_sent_with_borgs_figures_and_refused_ones_are_dropped()
    {
        var repository = _server.AddRepository("laptop");
        var started = new DateTimeOffset(2026, 11, 25, 10, 0, 0, TimeSpan.Zero);
        var report = new RunReport(RunReportKind.Backup, RunReportOutcome.Warnings, started, TimeSpan.FromMinutes(4))
        {
            ArchiveName = "laptop-2026-11-25",
            OriginalSize = 1000,
            DeduplicatedSize = 10,
            TotalSize = 5000,
            UniqueCompressedSize = 300,
            Message = "file.txt: Permission denied",
        };

        await _provider.SendReportAsync(new RunReportRequest(Values(repository.Id), report), CancellationToken.None);

        var sent = Assert.Single(_server.Reports);
        Assert.Equal("backup", sent["kind"]!.GetValue<string>());
        Assert.Equal("warnings", sent["outcome"]!.GetValue<string>());
        Assert.Equal(240, sent["durationSeconds"]!.GetValue<double>());
        Assert.Equal(300, sent["uniqueCompressedSize"]!.GetValue<long>());
        Assert.Equal("laptop-2026-11-25", sent["archiveName"]!.GetValue<string>());

        await Assert.ThrowsAsync<RunReportRejectedException>(() =>
            _provider.SendReportAsync(new RunReportRequest(Values(Guid.NewGuid()), report), CancellationToken.None));
        _server.Revoked = true;
        await Assert.ThrowsAsync<RepositoryUnavailableException>(() =>
            _provider.SendReportAsync(new RunReportRequest(Values(repository.Id), report), CancellationToken.None));
    }

    [Fact]
    public async Task A_probe_tells_a_server_from_anything_else()
    {
        var http = new ServerHttp(_server);
        Assert.Null((await http.ProbeAsync(FakeBorgStudioServer.Url, CancellationToken.None)).Certificate);

        var other = new ServerHttp(new FakeHttpHandler());
        var notAServer = await Assert.ThrowsAsync<ServerException>(() => other.ProbeAsync("https://example.org/", CancellationToken.None));
        Assert.Equal(ServerProblem.NotAServer, notAServer.Problem);

        _server.Unreachable = true;
        Assert.Equal(ServerProblem.Unreachable,
            (await Assert.ThrowsAsync<ServerException>(() => http.ProbeAsync(FakeBorgStudioServer.Url, CancellationToken.None))).Problem);
    }

    [Fact]
    public void Only_trusted_or_confirmed_certificates_are_accepted()
    {
        using var key = ECDsa.Create();
        using var certificate = new CertificateRequest("CN=backup.test", key, HashAlgorithmName.SHA256)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        var confirmed = ServerCertificate.Of(certificate);

        Assert.Equal(64, confirmed.Sha256.Length);
        Assert.Equal(32, confirmed.Fingerprint.Split(':').Length);
        Assert.Equal("CN=backup.test", confirmed.Subject);

        Assert.True(ServerHttp.Accepts(SslPolicyErrors.None, certificate, null));
        Assert.False(ServerHttp.Accepts(SslPolicyErrors.RemoteCertificateChainErrors, certificate, null));
        Assert.True(ServerHttp.Accepts(SslPolicyErrors.RemoteCertificateChainErrors, certificate, confirmed.Sha256.ToLowerInvariant()));
        Assert.False(ServerHttp.Accepts(SslPolicyErrors.RemoteCertificateChainErrors, certificate, new string('A', 64)));
    }

    [Fact]
    public void Accounts_keep_their_token_in_the_secrets_and_the_rest_in_a_file()
    {
        var account = Assert.Single(_accounts.All());
        Assert.Equal("Backup Server (backup.test:8443)", account.ToString());
        Assert.Equal(FakeBorgStudioServer.Token, _accounts.Token("backup.test:8443"));
        Assert.DoesNotContain(FakeBorgStudioServer.Token, File.ReadAllText(_accounts.FilePath));
        Assert.Equal(FakeBorgStudioServer.Token, _secrets.Values[ServerAccounts.SecretPrefix + FakeBorgStudioServer.Url]);

        // Paired again: replaced, not added.
        _accounts.Save(account with { UserName = "bob", PinnedCertificate = "AB" }, "new-token");
        Assert.Equal(("bob", "AB"), (Assert.Single(_accounts.All()).UserName, _accounts.Find(FakeBorgStudioServer.Url)!.PinnedCertificate));
        Assert.Equal("new-token", _accounts.Token(FakeBorgStudioServer.Url));
    }
}
