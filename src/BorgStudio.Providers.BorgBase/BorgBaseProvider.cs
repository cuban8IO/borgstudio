using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using BorgStudio.Plugins;
using BorgStudio.Providers.BorgBase.Resources;

namespace BorgStudio.Providers.BorgBase;

/// <summary>Built into BorgStudio, but registered through the same plugin API as external providers.</summary>
/// <param name="handler">Answers the API's requests (tests replace it); default: the internet.</param>
public sealed class BorgBaseProviderPlugin(HttpMessageHandler? handler = null) : IBorgStudioPlugin
{
    public BorgBaseProviderPlugin() : this(null)
    {
    }

    public void Register(IPluginRegistrar registrar) => registrar.AddRepositoryProvider(new BorgBaseProvider(handler));
}

/// <summary>
/// Repository at BorgBase. There is no password login: with an API token the provider registers the login key, creates or
/// finds the repository and reads its quota (GraphQL at https://api.borgbase.com/graphql); without one, the user adds the
/// key in BorgBase's web interface and enters the repository's address.
/// </summary>
public sealed partial class BorgBaseProvider : IRepositoryProvider, ISshKeyProvisioner, IStorageQuotaSource
{
    public const string TokenKey = "token";
    public const string RepoKey = "repo";
    public const string RegionKey = "region";

    /// <summary>Kept by the provider: the repository's address, its id at BorgBase and the server's host key fingerprints.</summary>
    public const string AddressKey = "address";
    public const string RepoIdKey = "repoId";
    public const string HostKeysKey = "hostKeys";

    public const string Endpoint = "https://api.borgbase.com/graphql";

    /// <summary>BorgBase counts sizes in megabytes (as its account's <c>storageLimitMb</c> says).</summary>
    public const long Megabyte = 1000 * 1000;

    private readonly HttpClient _http;

    public BorgBaseProvider(HttpMessageHandler? handler = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromSeconds(30);
    }

    public string Id => "borgbase";

    public string DisplayName => Strings.DisplayName;

    public string Description => Strings.Description;

    public bool UsesSsh => true;

    public IReadOnlyList<ProviderField> Fields =>
    [
        new(TokenKey, Strings.TokenLabel, ProviderFieldKind.Secret) { Required = false, Hint = Strings.TokenHint },
        new(RepoKey, Strings.RepoLabel) { Hint = Strings.RepoHint },
        new(RegionKey, Strings.RegionLabel, ProviderFieldKind.Choice)
        {
            DefaultValue = "eu",
            Hint = Strings.RegionHint,
            Options = [new("eu", Strings.RegionEu), new("us", Strings.RegionUs)],
        },
    ];

    public IReadOnlyList<string> Validate(IReadOnlyDictionary<string, string> values) =>
        Value(values, RepoKey).Length == 0 ? [Strings.RepoRequired] : [];

    /// <exception cref="ArgumentException">No address yet: a repository name that was never set up through the API.</exception>
    public RepositoryLocation GetLocation(IReadOnlyDictionary<string, string> values)
    {
        var address = Address(Value(values, AddressKey)) ?? Address(Value(values, RepoKey))
            ?? throw new ArgumentException("The BorgBase repository has no address yet.", nameof(values));
        return new RepositoryLocation($"ssh://{address.User}@{address.Host}/./{address.Path}")
        {
            Ssh = new SshEndpoint(address.Host, 22, address.User, address.Path)
            {
                KeyInstallation = SshKeyInstallation.Provider,
                PublishedHostKeyFingerprints = Value(values, HostKeysKey).Split(' ', StringSplitOptions.RemoveEmptyEntries),
            },
        };
    }

    // --- Setting up the key and the repository ---

    public async Task<IReadOnlyDictionary<string, string>?> ProvisionAsync(SshKeyProvisionRequest request, CancellationToken cancellationToken)
    {
        var repo = Value(request.Values, RepoKey);
        if (request.Secrets.GetValueOrDefault(TokenKey) is not { Length: > 0 } token)
            return Address(repo) is null ? throw new RepositoryUnavailableException(Strings.AddressNeeded) : null;

        var keyId = await KeyIdAsync(token, request.PublicKey, request.KeyName, cancellationToken);
        var repositories = await QueryAsync(token, "query { repoList { id name repoPath fullAccessKeyList { id } server { fingerprintEd25519 fingerprintEcdsa fingerprintRsa } } }",
            null, cancellationToken);
        var address = Address(repo);
        var found = (repositories["repoList"] as JsonArray ?? []).OfType<JsonObject>().FirstOrDefault(candidate => address is null
            ? string.Equals(candidate["name"]?.GetValue<string>(), repo, StringComparison.Ordinal)
            : Address(candidate["repoPath"]?.GetValue<string>() ?? "") == address);

        JsonObject repository;
        if (found is not null)
        {
            var keys = (found["fullAccessKeyList"] as JsonArray ?? []).Select(key => key?["id"]?.ToString()).OfType<string>().ToList();
            if (!keys.Contains(keyId))
                await QueryAsync(token, "mutation ($id: String!, $keys: [String]) { repoEdit(id: $id, fullAccessKeys: $keys) { repoEdited { id } } }",
                    new JsonObject { ["id"] = found["id"]!.ToString(), ["keys"] = new JsonArray([.. keys.Append(keyId).Select(key => (JsonNode)key)]) },
                    cancellationToken);
            repository = found;
        }
        else if (address is null && request.CreateNew)
        {
            var added = await QueryAsync(token,
                "mutation ($name: String!, $region: String!, $keys: [String]) { repoAdd(name: $name, region: $region, fullAccessKeys: $keys) "
                + "{ repoAdded { id name repoPath server { fingerprintEd25519 fingerprintEcdsa fingerprintRsa } } } }",
                new JsonObject
                {
                    ["name"] = repo,
                    ["region"] = Value(request.Values, RegionKey) is { Length: > 0 } region ? region : "eu",
                    ["keys"] = new JsonArray(keyId),
                },
                cancellationToken);
            repository = added["repoAdd"]?["repoAdded"] as JsonObject ?? throw new RepositoryUnavailableException(BorgBaseError("repoAdd"));
        }
        else
        {
            throw new RepositoryUnavailableException(string.Format(CultureInfo.CurrentCulture, Strings.RepoNotFound, repo));
        }

        var path = Address(repository["repoPath"]?.GetValue<string>() ?? "") ?? throw new RepositoryUnavailableException(BorgBaseError("repoPath"));
        return new Dictionary<string, string>
        {
            [AddressKey] = $"ssh://{path.User}@{path.Host}/./{path.Path}",
            [RepoIdKey] = repository["id"]?.ToString() ?? "",
            [HostKeysKey] = string.Join(' ', HostKeys(repository["server"] as JsonObject)),
        };
    }

    /// <summary>The key's id at BorgBase: registered before (same key data), or added now.</summary>
    private async Task<string> KeyIdAsync(string token, string publicKey, string name, CancellationToken cancellationToken)
    {
        var blob = KeyBlob(publicKey);
        var keys = await QueryAsync(token, "query { sshList { id keyData } }", null, cancellationToken);
        if ((keys["sshList"] as JsonArray ?? []).OfType<JsonObject>()
            .FirstOrDefault(key => KeyBlob(key["keyData"]?.GetValue<string>() ?? "") == blob) is { } known)
            return known["id"]!.ToString();

        var added = await QueryAsync(token, "mutation ($name: String!, $keyData: String!) { sshAdd(name: $name, keyData: $keyData) { keyAdded { id } } }",
            new JsonObject { ["name"] = name, ["keyData"] = publicKey.Trim() }, cancellationToken);
        return added["sshAdd"]?["keyAdded"]?["id"]?.ToString() ?? throw new RepositoryUnavailableException(BorgBaseError("sshAdd"));
    }

    // --- Quota ---

    /// <summary>
    /// The repository's quota if it has one at BorgBase, otherwise the account's storage limit. Needs the API token;
    /// <c>null</c> without (or when BorgBase does not answer).
    /// </summary>
    public async Task<StorageQuota?> GetQuotaAsync(StorageQuotaRequest request, CancellationToken cancellationToken)
    {
        if (request.Secrets.GetValueOrDefault(TokenKey) is not { Length: > 0 } token)
            return null;
        try
        {
            if (Value(request.Values, RepoIdKey) is { Length: > 0 } repoId)
            {
                var data = await QueryAsync(token, "query ($id: String!) { repo(repoId: $id) { currentUsage quota quotaEnabled } }",
                    new JsonObject { ["id"] = repoId }, cancellationToken);
                if (data["repo"] is JsonObject repo && repo["quotaEnabled"]?.GetValue<bool>() == true && Number(repo["quota"]) is > 0 and var quota)
                    return new StorageQuota((long)(Number(repo["currentUsage"]) * Megabyte), (long)(quota * Megabyte));
            }
            var me = (await QueryAsync(token, "query { me { diskUsage storageLimitMb } }", null, cancellationToken))["me"];
            return Number(me?["storageLimitMb"]) is > 0 and var limit
                ? new StorageQuota((long)(Number(me?["diskUsage"]) * Megabyte), (long)(limit * Megabyte))
                : null;
        }
        catch (Exception exception) when (exception is RepositoryUnavailableException or HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    // --- The API ---

    /// <summary>Runs a GraphQL query or mutation; its <c>data</c>. Errors become <see cref="RepositoryUnavailableException"/>.</summary>
    private async Task<JsonObject> QueryAsync(string token, string query, JsonObject? variables, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new StringContent(new JsonObject { ["query"] = query, ["variables"] = variables }.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _http.SendAsync(message, cancellationToken);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new RepositoryUnavailableException(Strings.TokenRefused);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken)) as JsonObject
            ?? throw new RepositoryUnavailableException(BorgBaseError($"HTTP {(int)response.StatusCode}"));
        if (body["errors"] is JsonArray { Count: > 0 } errors)
        {
            var text = errors[0]?["message"]?.ToString() ?? "";
            throw new RepositoryUnavailableException(text.Contains("auth", StringComparison.OrdinalIgnoreCase)
                || text.Contains("permission", StringComparison.OrdinalIgnoreCase) ? Strings.TokenRefused : BorgBaseError(text));
        }
        return body["data"] as JsonObject ?? throw new RepositoryUnavailableException(BorgBaseError($"HTTP {(int)response.StatusCode}"));
    }

    private static string BorgBaseError(string text) => string.Format(CultureInfo.CurrentCulture, Strings.ApiError, text);

    private static double Number(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<double>(out var number) ? number : 0;

    // --- Addresses, keys ---

    private sealed record RepoAddress(string User, string Host, string Path);

    /// <summary>"ssh://user@host[:22]/./repo" or the older "user@host:repo"; <c>null</c> for anything else (a name).</summary>
    private static RepoAddress? Address(string text)
    {
        text = text.Trim();
        if (SshUrl().Match(text) is { Success: true } url)
            return new RepoAddress(url.Groups["user"].Value, url.Groups["host"].Value.ToLowerInvariant(), Clean(url.Groups["path"].Value));
        if (ScpForm().Match(text) is { Success: true } scp)
            return new RepoAddress(scp.Groups["user"].Value, scp.Groups["host"].Value.ToLowerInvariant(), Clean(scp.Groups["path"].Value));
        return null;

        static string Clean(string path)
        {
            while (path.StartsWith("./", StringComparison.Ordinal) || path.StartsWith("~/", StringComparison.Ordinal))
                path = path[2..];
            return path.Trim('/') is { Length: > 0 } clean ? clean : "repo";
        }
    }

    /// <summary>The key's type and data ("ssh-ed25519 AAAA…"), without the comment.</summary>
    private static string KeyBlob(string publicKey) =>
        string.Join(' ', publicKey.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2));

    /// <summary>The server's fingerprints as BorgStudio compares them ("SHA256:…"); others (MD5) are left out.</summary>
    private static IEnumerable<string> HostKeys(JsonObject? server) =>
        new[] { "fingerprintEd25519", "fingerprintEcdsa", "fingerprintRsa" }
            .Select(name => server?[name]?.ToString()?.Trim() ?? "")
            .Select(value => value.StartsWith("SHA256:", StringComparison.Ordinal) ? value
                : Base64Fingerprint().IsMatch(value) ? "SHA256:" + value.TrimEnd('=')
                : "")
            .Where(value => value.Length > 0);

    private static string Value(IReadOnlyDictionary<string, string> values, string key) =>
        values.GetValueOrDefault(key)?.Trim() ?? "";

    [GeneratedRegex(@"^ssh://(?<user>[A-Za-z0-9._-]+)@(?<host>[A-Za-z0-9.-]+)(:22)?/(?<path>.*)$")]
    private static partial Regex SshUrl();

    [GeneratedRegex(@"^(?<user>[A-Za-z0-9._-]+)@(?<host>[A-Za-z0-9.-]+\.[A-Za-z]+):(?<path>[^/].*)$")]
    private static partial Regex ScpForm();

    [GeneratedRegex(@"^[A-Za-z0-9+/]{43}=?$")]
    private static partial Regex Base64Fingerprint();
}
