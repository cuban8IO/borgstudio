using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using BorgStudio.Providers.Server;

namespace BorgStudio.Tests.TestSupport;

/// <summary>
/// The interface of a BorgStudio Server (/api/v1) in memory: pairing, repositories, keys, reports. One account
/// ("alice"); its token is <see cref="Token"/> once the pairing is approved.
/// </summary>
public sealed class FakeBorgStudioServer : HttpMessageHandler
{
    public const string Url = "https://backup.test:8443/";
    public const string Token = "bss_test-token";
    public const string HostKey = "SHA256:serverhostkeyfingerprint0000000000000000000";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Guid DeviceId { get; } = Guid.NewGuid();

    public Guid DatastoreId { get; } = Guid.NewGuid();

    /// <summary>The repositories the account may use.</summary>
    public List<ServerRepository> Repositories { get; } = [];

    /// <summary>Repository id → the public key registered for this computer.</summary>
    public Dictionary<Guid, string> Keys { get; } = [];

    /// <summary>The reports received (their JSON).</summary>
    public List<JsonObject> Reports { get; } = [];

    public List<(HttpMethod Method, string Path, string? Token)> Requests { get; } = [];

    /// <summary>How the pairing ends: "approved", "denied" or "expired"; <see cref="PendingPolls"/> answers "pending" first.</summary>
    public string PairingDecision { get; set; } = "approved";

    public int PendingPolls { get; set; }

    /// <summary>The token is no longer valid (revoked on the server).</summary>
    public bool Revoked { get; set; }

    /// <summary>Nothing answers.</summary>
    public bool Unreachable { get; set; }

    public bool MayCreate { get; set; } = true;

    public bool Unpaired { get; private set; }

    /// <summary>Adds a repository on the server the account may use.</summary>
    public ServerRepository AddRepository(string name, long used = 0, long? quota = null, string access = "full", bool isOwner = true,
        bool participantsHidden = false)
    {
        var id = Guid.NewGuid();
        var repository = new ServerRepository(id, isOwner ? "alice" : "bob", name, $"{(isOwner ? "alice" : "bob")}/{name}", access, isOwner,
            SshUrl(id), used, quota, DateTimeOffset.UtcNow, "data", 400L << 30, null,
            participantsHidden ? null : new ServerLastAccess(new DateTimeOffset(2026, 11, 20, 8, 30, 0, TimeSpan.Zero), "bob", "laptop"),
            participantsHidden ? [] : [new ServerParticipant(isOwner ? "alice" : "bob", "full", true), new ServerParticipant("carol", "appendOnly", false)],
            false, participantsHidden);
        Repositories.Add(repository);
        return repository;
    }

    public static string SshUrl(Guid id) => $"ssh://borgstudio@backup.test:2222/./{id:N}";

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (Unreachable)
            throw new HttpRequestException("Connection refused");
        var path = request.RequestUri!.AbsolutePath;
        var token = request.Headers.Authorization?.Parameter;
        Requests.Add((request.Method, path, token));
        var body = request.Content is null ? null : JsonNode.Parse(await request.Content.ReadAsStringAsync(cancellationToken)) as JsonObject;
        const string api = "/api/v1/";
        if (!path.StartsWith(api, StringComparison.Ordinal))
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        path = path[api.Length..];

        if (path == "pairing" && request.Method == HttpMethod.Post)
            return Answer(new PairingStart("pairing-secret", "ABCD-EFGH", $"{Url}pair", $"{Url}pair?code=ABCD-EFGH",
                DateTimeOffset.UtcNow.AddMinutes(10), 0));
        if (path == "pairing/complete")
        {
            if (body?["pairingCode"]?.GetValue<string>() != "pairing-secret")
                return Answer(new PairingState("expired", null, null, null), HttpStatusCode.Gone);
            if (PendingPolls > 0)
            {
                PendingPolls--;
                return Answer(new PairingState("pending", null, null, null), HttpStatusCode.Accepted);
            }
            return PairingDecision switch
            {
                "approved" => Approve(),
                "denied" => Answer(new PairingState("denied", null, null, null), HttpStatusCode.Forbidden),
                _ => Answer(new PairingState("expired", null, null, null), HttpStatusCode.Gone),
            };
        }

        if (token != Token || Revoked || Unpaired)
            return Error(HttpStatusCode.Unauthorized, "unauthorized", "A valid token is needed.");

        var ssh = new ServerSsh("backup.test", 2222, "borgstudio", "running", null, [new ServerHostKey("ssh-ed25519", HostKey, "ssh-ed25519 AAAA")]);
        if (path == "server")
            return Answer(new ServerInfo("Backup Server", "0.5.0", "alice", DeviceId, ssh));
        if (path == "datastores")
            return Answer(new DatastoreList(MayCreate, Repositories.Sum(r => r.UsedBytes), 100L << 30,
                [new ServerDatastore(DatastoreId, "data", 400L << 30), new ServerDatastore(Guid.NewGuid(), "nas", 2L << 40)]));
        if (path == "device" && request.Method == HttpMethod.Delete)
        {
            Unpaired = true;
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }
        if (path == "repositories" && request.Method == HttpMethod.Get)
            return Answer(Repositories);
        if (path == "repositories" && request.Method == HttpMethod.Post)
        {
            if (!MayCreate)
                return Error((HttpStatusCode)422, "rejected", "You may not create repositories.");
            var created = AddRepository(body!["name"]!.GetValue<string>(), quota: body["quotaBytes"]?.GetValue<long?>());
            return Answer(created, HttpStatusCode.Created);
        }

        var parts = path.Split('/');
        if (parts is ["repositories", var idText, ..] && Guid.TryParse(idText, out var id))
        {
            var repository = Repositories.FirstOrDefault(r => r.Id == id);
            if (repository is null)
                return Error(HttpStatusCode.NotFound, "not_found", "The repository does not exist or this account has no access to it.");
            switch (parts)
            {
                case [_, _] when request.Method == HttpMethod.Get:
                    return Answer(repository);
                case [_, _, "ssh-key"] when request.Method == HttpMethod.Put:
                    Keys[id] = body!["publicKey"]!.GetValue<string>();
                    return Answer(new SshKeyRegistration(SshUrl(id), ssh));
                case [_, _, "reports"] when request.Method == HttpMethod.Post:
                    var kind = body!["kind"]?.GetValue<string>();
                    if (kind is not ("backup" or "check" or "prune" or "compact"))
                        return Error(HttpStatusCode.BadRequest, "invalid_request", "kind or outcome is unknown.");
                    Reports.Add(body);
                    return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
        }
        return Error(HttpStatusCode.NotFound, "not_found", "There is no such interface.");
    }

    /// <summary>A new pairing: the token works again (it stands for the new one).</summary>
    private HttpResponseMessage Approve()
    {
        Revoked = false;
        Unpaired = false;
        return Answer(new PairingState("approved", Token, DeviceId, "alice"));
    }

    private static HttpResponseMessage Answer<T>(T value, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = JsonContent.Create(value, options: Json) };

    private static HttpResponseMessage Error(HttpStatusCode status, string error, string message) =>
        Answer(new { error, message }, status);
}

/// <summary>Secrets in memory (instead of the vault); <see cref="Locked"/> makes reading fail like a locked vault.</summary>
public sealed class MemoryServerSecrets : IServerSecrets
{
    public Dictionary<string, string> Values { get; } = [];

    public bool Locked { get; set; }

    public string? Get(string name) => Locked ? null : Values.GetValueOrDefault(name);

    public void Set(string name, string value) => Values[name] = value;

    public void Remove(string name) => Values.Remove(name);
}
