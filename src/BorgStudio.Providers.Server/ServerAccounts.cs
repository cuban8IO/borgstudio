using System.Text.Json;
using System.Text.Json.Serialization;

namespace BorgStudio.Providers.Server;

/// <summary>A BorgStudio Server this computer is paired with.</summary>
public sealed record ServerAccount
{
    /// <summary>The server's address as <see cref="ServerAddress.Normalize"/> gives it (the key of the account).</summary>
    public required string Url { get; init; }

    /// <summary>The server's name (from its settings).</summary>
    public string Name { get; init; } = "";

    /// <summary>The account on the server that confirmed the pairing.</summary>
    public string UserName { get; init; } = "";

    /// <summary>This computer on the server (under "Devices").</summary>
    public Guid DeviceId { get; init; }

    /// <summary>
    /// SHA-256 of the server's TLS certificate (hex) when it is not trusted by the system and the user confirmed it; only
    /// this certificate is accepted then. <c>null</c>: the system has to trust it.
    /// </summary>
    public string? PinnedCertificate { get; init; }

    public DateTimeOffset PairedAt { get; init; }

    public override string ToString() => Name.Length > 0 && Name != ServerAddress.Display(Url) ? $"{Name} ({ServerAddress.Display(Url)})" : ServerAddress.Display(Url);
}

/// <summary>Where the device tokens are kept (BorgStudio's vault); reading may fail while it is locked.</summary>
public interface IServerSecrets
{
    /// <summary>The secret; <c>null</c> if there is none or it cannot be read now (locked).</summary>
    string? Get(string name);

    /// <summary>Stores a secret (the vault must be open).</summary>
    void Set(string name, string value);

    void Remove(string name);
}

/// <summary>
/// The servers this computer is paired with: what is not secret in <c>servers.json</c>, each device token in the vault
/// ("borgstudio-server/&lt;address&gt;"). One pairing serves all repositories on that server.
/// </summary>
public sealed class ServerAccounts(string filePath, IServerSecrets secrets)
{
    public const string SecretPrefix = "borgstudio-server/";

    private static readonly object Gate = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string FilePath => filePath;

    /// <summary>Raised after a server was paired again or removed (other parts show its state).</summary>
    public event EventHandler? Changed;

    public IReadOnlyList<ServerAccount> All()
    {
        lock (Gate)
            return Load();
    }

    public ServerAccount? Find(string url)
    {
        var key = ServerAddress.Normalize(url);
        return key is null ? null : All().FirstOrDefault(account => account.Url == key);
    }

    /// <summary>The device token; <c>null</c> if not paired or the vault is locked.</summary>
    public string? Token(string url) =>
        ServerAddress.Normalize(url) is { } key ? secrets.Get(SecretPrefix + key) : null;

    /// <summary>Adds or replaces the account and its token (the vault must be open).</summary>
    public void Save(ServerAccount account, string token)
    {
        lock (Gate)
        {
            secrets.Set(SecretPrefix + account.Url, token);
            var accounts = Load().Where(existing => existing.Url != account.Url).Append(account).OrderBy(existing => existing.Url, StringComparer.Ordinal).ToList();
            Write(accounts);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Forgets the server and its token.</summary>
    public void Remove(string url)
    {
        if (ServerAddress.Normalize(url) is not { } key)
            return;
        lock (Gate)
        {
            secrets.Remove(SecretPrefix + key);
            Write([.. Load().Where(account => account.Url != key)]);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private List<ServerAccount> Load()
    {
        try
        {
            return File.Exists(filePath)
                ? JsonSerializer.Deserialize<List<ServerAccount>>(File.ReadAllText(filePath), JsonOptions) ?? []
                : [];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    private void Write(List<ServerAccount> accounts)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(filePath))!);
        var temporary = $"{filePath}.{Environment.ProcessId}.tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(accounts, JsonOptions));
        File.Move(temporary, filePath, overwrite: true);
    }
}

/// <summary>Addresses of servers as the user types them and as they are kept.</summary>
public static class ServerAddress
{
    /// <summary>
    /// "backup.example.org:8443" → "https://backup.example.org:8443/": HTTPS unless "http://" is typed, host in lower
    /// case, no path, query or user. <c>null</c> if it is no address.
    /// </summary>
    public static string? Normalize(string? text)
    {
        text = text?.Trim() ?? "";
        if (text.Length == 0)
            return null;
        if (!text.Contains("://", StringComparison.Ordinal))
            text = "https://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http") || uri.Host.Length == 0
            || uri.UserInfo.Length > 0)
            return null;
        var builder = new UriBuilder(uri.Scheme, uri.Host.ToLowerInvariant(), uri.IsDefaultPort ? -1 : uri.Port) { Path = PathOf(uri) };
        return builder.Uri.AbsoluteUri;

        // A server behind a reverse proxy may live below a path ("https://example.org/borgstudio/").
        static string PathOf(Uri uri) => uri.AbsolutePath.TrimEnd('/') is { Length: > 0 } path ? path + "/" : "/";
    }

    /// <summary>For people: without "https://" and the trailing slash.</summary>
    public static string Display(string url) =>
        url.StartsWith("https://", StringComparison.Ordinal) ? url["https://".Length..].TrimEnd('/') : url.TrimEnd('/');

    public static bool IsUnencrypted(string url) => url.StartsWith("http://", StringComparison.Ordinal);
}
