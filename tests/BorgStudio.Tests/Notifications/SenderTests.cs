using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using BorgStudio.Core.Backups;
using BorgStudio.Core.Notifications;
using BorgStudio.Core.Processes;
using BorgStudio.Tests.TestSupport;

namespace BorgStudio.Tests.Notifications;

/// <summary>The three channels: what reaches the receiver, and how failures come back.</summary>
public sealed class SenderTests
{
    private static NotificationMessage Message(NotificationEvent notificationEvent = NotificationEvent.Failed) =>
        new(notificationEvent, "Backup failed: Docs", "Reason: disk full\nComputer: PC")
        {
            Job = "Docs",
            JobId = Guid.Parse("11111111-2222-3333-4444-555555555555"),
            Repository = "Box",
            Host = "PC",
            Run = new BackupRunSummary
            {
                Started = DateTimeOffset.Parse("2026-11-18T20:00:00+01:00"),
                Duration = TimeSpan.FromSeconds(90),
                Outcome = BackupOutcome.Failed,
                Message = "disk full",
            },
        };

    // --- Webhook ---

    private static async Task<FakeHttpHandler.Request> PostAsync(WebhookChannel channel, string? token = null,
        NotificationEvent notificationEvent = NotificationEvent.Failed)
    {
        var http = new FakeHttpHandler();
        await new WebhookSender(new HttpClient(http)).SendAsync(channel, Message(notificationEvent), token);
        return Assert.Single(http.Requests);
    }

    [Fact]
    public async Task The_json_webhook_carries_the_event_and_the_run()
    {
        var request = await PostAsync(new WebhookChannel { Url = "https://example.com/hook" });

        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://example.com/hook", request.Url.ToString());
        var body = JsonNode.Parse(request.Body)!;
        Assert.Equal("failed", (string?)body["event"]);
        Assert.Equal("Docs", (string?)body["job"]);
        Assert.Equal("11111111-2222-3333-4444-555555555555", (string?)body["jobId"]);
        Assert.Equal("Box", (string?)body["repository"]);
        Assert.Equal("Failed", (string?)body["run"]!["outcome"]);
        Assert.Equal(90, (double?)body["run"]!["durationSeconds"]);
        Assert.False((bool?)body["test"]);
    }

    [Fact]
    public async Task Ntfy_gets_the_topic_in_json_at_its_root()
    {
        var request = await PostAsync(new WebhookChannel { Url = "https://ntfy.example.com/my-backups", Format = WebhookFormat.Ntfy },
            "Bearer tk_1", NotificationEvent.Failed);

        Assert.Equal("https://ntfy.example.com/", request.Url.ToString());
        var body = JsonNode.Parse(request.Body)!;
        Assert.Equal("my-backups", (string?)body["topic"]);
        Assert.Equal("Backup failed: Docs", (string?)body["title"]);
        Assert.Equal(4, (int?)body["priority"]);
        // No header name: no token sent.
        Assert.False(request.Headers.ContainsKey("Authorization"));
    }

    [Fact]
    public async Task Gotify_gets_its_token_in_the_header()
    {
        var request = await PostAsync(new WebhookChannel
        {
            Url = "https://gotify.example.com/message",
            Format = WebhookFormat.Gotify,
            HeaderName = WebhookSender.SuggestedHeader(WebhookFormat.Gotify),
        }, "app-token", NotificationEvent.Succeeded);

        Assert.Equal("app-token", request.Headers["X-Gotify-Key"]);
        var body = JsonNode.Parse(request.Body)!;
        Assert.Equal("Reason: disk full\nComputer: PC", (string?)body["message"]);
        Assert.Equal(5, (int?)body["priority"]);
    }

    [Fact]
    public async Task Discord_and_slack_get_their_own_shape()
    {
        var discord = JsonNode.Parse((await PostAsync(new WebhookChannel { Url = "https://discord.example/api/webhooks/1/x", Format = WebhookFormat.Discord })).Body)!;
        Assert.Equal("Backup failed: Docs", (string?)discord["embeds"]![0]!["title"]);
        Assert.Equal(0xD64545, (int?)discord["embeds"]![0]!["color"]);

        var slack = JsonNode.Parse((await PostAsync(new WebhookChannel { Url = "https://hooks.slack.example/services/x", Format = WebhookFormat.Slack })).Body)!;
        Assert.StartsWith("*Backup failed: Docs*\n", (string?)slack["text"]);
    }

    [Fact]
    public async Task A_refused_webhook_says_why()
    {
        var http = new FakeHttpHandler { Status = HttpStatusCode.Unauthorized, ResponseBody = "bad token" };
        var sender = new WebhookSender(new HttpClient(http));

        var error = await Assert.ThrowsAsync<NotificationException>(() =>
            sender.SendAsync(new WebhookChannel { Url = "https://example.com/hook" }, Message(), null));
        Assert.Contains("401", error.Message);
        Assert.Contains("bad token", error.Message);

        await Assert.ThrowsAsync<NotificationException>(() => sender.SendAsync(new WebhookChannel { Url = "ftp://x" }, Message(), null));
        await Assert.ThrowsAsync<NotificationException>(() =>
            sender.SendAsync(new WebhookChannel { Url = "https://ntfy.sh/", Format = WebhookFormat.Ntfy }, Message(), null));
    }

    // --- System notification ---

    [Fact]
    public async Task Linux_uses_notify_send_and_falls_back_to_gdbus()
    {
        var processes = new RecordingProcesses();
        var notifier = new SystemNotifier(processes, "/data/icon.png") { Platform = SystemNotifier.OSPlatform.Linux };

        await notifier.SendAsync(Message());
        var call = Assert.Single(processes.Calls);
        Assert.Equal("notify-send", call.FileName);
        Assert.Equal(["--app-name=BorgStudio", "--icon=/data/icon.png", "--urgency=critical", "--", "Backup failed: Docs",
            "Reason: disk full\nComputer: PC"], call.Arguments);

        processes.Calls.Clear();
        processes.Results["notify-send"] = null;
        await notifier.SendAsync(Message(NotificationEvent.Succeeded) with { Title = "Say \"hi\"" });
        Assert.Equal("gdbus", processes.Calls[^1].FileName);
        Assert.Contains("\"Say \\\"hi\\\"\"", processes.Calls[^1].Arguments);

        processes.Results["gdbus"] = null;
        await Assert.ThrowsAsync<NotificationException>(() => notifier.SendAsync(Message()));
    }

    [Fact]
    public async Task MacOS_passes_the_texts_as_arguments()
    {
        var processes = new RecordingProcesses();
        await new SystemNotifier(processes) { Platform = SystemNotifier.OSPlatform.OSX }.SendAsync(Message());

        var call = Assert.Single(processes.Calls);
        Assert.Equal("osascript", call.FileName);
        Assert.Equal(["Backup failed: Docs", "Reason: disk full\nComputer: PC"], call.Arguments.TakeLast(2));
    }

    [Fact]
    public async Task Windows_shows_a_toast_through_powershell()
    {
        var processes = new RecordingProcesses();
        var notifier = new SystemNotifier(processes) { Platform = SystemNotifier.OSPlatform.Windows, RegisterName = false };
        await notifier.SendAsync(Message() with { Title = "A <b> & c" });

        var call = Assert.Single(processes.Calls);
        Assert.EndsWith("powershell.exe", call.FileName);
        var script = Encoding.Unicode.GetString(Convert.FromBase64String(call.Arguments[^1]));
        Assert.Equal(SystemNotifier.ToastScript, script);
        Assert.Equal(SystemNotifier.AppUserModelId, call.Environment["BORGSTUDIO_AUMID"]);
        // The texts are XML-escaped.
        Assert.Contains("<text>A &lt;b&gt; &amp; c</text>", call.Environment["BORGSTUDIO_TOAST"]);

        processes.Results["powershell.exe"] = new ProcessResult(1, "", "Access denied");
        var error = await Assert.ThrowsAsync<NotificationException>(() => notifier.SendAsync(Message()));
        Assert.Contains("Access denied", error.Message);
    }

    // --- E-mail ---

    [Fact]
    public async Task An_email_reaches_the_smtp_server_with_login()
    {
        using var server = new FakeSmtpServer();
        var channel = new EmailChannel
        {
            Host = "127.0.0.1",
            Port = server.Port,
            Security = SmtpSecurity.None,
            UserName = "backup",
            From = "BorgStudio <backup@example.com>",
            To = "me@example.com; admin@example.com",
        };

        await new EmailSender().SendAsync(channel, Message(), "secret");

        Assert.Equal("backup\0secret", server.Login);
        Assert.Equal(["<me@example.com>", "<admin@example.com>"], server.Recipients);
        Assert.Contains("Subject: [BorgStudio] Backup failed: Docs", server.Data);
        Assert.Contains("Reason: disk full", server.Data);
    }

    [Fact]
    public async Task A_missing_server_or_address_is_reported()
    {
        var sender = new EmailSender();
        await Assert.ThrowsAsync<NotificationException>(() => sender.SendAsync(new EmailChannel { From = "a@b.c", To = "d@e.f" }, Message(), null));
        await Assert.ThrowsAsync<NotificationException>(() => sender.SendAsync(new EmailChannel { Host = "x", From = "a@b.c" }, Message(), null));

        // Nobody listens on the port.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        await Assert.ThrowsAsync<NotificationException>(() => sender.SendAsync(
            new EmailChannel { Host = "127.0.0.1", Port = port, Security = SmtpSecurity.None, From = "a@b.c", To = "d@e.f" }, Message(), null));
    }

    /// <summary>Just enough SMTP for one mail (EHLO, AUTH PLAIN, MAIL, RCPT, DATA, QUIT).</summary>
    private sealed class FakeSmtpServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly Task _session;

        public FakeSmtpServer()
        {
            _listener.Start();
            _session = Task.Run(ServeAsync);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public string? Login { get; private set; }

        public List<string> Recipients { get; } = [];

        public string Data { get; private set; } = "";

        private async Task ServeAsync()
        {
            using var client = await _listener.AcceptTcpClientAsync();
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII);
            await using var writer = new StreamWriter(stream, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
            await writer.WriteLineAsync("220 localhost ESMTP");
            while (await reader.ReadLineAsync() is { } line)
            {
                var command = line.Split(' ')[0].ToUpperInvariant();
                switch (command)
                {
                    case "EHLO":
                        await writer.WriteLineAsync("250-localhost");
                        await writer.WriteLineAsync("250 AUTH PLAIN");
                        break;
                    case "AUTH":
                        Login = Encoding.UTF8.GetString(Convert.FromBase64String(line.Split(' ')[2]))[1..];
                        await writer.WriteLineAsync("235 OK");
                        break;
                    case "RCPT":
                        Recipients.Add(line[(line.IndexOf(':') + 1)..].Trim());
                        await writer.WriteLineAsync("250 OK");
                        break;
                    case "DATA":
                        await writer.WriteLineAsync("354 Go ahead");
                        var data = new StringBuilder();
                        while (await reader.ReadLineAsync() is { } dataLine && dataLine != ".")
                            data.AppendLine(dataLine);
                        Data = data.ToString();
                        await writer.WriteLineAsync("250 OK");
                        break;
                    case "QUIT":
                        await writer.WriteLineAsync("221 Bye");
                        return;
                    default:
                        await writer.WriteLineAsync("250 OK");
                        break;
                }
            }
        }

        public void Dispose()
        {
            _listener.Stop();
            try
            {
                _session.Wait(TimeSpan.FromSeconds(5));
            }
            catch (AggregateException)
            {
                // The session ended with the listener.
            }
        }
    }
}
