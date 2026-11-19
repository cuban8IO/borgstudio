using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BorgStudio.Core.Notifications;

/// <summary>Posts a notification to a URL – as BorgStudio's own JSON or in the form ntfy, Discord, Slack or Gotify expect.</summary>
public sealed class WebhookSender(HttpClient http)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    /// <summary>The header name a format usually wants for its token (filled in when the format is chosen).</summary>
    public static string SuggestedHeader(WebhookFormat format) => format switch
    {
        WebhookFormat.Gotify => "X-Gotify-Key",
        WebhookFormat.Ntfy or WebhookFormat.Json => "Authorization",
        _ => "",
    };

    /// <param name="headerValue">The value of the channel's extra header (its token); ignored without a header name.</param>
    /// <exception cref="NotificationException" />
    public async Task SendAsync(WebhookChannel channel, NotificationMessage message, string? headerValue,
        CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(channel.Url.Trim(), UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https"))
            throw new NotificationException("The webhook needs an http:// or https:// URL.");

        var (target, body) = Request(channel.Format, url, message);
        using var request = new HttpRequestMessage(HttpMethod.Post, target)
        {
            Content = new StringContent(body.ToJsonString(JsonOptions), Encoding.UTF8, "application/json"),
        };
        if (channel.HeaderName.Trim() is { Length: > 0 } header && !string.IsNullOrEmpty(headerValue))
        {
            if (!request.Headers.TryAddWithoutValidation(header, headerValue))
                throw new NotificationException($"The header \"{header}\" cannot be set.");
        }
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("BorgStudio", null));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new NotificationException($"{url.Host} did not answer within {Timeout.TotalSeconds:0} seconds.");
        }
        catch (HttpRequestException exception)
        {
            throw new NotificationException(exception.Message, exception);
        }

        using (response)
        {
            if (response.IsSuccessStatusCode)
                return;
            var text = (await response.Content.ReadAsStringAsync(cancellationToken)).Trim();
            if (text.Length > 300)
                text = text[..300] + "…";
            throw new NotificationException($"{(int)response.StatusCode} {response.ReasonPhrase}{(text.Length > 0 ? $": {text}" : "")}");
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    /// <summary>Where to post and what.</summary>
    internal static (Uri Target, JsonObject Body) Request(WebhookFormat format, Uri url, NotificationMessage message) => format switch
    {
        WebhookFormat.Ntfy => Ntfy(url, message),
        WebhookFormat.Discord => (url, new JsonObject
        {
            ["username"] = "BorgStudio",
            ["embeds"] = new JsonArray(new JsonObject
            {
                ["title"] = Limit(message.Title, 256),
                ["description"] = Limit(message.Body, 4000),
                ["color"] = Color(message.Event),
            }),
        }),
        WebhookFormat.Slack => (url, new JsonObject { ["text"] = Limit($"*{message.Title}*\n{message.Body}", 3000) }),
        WebhookFormat.Gotify => (url, new JsonObject
        {
            ["title"] = message.Title,
            ["message"] = message.Body,
            ["priority"] = message.Event is NotificationEvent.Failed or NotificationEvent.StorageFull ? 8 : 5,
        }),
        _ => (url, Json(message)),
    };

    /// <summary>
    /// ntfy takes JSON at its root URL, with the topic inside – so titles with umlauts need no header encoding.
    /// https://ntfy.sh/backups → POST https://ntfy.sh/ {"topic": "backups", …}.
    /// </summary>
    private static (Uri, JsonObject) Ntfy(Uri url, NotificationMessage message)
    {
        var path = url.AbsolutePath.TrimEnd('/');
        var slash = path.LastIndexOf('/');
        var topic = Uri.UnescapeDataString(path[(slash + 1)..]);
        if (topic.Length == 0)
            throw new NotificationException("The ntfy URL needs the topic at its end, e.g. https://ntfy.sh/my-backups.");
        var root = new UriBuilder(url) { Path = path[..(slash + 1)], Query = "" }.Uri;
        return (root, new JsonObject
        {
            ["topic"] = topic,
            ["title"] = message.Title,
            ["message"] = message.Body,
            ["priority"] = message.Event is NotificationEvent.Failed or NotificationEvent.StorageFull ? 4 : 3,
            ["tags"] = new JsonArray(message.Event switch
            {
                NotificationEvent.Failed or NotificationEvent.StorageFull => "x",
                NotificationEvent.Warnings or NotificationEvent.Skipped or NotificationEvent.StorageLow => "warning",
                _ => "white_check_mark",
            }),
        });
    }

    /// <summary>BorgStudio's own format: everything known about the run.</summary>
    internal static JsonObject Json(NotificationMessage message)
    {
        var body = new JsonObject
        {
            ["app"] = "BorgStudio",
            ["event"] = EventName(message.Event),
            ["test"] = message.IsTest,
            ["title"] = message.Title,
            ["message"] = message.Body,
            ["job"] = message.Job,
            ["jobId"] = message.JobId?.ToString(),
            ["repository"] = message.Repository,
            ["host"] = message.Host,
            ["time"] = message.Time.ToString("O"),
        };
        if (message.Storage is { } storage)
        {
            body["storage"] = new JsonObject
            {
                ["total"] = storage.Total,
                ["free"] = storage.Free,
                ["repositorySize"] = storage.RepositorySize,
                ["source"] = storage.Source.ToString(),
            };
        }
        if (message.Run is { } run)
        {
            body["run"] = new JsonObject
            {
                ["outcome"] = run.Outcome.ToString(),
                ["started"] = run.Started.ToString("O"),
                ["durationSeconds"] = Math.Round(run.Duration.TotalSeconds),
                ["archive"] = run.ArchiveName,
                ["originalSize"] = run.OriginalSize,
                ["deduplicatedSize"] = run.DeduplicatedSize,
                ["files"] = run.Files,
                ["message"] = run.Message,
                ["skipReason"] = run.SkipReason?.ToString(),
                ["prunedArchives"] = run.Cleanup?.PrunedArchives,
                ["cleanupError"] = run.Cleanup?.Error,
            };
        }
        return body;
    }

    /// <summary>"failed", "succeeded", … (stable names for scripts).</summary>
    public static string EventName(NotificationEvent notificationEvent) => notificationEvent switch
    {
        NotificationEvent.Succeeded => "succeeded",
        NotificationEvent.Warnings => "warnings",
        NotificationEvent.Failed => "failed",
        NotificationEvent.Skipped => "skipped",
        NotificationEvent.Recovered => "recovered",
        NotificationEvent.StorageLow => "storage-low",
        NotificationEvent.StorageFull => "storage-full",
        _ => notificationEvent.ToString().ToLowerInvariant(),
    };

    private static int Color(NotificationEvent notificationEvent) => notificationEvent switch
    {
        NotificationEvent.Failed or NotificationEvent.StorageFull => 0xD64545,
        NotificationEvent.Warnings or NotificationEvent.StorageLow => 0xFF9800,
        NotificationEvent.Skipped => 0x9C9C9C,
        _ => 0x01FF95,
    };

    private static string Limit(string text, int length) => text.Length <= length ? text : text[..(length - 1)] + "…";
}
