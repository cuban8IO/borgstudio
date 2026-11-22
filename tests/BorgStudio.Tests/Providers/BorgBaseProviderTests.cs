using BorgStudio.Plugins;
using BorgStudio.Providers.BorgBase;
using BorgStudio.Tests.TestSupport;

namespace BorgStudio.Tests.Providers;

/// <summary>The BorgBase provider against a fake of its GraphQL API: keys, repositories, quota.</summary>
public sealed class BorgBaseProviderTests
{
    private const string PublicKey = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIBorgStudioTestKeyData borgstudio-pc-1a2b3c4d";

    private readonly FakeBorgBase _api = new();
    private readonly BorgBaseProvider _provider;

    public BorgBaseProviderTests() => _provider = new BorgBaseProvider(_api);

    private static Dictionary<string, string> Values(string repo, string region = "eu") =>
        new() { [BorgBaseProvider.RepoKey] = repo, [BorgBaseProvider.RegionKey] = region };

    private Task<IReadOnlyDictionary<string, string>?> ProvisionAsync(string repo, bool createNew = true, string? token = FakeBorgBase.Token) =>
        _provider.ProvisionAsync(new SshKeyProvisionRequest(Values(repo, "us"),
            token is null ? new Dictionary<string, string>() : new Dictionary<string, string> { [BorgBaseProvider.TokenKey] = token },
            PublicKey, "borgstudio-pc-1a2b3c4d", "Laptop", createNew), CancellationToken.None);

    [Fact]
    public void Addresses_from_borgbase_become_locations_without_a_password_login()
    {
        var location = _provider.GetLocation(Values("ssh://mmvz9gp4@mmvz9gp4.repo.borgbase.com/./repo"));
        Assert.Equal("ssh://mmvz9gp4@mmvz9gp4.repo.borgbase.com/./repo", location.Url);
        Assert.Equal(("mmvz9gp4.repo.borgbase.com", 22, "mmvz9gp4", "repo"),
            (location.Ssh!.Host, location.Ssh.Port, location.Ssh.User, location.Ssh.RepositoryPath));
        Assert.Equal(SshKeyInstallation.Provider, location.Ssh.KeyInstallation);
        Assert.Empty(location.BorgArguments);

        // The older scp form, and the address the API gave.
        Assert.Equal("ssh://y4ab823u@y4ab823u.repo.borgbase.com/./repo", _provider.GetLocation(Values("y4ab823u@y4ab823u.repo.borgbase.com:repo")).Url);
        var named = Values("Laptop");
        named[BorgBaseProvider.AddressKey] = "ssh://abcd1@abcd1.repo.borgbase.com/./repo";
        named[BorgBaseProvider.HostKeysKey] = FakeBorgBaseRepo.Ed25519;
        Assert.Equal([FakeBorgBaseRepo.Ed25519], _provider.GetLocation(named).Ssh!.PublishedHostKeyFingerprints);

        // A name never set up: no address yet.
        Assert.Throws<ArgumentException>(() => _provider.GetLocation(Values("Laptop")));
        Assert.Single(_provider.Validate(Values("")));
        Assert.Empty(_provider.Validate(Values("Laptop")));
        Assert.Equal(ProviderFieldKind.Secret, _provider.Fields.Single(field => field.Key == BorgBaseProvider.TokenKey).Kind);
    }

    [Fact]
    public async Task With_a_token_a_new_repository_is_created_with_full_access_for_the_new_key()
    {
        var values = await ProvisionAsync("Laptop");

        Assert.Equal(["query sshList", "mutation sshAdd", "query repoList", "mutation repoAdd"], _api.Operations);
        Assert.All(_api.Requests, request => Assert.Equal(FakeBorgBase.Token, request.Token));
        var repo = Assert.Single(_api.Repos);
        Assert.Equal(("Laptop", "us"), (repo.Name, repo.Region));
        Assert.Equal([_api.Keys.Single().Id], repo.FullAccessKeys);
        Assert.Equal(PublicKey, _api.Keys.Single().KeyData);

        Assert.Equal("ssh://abcd1@abcd1.repo.borgbase.com/./repo", values![BorgBaseProvider.AddressKey]);
        Assert.Equal("r1", values[BorgBaseProvider.RepoIdKey]);
        // The MD5 fingerprint is left out: BorgStudio compares SHA256.
        Assert.Equal(FakeBorgBaseRepo.Ed25519, values[BorgBaseProvider.HostKeysKey]);
    }

    [Fact]
    public async Task An_existing_repository_gets_the_key_once_and_a_known_key_is_not_added_again()
    {
        _api.Keys.Add(("7", "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIBorgStudioTestKeyData other comment"));
        _api.Repos.Add(new FakeBorgBaseRepo("r9", "Laptop", "zz99") { FullAccessKeys = { "3" } });

        var values = await ProvisionAsync("Laptop", createNew: false);

        Assert.Equal(["query sshList", "query repoList", "mutation repoEdit"], _api.Operations);
        Assert.Equal(["3", "7"], _api.Repos.Single().FullAccessKeys);
        Assert.Equal("ssh://zz99@zz99.repo.borgbase.com/./repo", values![BorgBaseProvider.AddressKey]);

        // Already there: nothing to change.
        _api.Requests.Clear();
        await ProvisionAsync("zz99@zz99.repo.borgbase.com:repo", createNew: false);
        Assert.Equal(["query sshList", "query repoList"], _api.Operations);
    }

    [Fact]
    public async Task Without_a_token_the_key_is_added_by_hand_and_an_address_is_needed()
    {
        Assert.Null(await ProvisionAsync("ssh://mmvz9gp4@mmvz9gp4.repo.borgbase.com/./repo", token: null));
        Assert.Empty(_api.Requests);

        var noAddress = await Assert.ThrowsAsync<RepositoryUnavailableException>(() => ProvisionAsync("Laptop", token: null));
        Assert.Contains("ssh://", noAddress.Message);
    }

    [Fact]
    public async Task A_missing_repository_or_a_refused_token_is_told()
    {
        var missing = await Assert.ThrowsAsync<RepositoryUnavailableException>(() => ProvisionAsync("Laptop", createNew: false));
        Assert.Contains("Laptop", missing.Message);
        Assert.Empty(_api.Repos);

        var refused = await Assert.ThrowsAsync<RepositoryUnavailableException>(() => ProvisionAsync("Laptop", token: "expired"));
        Assert.Contains("API", refused.Message);
    }

    [Fact]
    public async Task The_quota_is_the_repositorys_or_else_the_accounts()
    {
        _api.Repos.Add(new FakeBorgBaseRepo("r1", "Laptop", "abcd1") { UsageMb = 250, QuotaMb = 1000 });
        var values = Values("Laptop");
        values[BorgBaseProvider.RepoIdKey] = "r1";
        var secrets = new Dictionary<string, string> { [BorgBaseProvider.TokenKey] = FakeBorgBase.Token };

        Assert.Equal(new StorageQuota(250 * BorgBaseProvider.Megabyte, 1000 * BorgBaseProvider.Megabyte),
            await _provider.GetQuotaAsync(new StorageQuotaRequest(values) { Secrets = secrets }, CancellationToken.None));

        _api.Repos[0].QuotaMb = 0;
        Assert.Equal(new StorageQuota(1500 * BorgBaseProvider.Megabyte, 10000 * BorgBaseProvider.Megabyte),
            await _provider.GetQuotaAsync(new StorageQuotaRequest(values) { Secrets = secrets }, CancellationToken.None));

        // Without the token, or refused: no quota.
        Assert.Null(await _provider.GetQuotaAsync(new StorageQuotaRequest(values), CancellationToken.None));
        Assert.Null(await _provider.GetQuotaAsync(new StorageQuotaRequest(values)
            { Secrets = new Dictionary<string, string> { [BorgBaseProvider.TokenKey] = "expired" } }, CancellationToken.None));
    }
}
