using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BorgStudio.Core.Backups;

namespace BorgStudio.Core.Trigger;

/// <summary>Where the incoming webhook listens (app-wide; only in background mode).</summary>
public sealed record TriggerSettings
{
    public const int DefaultPort = 8473;

    public bool Enabled { get; init; }

    public int Port { get; init; } = DefaultPort;

    /// <summary>Also reachable from other computers (all network interfaces); otherwise only from this one (127.0.0.1).</summary>
    public bool AllowNetwork { get; init; }
}

/// <summary>A job the webhook was asked to start.</summary>
/// <param name="Allowed">The job may be started by the webhook (<see cref="BackupJob.WebhookTrigger"/>).</param>
public sealed record TriggerJob(Guid Id, string Name, bool Allowed);

/// <summary>A run the webhook started: right away or after the one running now; <see cref="Finished"/> completes with its outcome.</summary>
public sealed record TriggerStart(bool Queued, Task<BackupRunSummary?> Finished);

/// <summary>What the webhook can do in the app (implemented by the app; called on background threads).</summary>
public interface ITriggerHandler
{
    /// <summary>The job with this id or name (names ignore case); <c>null</c> if there is none.</summary>
    Task<TriggerJob?> FindAsync(string idOrName);

    Task<TriggerStart> RunAsync(Guid jobId);

    /// <summary>What runs, what waits, and each job's last run.</summary>
    Task<JsonObject> StatusAsync();

    /// <summary>Stops the running backup; <c>false</c> if none runs.</summary>
    Task<bool> CancelAsync();
}

/// <summary>
/// The incoming webhook: a small HTTP server (no admin rights or URL reservation needed) that starts backup jobs.
/// Every request needs the token, as <c>Authorization: Bearer &lt;token&gt;</c> or <c>X-BorgStudio-Token</c>.
/// <list type="bullet">
/// <item><c>POST /run/&lt;job id or name&gt;</c> – 202 started/queued; with <c>?wait=1</c> 200 and the outcome when done.</item>
/// <item><c>GET /status</c> – running and queued jobs, each job's last run.</item>
/// <item><c>POST /cancel</c> – stops the running backup.</item>
/// </list>
/// </summary>
public sealed class TriggerServer : IDisposable
{
    private const int MaxHeaderBytes = 16 * 1024;
    private const int MaxBodyBytes = 64 * 1024;
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan WrongTokenDelay = TimeSpan.FromMilliseconds(500);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly ITriggerHandler _handler;
    private readonly Func<string?> _token;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();

    /// <param name="token">The current token; <c>null</c>/empty: every request is refused (503).</param>
    /// <exception cref="SocketException">The port is taken or not allowed.</exception>
    public TriggerServer(ITriggerHandler handler, Func<string?> token, TriggerSettings settings)
    {
        _handler = handler;
        _token = token;
        _listener = new TcpListener(settings.AllowNetwork ? IPAddress.Any : IPAddress.Loopback, settings.Port);
        _listener.Start();
        _ = AcceptAsync();
    }

    /// <summary>The port it listens on (the one chosen by the system when 0 was asked for).</summary>
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
    }

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }
            _ = ServeAsync(client);
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var (status, body) = await HandleAsync(stream);
                await WriteAsync(stream, status, body);
            }
            catch (Exception exception) when (exception is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
            {
                // The caller went away.
            }
        }
    }

    private async Task<(int Status, JsonObject Body)> HandleAsync(NetworkStream stream)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        timeout.CancelAfter(ReadTimeout);
        var request = await ReadRequestAsync(stream, timeout.Token);
        if (request is null)
            return (400, Error("bad request"));

        if (_token() is not { Length: > 0 } token)
            return (503, Error("no token set up"));
        if (!Authorized(request.Headers, token))
        {
            await Task.Delay(WrongTokenDelay, _stop.Token);
            return (401, Error("missing or wrong token"));
        }

        var path = request.Path;
        if (path == "/status")
            return request.Method == "GET" ? (200, await _handler.StatusAsync()) : (405, Error("method not allowed"));
        if (path == "/cancel")
            return request.Method == "POST" ? (200, new JsonObject { ["cancelled"] = await _handler.CancelAsync() }) : (405, Error("method not allowed"));
        if (path.StartsWith("/run/", StringComparison.Ordinal) && path.Length > "/run/".Length)
        {
            return request.Method == "POST"
                ? await RunAsync(Uri.UnescapeDataString(path["/run/".Length..]), request.Query.GetValueOrDefault("wait") is "1" or "true")
                : (405, Error("method not allowed"));
        }
        return (404, Error("unknown path"));
    }

    private async Task<(int, JsonObject)> RunAsync(string idOrName, bool wait)
    {
        if (await _handler.FindAsync(idOrName) is not { } job)
            return (404, Error("unknown job"));
        if (!job.Allowed)
            return (403, Error("this job may not be started by the webhook"));

        var start = await _handler.RunAsync(job.Id);
        if (!wait)
            return (202, new JsonObject { ["status"] = start.Queued ? "queued" : "started", ["job"] = job.Name, ["jobId"] = job.Id.ToString() });

        var summary = await start.Finished.WaitAsync(_stop.Token);
        if (summary is null)
            return (500, Error("the job did not run"));
        return (200, new JsonObject
        {
            ["job"] = job.Name,
            ["jobId"] = job.Id.ToString(),
            ["outcome"] = summary.Outcome.ToString(),
            ["started"] = summary.Started.ToString("O"),
            ["durationSeconds"] = Math.Round(summary.Duration.TotalSeconds),
            ["archive"] = summary.ArchiveName,
            ["message"] = summary.Message,
            ["skipReason"] = summary.SkipReason?.ToString(),
        });
    }

    private static bool Authorized(IReadOnlyDictionary<string, string> headers, string token)
    {
        var given = headers.GetValueOrDefault("authorization") is { } authorization
                    && authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? authorization[7..].Trim()
            : headers.GetValueOrDefault("x-borgstudio-token")?.Trim();
        return given is not null && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), Encoding.UTF8.GetBytes(token));
    }

    private static JsonObject Error(string message) => new() { ["error"] = message };

    // --- HTTP ---

    private sealed record Request(string Method, string Path, IReadOnlyDictionary<string, string> Query, IReadOnlyDictionary<string, string> Headers);

    /// <summary>The request line and headers (the body is read and dropped); <c>null</c> if it is not HTTP or too big.</summary>
    private static async Task<Request?> ReadRequestAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[MaxHeaderBytes];
        var length = 0;
        int end;
        while ((end = HeaderEnd(buffer, length)) < 0)
        {
            if (length == buffer.Length)
                return null;
            var read = await stream.ReadAsync(buffer.AsMemory(length), cancellationToken);
            if (read == 0)
                return null;
            length += read;
        }

        var lines = Encoding.ASCII.GetString(buffer, 0, end).Split("\r\n");
        var parts = lines[0].Split(' ');
        if (parts.Length != 3 || !parts[2].StartsWith("HTTP/1.", StringComparison.Ordinal))
            return null;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon > 0)
                headers[line[..colon].Trim().ToLowerInvariant()] = line[(colon + 1)..].Trim();
        }

        // Drop a small body (nothing reads it); refuse a big one.
        if (headers.GetValueOrDefault("content-length") is { } contentLength)
        {
            if (!int.TryParse(contentLength, NumberStyles.None, CultureInfo.InvariantCulture, out var bodyLength) || bodyLength > MaxBodyBytes)
                return null;
            var remaining = bodyLength - (length - end - 4);
            var discard = new byte[4096];
            while (remaining > 0)
            {
                var read = await stream.ReadAsync(discard.AsMemory(0, Math.Min(discard.Length, remaining)), cancellationToken);
                if (read == 0)
                    break;
                remaining -= read;
            }
        }

        var target = parts[1];
        var question = target.IndexOf('?');
        var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (question >= 0)
        {
            foreach (var pair in target[(question + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var equals = pair.IndexOf('=');
                query[Uri.UnescapeDataString(equals < 0 ? pair : pair[..equals])] = equals < 0 ? "" : Uri.UnescapeDataString(pair[(equals + 1)..]);
            }
        }
        var path = (question >= 0 ? target[..question] : target).TrimEnd('/');
        return new Request(parts[0].ToUpperInvariant(), path.Length == 0 ? "/" : path, query, headers);
    }

    private static int HeaderEnd(byte[] buffer, int length)
    {
        for (var index = 0; index + 3 < length; index++)
        {
            if (buffer[index] == '\r' && buffer[index + 1] == '\n' && buffer[index + 2] == '\r' && buffer[index + 3] == '\n')
                return index;
        }
        return -1;
    }

    private static async Task WriteAsync(NetworkStream stream, int status, JsonObject body)
    {
        var content = Encoding.UTF8.GetBytes(body.ToJsonString(JsonOptions) + "\n");
        var reason = status switch
        {
            200 => "OK",
            202 => "Accepted",
            400 => "Bad Request",
            401 => "Unauthorized",
            403 => "Forbidden",
            404 => "Not Found",
            405 => "Method Not Allowed",
            503 => "Service Unavailable",
            _ => "Internal Server Error",
        };
        var head = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status} {reason}\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: {content.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(head);
        await stream.WriteAsync(content);
    }
}
