using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using BorgStudio.Providers.Server.Resources;

namespace BorgStudio.Providers.Server;

/// <summary>What went wrong talking to a server.</summary>
public enum ServerProblem
{
    /// <summary>No connection (server off, wrong address, network).</summary>
    Unreachable,

    /// <summary>The token is not (or no longer) valid: the pairing was revoked on the server.</summary>
    Revoked,

    /// <summary>This computer is not paired with the server, or the vault is locked.</summary>
    NotPaired,

    /// <summary>The server presented another certificate than the confirmed one.</summary>
    CertificateChanged,

    /// <summary>The repository does not exist there, or the account has no access to it.</summary>
    NotFound,

    /// <summary>The server refused the request (its message says why).</summary>
    Rejected,

    /// <summary>The address answers, but not like a BorgStudio Server.</summary>
    NotAServer,
}

public sealed class ServerException(ServerProblem problem, string message) : Exception(message)
{
    public ServerProblem Problem { get; } = problem;
}

/// <summary>A server's TLS certificate as the user is asked to confirm it.</summary>
/// <param name="Sha256">SHA-256 of the certificate (hex, upper case, without separators).</param>
public sealed record ServerCertificate(string Sha256, string Subject, DateTimeOffset NotAfter)
{
    /// <summary>"AB:CD:…" as browsers and the server's settings show it.</summary>
    public string Fingerprint => string.Join(':', Enumerable.Range(0, Sha256.Length / 2).Select(i => Sha256.Substring(i * 2, 2)));

    public static ServerCertificate Of(X509Certificate certificate)
    {
        // The one TLS hands over stays with TLS; a copy only where it is no X509Certificate2.
        if (certificate is X509Certificate2 x509)
            return From(x509);
        using var copy = new X509Certificate2(certificate);
        return From(copy);

        static ServerCertificate From(X509Certificate2 x509) =>
            new(Convert.ToHexString(SHA256.HashData(x509.RawData)), x509.Subject, x509.NotAfter);
    }
}

/// <summary>What the first contact found out.</summary>
/// <param name="Certificate">The certificate if the system does not trust it (to be confirmed); <c>null</c> if it does or the address is http.</param>
public sealed record ServerProbe(string Url, ServerCertificate? Certificate);

/// <summary>
/// HTTP for the servers: one client per server and confirmed certificate. A certificate the system trusts is always
/// accepted; otherwise only the one the user confirmed (pinned). Tests pass a handler that answers instead.
/// </summary>
public sealed class ServerHttp(HttpMessageHandler? handler = null)
{
    private readonly ConcurrentDictionary<(string Url, string? Pin), HttpClient> _clients = new();

    /// <summary>Tests: the certificate a probe finds (with <c>handler</c> there is no TLS); <c>null</c>: trusted.</summary>
    public ServerCertificate? TestCertificate { get; set; }

    public HttpClient Client(string url, string? pinnedCertificate) =>
        _clients.GetOrAdd((url, pinnedCertificate), key =>
        {
            var client = handler is null
                ? new HttpClient(new SocketsHttpHandler
                {
                    SslOptions = new SslClientAuthenticationOptions
                    {
                        RemoteCertificateValidationCallback = (_, certificate, _, errors) => Accepts(errors, certificate, key.Pin),
                    },
                    PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                })
                : new HttpClient(handler, disposeHandler: false);
            client.BaseAddress = new Uri(key.Url);
            client.Timeout = TimeSpan.FromSeconds(30);
            return client;
        });

    /// <summary>Whether a certificate is accepted: trusted by the system, or the confirmed one.</summary>
    public static bool Accepts(SslPolicyErrors errors, X509Certificate? certificate, string? pinnedCertificate) =>
        errors == SslPolicyErrors.None
        || (pinnedCertificate is { Length: > 0 } && certificate is not null
            && string.Equals(ServerCertificate.Of(certificate).Sha256, pinnedCertificate, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// First contact: finds out whether the address is a BorgStudio Server and whether its certificate has to be confirmed.
    /// Nothing secret is sent.
    /// </summary>
    /// <exception cref="ServerException" />
    public async Task<ServerProbe> ProbeAsync(string url, CancellationToken cancellationToken)
    {
        ServerCertificate? untrusted = null;
        using var client = handler is null
            ? new HttpClient(new SocketsHttpHandler
            {
                SslOptions = new SslClientAuthenticationOptions
                {
                    RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
                    {
                        if (errors != SslPolicyErrors.None && certificate is not null)
                            untrusted = ServerCertificate.Of(certificate);
                        return certificate is not null;
                    },
                },
            })
            : new HttpClient(handler, disposeHandler: false);
        client.Timeout = TimeSpan.FromSeconds(20);
        if (handler is not null)
            untrusted = TestCertificate;

        HttpResponseMessage response;
        try
        {
            response = await client.GetAsync(new Uri(new Uri(url), "api/v1/server"), cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw Unreachable(url, exception);
        }

        using (response)
        {
            // Without a token the server answers 401 with its JSON error; anything else is not a BorgStudio Server.
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (response.StatusCode != HttpStatusCode.Unauthorized || !body.Contains("\"unauthorized\"", StringComparison.Ordinal))
                throw new ServerException(ServerProblem.NotAServer, string.Format(CultureInfo.CurrentCulture, Strings.NotAServer, ServerAddress.Display(url)));
        }
        return new ServerProbe(url, untrusted);
    }

    internal static ServerException Unreachable(string url, Exception exception)
    {
        if (exception is HttpRequestException { InnerException: AuthenticationException })
            return new ServerException(ServerProblem.CertificateChanged, string.Format(CultureInfo.CurrentCulture, Strings.CertificateChanged, ServerAddress.Display(url)));
        var reason = exception is TaskCanceledException ? Strings.Timeout : (exception.InnerException ?? exception).Message;
        return new ServerException(ServerProblem.Unreachable, string.Format(CultureInfo.CurrentCulture, Strings.Unreachable, ServerAddress.Display(url), reason));
    }
}

/// <summary>The server's interface (JSON at /api/v1) for one server, with or without a token.</summary>
public sealed class ServerClient(ServerHttp http, string url, string? token, string? pinnedCertificate)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string Url => url;

    // --- Pairing ---

    /// <exception cref="ServerException" />
    public Task<PairingStart> StartPairingAsync(string deviceName, string? platform, string? appVersion, CancellationToken cancellationToken) =>
        RequireAsync<PairingStart>(HttpMethod.Post, "pairing", new { deviceName, platform, appVersion }, cancellationToken);

    /// <summary>Whether the user confirmed the pairing yet (with the token once approved).</summary>
    /// <exception cref="ServerException" />
    public async Task<PairingState> CompletePairingAsync(string pairingCode, CancellationToken cancellationToken)
    {
        using var response = await RawAsync(HttpMethod.Post, "pairing/complete", new { pairingCode }, cancellationToken);
        // 202 pending, 403 denied, 410 expired: all answer with the state.
        return await ReadAsync<PairingState>(response, cancellationToken)
            ?? throw new ServerException(ServerProblem.NotAServer, string.Format(CultureInfo.CurrentCulture, Strings.NotAServer, ServerAddress.Display(url)));
    }

    // --- With the token ---

    /// <exception cref="ServerException" />
    public Task<ServerInfo> GetServerAsync(CancellationToken cancellationToken) =>
        RequireAsync<ServerInfo>(HttpMethod.Get, "server", null, cancellationToken);

    /// <exception cref="ServerException" />
    public Task<DatastoreList> GetDatastoresAsync(CancellationToken cancellationToken) =>
        RequireAsync<DatastoreList>(HttpMethod.Get, "datastores", null, cancellationToken);

    /// <exception cref="ServerException" />
    public Task<List<ServerRepository>> GetRepositoriesAsync(CancellationToken cancellationToken) =>
        RequireAsync<List<ServerRepository>>(HttpMethod.Get, "repositories", null, cancellationToken);

    /// <exception cref="ServerException" />
    public Task<ServerRepository> GetRepositoryAsync(Guid id, CancellationToken cancellationToken) =>
        RequireAsync<ServerRepository>(HttpMethod.Get, $"repositories/{id}", null, cancellationToken);

    /// <exception cref="ServerException" />
    public Task<ServerRepository> CreateRepositoryAsync(string name, Guid? datastoreId, long? quotaBytes, CancellationToken cancellationToken) =>
        RequireAsync<ServerRepository>(HttpMethod.Post, "repositories", new { name, datastoreId, quotaBytes }, cancellationToken);

    /// <summary>Registers this computer's key for the repository (replacing its earlier one); the address to use with it.</summary>
    /// <exception cref="ServerException" />
    public Task<SshKeyRegistration> SetSshKeyAsync(Guid repositoryId, string publicKey, CancellationToken cancellationToken) =>
        RequireAsync<SshKeyRegistration>(HttpMethod.Put, $"repositories/{repositoryId}/ssh-key", new { publicKey }, cancellationToken);

    /// <exception cref="ServerException" />
    public Task ReportAsync(Guid repositoryId, object report, CancellationToken cancellationToken) =>
        SendAsync<object>(HttpMethod.Post, $"repositories/{repositoryId}/reports", report, cancellationToken);

    /// <summary>Ends the pairing on the server (this computer's token and keys); a token already revoked is fine.</summary>
    /// <exception cref="ServerException" />
    public async Task UnpairAsync(CancellationToken cancellationToken)
    {
        try
        {
            await SendAsync<object>(HttpMethod.Delete, "device", null, cancellationToken);
        }
        catch (ServerException exception) when (exception.Problem is ServerProblem.Revoked or ServerProblem.NotFound)
        {
        }
    }

    private async Task<T> RequireAsync<T>(HttpMethod method, string path, object? body, CancellationToken cancellationToken) where T : class =>
        await SendAsync<T>(method, path, body, cancellationToken)
        ?? throw new ServerException(ServerProblem.NotAServer, string.Format(CultureInfo.CurrentCulture, Strings.NotAServer, ServerAddress.Display(url)));

    private async Task<T?> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        using var response = await RawAsync(method, path, body, cancellationToken);
        if (response.IsSuccessStatusCode)
            return response.StatusCode == HttpStatusCode.NoContent || typeof(T) == typeof(object) ? default : await ReadAsync<T>(response, cancellationToken);

        var error = await ReadAsync<ApiError>(response, cancellationToken);
        throw response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => new ServerException(ServerProblem.Revoked, string.Format(CultureInfo.CurrentCulture, Strings.Revoked, ServerAddress.Display(url))),
            HttpStatusCode.NotFound when error?.Error == "not_found" && path.StartsWith("repositories/", StringComparison.Ordinal) =>
                new ServerException(ServerProblem.NotFound, Strings.RepositoryNotFound),
            _ when error is null => new ServerException(ServerProblem.NotAServer, string.Format(CultureInfo.CurrentCulture, Strings.NotAServer, ServerAddress.Display(url))),
            _ => new ServerException(ServerProblem.Rejected, string.Format(CultureInfo.CurrentCulture, Strings.ServerSays, error.Message)),
        };
    }

    private async Task<HttpResponseMessage> RawAsync(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, new Uri(new Uri(url), "api/v1/" + path));
        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
            request.Content = JsonContent.Create(body, options: Json);
        try
        {
            return await http.Client(url, pinnedCertificate).SendAsync(request, cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw ServerHttp.Unreachable(url, exception);
        }
    }

    private async Task<T?> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<T>(Json, cancellationToken);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            return default;
        }
    }

    private sealed record ApiError(string Error, string Message);
}

// --- What the server answers ---

public sealed record PairingStart(string PairingCode, string UserCode, string VerificationUrl, string VerificationUrlComplete, DateTimeOffset ExpiresAt, int Interval);

/// <param name="State">"pending", "approved", "denied" or "expired".</param>
public sealed record PairingState(string State, string? Token, Guid? DeviceId, string? UserName);

public sealed record ServerHostKey(string Algorithm, string Fingerprint, string PublicKey);

/// <param name="State">"running", "stopped", … of the server's SSH service for borg.</param>
public sealed record ServerSsh(string Host, int Port, string User, string State, string? Message, IReadOnlyList<ServerHostKey> HostKeys);

public sealed record ServerInfo(string Name, string Version, string UserName, Guid DeviceId, ServerSsh Ssh);

public sealed record ServerDatastore(Guid Id, string Name, long? FreeBytes)
{
    public override string ToString() => Name;
}

/// <param name="MayCreate">The account may create repositories.</param>
public sealed record DatastoreList(bool MayCreate, long UsedBytes, long? QuotaBytes, IReadOnlyList<ServerDatastore> Datastores);

public sealed record ServerOwnerQuota(long UsedBytes, long QuotaBytes);

public sealed record ServerLastAccess(DateTimeOffset At, string UserName, string By);

public sealed record ServerParticipant(string UserName, string Access, bool IsOwner);

/// <param name="Access">"full" or "appendOnly".</param>
public sealed record ServerRepository(
    Guid Id,
    string Owner,
    string Name,
    string FullName,
    string Access,
    bool IsOwner,
    string Url,
    long UsedBytes,
    long? QuotaBytes,
    DateTimeOffset? UsageMeasuredAt,
    string Datastore,
    long? DatastoreFreeBytes,
    ServerOwnerQuota? OwnerQuota,
    ServerLastAccess? LastAccess,
    IReadOnlyList<ServerParticipant> Participants,
    bool IsMoving,
    bool ParticipantsHidden)
{
    public bool IsAppendOnly => Access == "appendOnly";

    /// <summary>
    /// What the repository may still grow to, in total: the smallest of its quota, what is left of the owner's quota and
    /// the datastore's free space; <c>null</c> if nothing limits it that the server knows of.
    /// </summary>
    public long? Capacity
    {
        get
        {
            long?[] limits =
            [
                QuotaBytes,
                OwnerQuota is { } owner ? UsedBytes + Math.Max(0, owner.QuotaBytes - owner.UsedBytes) : null,
                DatastoreFreeBytes is { } free ? UsedBytes + free : null,
            ];
            return limits.Where(limit => limit is > 0).Min();
        }
    }
}

/// <param name="Url">The borg address to use with the registered key.</param>
public sealed record SshKeyRegistration(string Url, ServerSsh Ssh);
