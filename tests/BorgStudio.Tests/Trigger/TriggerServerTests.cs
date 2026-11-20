using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using BorgStudio.Core.Backups;
using BorgStudio.Core.Trigger;

namespace BorgStudio.Tests.Trigger;

/// <summary>The incoming webhook's HTTP side: token, routes, answers.</summary>
public sealed class TriggerServerTests : IDisposable
{
    private const string Token = "s3cret-token";

    private static readonly Guid DocumentsId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private readonly FakeHandler _handler = new();
    private readonly TriggerServer _server;
    private readonly HttpClient _http = new();
    private string? _token = Token;

    public TriggerServerTests()
    {
        _server = new TriggerServer(_handler, () => _token, new TriggerSettings { Enabled = true, Port = 0 });
        _http.BaseAddress = new Uri($"http://127.0.0.1:{_server.Port}");
    }

    public void Dispose()
    {
        _server.Dispose();
        _http.Dispose();
    }

    private async Task<(HttpStatusCode Status, JsonNode Body)> SendAsync(HttpMethod method, string path, string? token = Token, bool header = true)
    {
        using var request = new HttpRequestMessage(method, path);
        if (token is not null)
        {
            if (header)
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            else
                request.Headers.Add("X-BorgStudio-Token", token);
        }
        using var response = await _http.SendAsync(request);
        return (response.StatusCode, JsonNode.Parse(await response.Content.ReadAsStringAsync())!);
    }

    [Fact]
    public async Task Without_the_right_token_nothing_happens()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(HttpMethod.Post, "/run/Documents", token: null)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(HttpMethod.Post, "/run/Documents", token: "wrong")).Status);
        Assert.Empty(_handler.Started);

        // No token set up: refused, whatever is sent.
        _token = null;
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await SendAsync(HttpMethod.Get, "/status")).Status);
    }

    [Fact]
    public async Task A_job_is_started_by_name_or_id_and_may_be_waited_for()
    {
        var (status, body) = await SendAsync(HttpMethod.Post, "/run/documents");
        Assert.Equal(HttpStatusCode.Accepted, status);
        Assert.Equal("started", (string?)body["status"]);
        Assert.Equal("Documents", (string?)body["job"]);

        _handler.Busy = true;
        (status, body) = await SendAsync(HttpMethod.Post, $"/run/{DocumentsId}", header: false);
        Assert.Equal(HttpStatusCode.Accepted, status);
        Assert.Equal("queued", (string?)body["status"]);

        (status, body) = await SendAsync(HttpMethod.Post, "/run/Documents?wait=1");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("Warnings", (string?)body["outcome"]);
        Assert.Equal("file locked", (string?)body["message"]);
        Assert.Equal([DocumentsId, DocumentsId, DocumentsId], _handler.Started);
    }

    [Fact]
    public async Task Unknown_and_locked_jobs_and_wrong_calls_are_refused()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Post, "/run/Nothing")).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Post, "/run/Photos")).Status);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await SendAsync(HttpMethod.Get, "/run/Documents")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, "/")).Status);
        Assert.Empty(_handler.Started);
    }

    [Fact]
    public async Task Status_and_cancel_reach_the_app()
    {
        var (status, body) = await SendAsync(HttpMethod.Get, "/status");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("ok", (string?)body["state"]);

        Assert.False((bool?)(await SendAsync(HttpMethod.Post, "/cancel")).Body["cancelled"]);
        _handler.Busy = true;
        Assert.True((bool?)(await SendAsync(HttpMethod.Post, "/cancel")).Body["cancelled"]);
    }

    [Fact]
    public async Task Garbage_gets_a_bad_request_and_the_port_is_released()
    {
        using (var client = new TcpClient())
        {
            await client.ConnectAsync(IPAddress.Loopback, _server.Port);
            var stream = client.GetStream();
            await stream.WriteAsync("NONSENSE\r\n\r\n"u8.ToArray());
            var answer = await new StreamReader(stream).ReadToEndAsync();
            Assert.StartsWith("HTTP/1.1 400", answer);
        }

        var port = _server.Port;
        _server.Dispose();
        using var again = new TriggerServer(_handler, () => Token, new TriggerSettings { Port = port });
        Assert.Equal(port, again.Port);
    }

    private sealed class FakeHandler : ITriggerHandler
    {
        public List<Guid> Started { get; } = [];

        public bool Busy { get; set; }

        public Task<TriggerJob?> FindAsync(string idOrName) => Task.FromResult<TriggerJob?>(idOrName.ToLowerInvariant() switch
        {
            "documents" or "11111111-1111-1111-1111-111111111111" => new TriggerJob(DocumentsId, "Documents", true),
            "photos" => new TriggerJob(Guid.NewGuid(), "Photos", false),
            _ => null,
        });

        public Task<TriggerStart> RunAsync(Guid jobId)
        {
            Started.Add(jobId);
            var summary = new BackupRunSummary
            {
                Started = DateTimeOffset.Now,
                Duration = TimeSpan.FromSeconds(3),
                Outcome = BackupOutcome.Warnings,
                Message = "file locked",
            };
            return Task.FromResult(new TriggerStart(Busy, Task.FromResult<BackupRunSummary?>(summary)));
        }

        public Task<JsonObject> StatusAsync() => Task.FromResult(new JsonObject { ["state"] = "ok" });

        public Task<bool> CancelAsync() => Task.FromResult(Busy);
    }
}
