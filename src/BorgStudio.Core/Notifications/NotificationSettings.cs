using System.Text.Json.Serialization;

namespace BorgStudio.Core.Notifications;

/// <summary>What a notification is about.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<NotificationEvent>))]
public enum NotificationEvent
{
    /// <summary>A backup finished without problems.</summary>
    Succeeded,

    /// <summary>A backup finished, but borg warned (e.g. files it could not read).</summary>
    Warnings,

    /// <summary>A backup failed (also a scheduled run that could not start).</summary>
    Failed,

    /// <summary>A backup did not start (e.g. the repository asks for its passphrase every time).</summary>
    Skipped,

    /// <summary>A job works again after a failure, a skipped run or warnings were reported.</summary>
    Recovered,

    /// <summary>A repository's storage has less free space than its warning threshold.</summary>
    StorageLow,

    /// <summary>A repository's storage is full (see <see cref="Storage.StorageFullCriteria"/>).</summary>
    StorageFull,

    /// <summary>A job has not backed up for longer than its <see cref="Backups.BackupJob.OverdueDays"/>.</summary>
    Overdue,

    /// <summary>The daily summary (sent to channels with <see cref="NotificationChannel.DailySummary"/>).</summary>
    Summary,

    /// <summary>Checking a repository found damage.</summary>
    CheckFailed,
}

/// <summary>How a channel's messages reach their receiver.</summary>
public abstract record NotificationChannel : IJsonOnDeserializing, IJsonOnDeserialized
{
    /// <summary>Raised with each event added later, so channels saved before get it (see <see cref="EventsRevision"/>).</summary>
    public const int CurrentEventsRevision = 1;

    public bool Enabled { get; init; }

    /// <summary>The events this channel reports.</summary>
    public List<NotificationEvent> Events { get; init; } =
    [
        NotificationEvent.Failed, NotificationEvent.Skipped, NotificationEvent.Recovered, NotificationEvent.StorageLow,
        NotificationEvent.StorageFull, NotificationEvent.Overdue, NotificationEvent.CheckFailed,
    ];

    /// <summary>
    /// Which events existed when the channel was saved (0: before it was kept). Events added since are switched on
    /// when loading if the channel reports failures – new problems should not go unreported.
    /// </summary>
    public int EventsRevision { get; set; } = CurrentEventsRevision;

    void IJsonOnDeserializing.OnDeserializing() => EventsRevision = 0;

    void IJsonOnDeserialized.OnDeserialized()
    {
        if (EventsRevision < 1 && Events.Contains(NotificationEvent.Failed) && !Events.Contains(NotificationEvent.CheckFailed))
            Events.Add(NotificationEvent.CheckFailed);
        EventsRevision = CurrentEventsRevision;
    }

    /// <summary>Also gets the daily summary (in addition to the single messages).</summary>
    public bool DailySummary { get; init; }

    [JsonIgnore]
    public bool IsConfigured => this switch
    {
        EmailChannel email => email.Host.Trim().Length > 0 && email.From.Trim().Length > 0 && email.Recipients.Count > 0,
        WebhookChannel webhook => webhook.Url.Trim().Length > 0,
        _ => true,
    };

    public bool Reports(NotificationEvent notificationEvent) =>
        Enabled && (notificationEvent == NotificationEvent.Summary ? DailySummary : Events.Contains(notificationEvent));
}

/// <summary>The system's own notifications (Windows notification center, macOS notification center, libnotify).</summary>
public sealed record SystemNotificationChannel : NotificationChannel;

[JsonConverter(typeof(JsonStringEnumConverter<SmtpSecurity>))]
public enum SmtpSecurity
{
    /// <summary>TLS on connect for port 465, STARTTLS where the server offers it otherwise.</summary>
    Auto,

    /// <summary>STARTTLS, required.</summary>
    StartTls,

    /// <summary>TLS from the start (usually port 465).</summary>
    SslOnConnect,

    /// <summary>No encryption (only for servers in the local network).</summary>
    None,
}

/// <summary>E-mail over SMTP. The password is a secret (<see cref="NotificationSecrets.SmtpPassword"/>).</summary>
public sealed record EmailChannel : NotificationChannel
{
    public string Host { get; init; } = "";

    public int Port { get; init; } = 587;

    public SmtpSecurity Security { get; init; } = SmtpSecurity.Auto;

    /// <summary>Login name; empty: the server takes mail without login.</summary>
    public string UserName { get; init; } = "";

    public string From { get; init; } = "";

    /// <summary>One or more addresses, separated by commas or semicolons.</summary>
    public string To { get; init; } = "";

    [JsonIgnore]
    public IReadOnlyList<string> Recipients =>
        [.. To.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
}

/// <summary>What the outgoing webhook sends.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<WebhookFormat>))]
public enum WebhookFormat
{
    /// <summary>BorgStudio's own JSON (all details), for scripts and home automation.</summary>
    Json,

    /// <summary>ntfy: the URL is the topic, e.g. https://ntfy.sh/my-backups.</summary>
    Ntfy,

    /// <summary>A Discord channel webhook.</summary>
    Discord,

    /// <summary>A Slack incoming webhook.</summary>
    Slack,

    /// <summary>Gotify: the URL ends in /message; the app token goes in the header X-Gotify-Key.</summary>
    Gotify,
}

/// <summary>An HTTP POST to a URL. The header's value is a secret (<see cref="NotificationSecrets.WebhookHeader"/>).</summary>
public sealed record WebhookChannel : NotificationChannel
{
    public string Url { get; init; } = "";

    public WebhookFormat Format { get; init; } = WebhookFormat.Json;

    /// <summary>An extra header for the receiver's token, e.g. "Authorization"; empty: none.</summary>
    public string HeaderName { get; init; } = "";
}

/// <summary>Who gets told what (app-wide; a job can be left out with <see cref="Backups.BackupJob.Notify"/>).</summary>
public sealed record NotificationSettings
{
    public SystemNotificationChannel System { get; init; } = new();

    public EmailChannel Email { get; init; } = new();

    public WebhookChannel Webhook { get; init; } = new();

    /// <summary>
    /// The same problem of a job is reported once – not again after every run – until the job works again, which is
    /// then reported as <see cref="NotificationEvent.Recovered"/>.
    /// </summary>
    public bool SuppressRepeats { get; init; } = true;

    /// <summary>When a repository's storage counts as full.</summary>
    public Storage.StorageFullCriteria StorageFull { get; init; } = new();

    /// <summary>When the daily check runs: the summary, and which jobs are overdue.</summary>
    public TimeOnly DailyTime { get; init; } = new(8, 0);

    /// <summary>The summary also comes when everything went well (a sign of life); otherwise only with something to report.</summary>
    public bool SummaryWhenAllWell { get; init; } = true;

    [JsonIgnore]
    public IEnumerable<NotificationChannel> Channels => [System, Email, Webhook];

    [JsonIgnore]
    public bool AnyEnabled => Channels.Any(channel => channel.Enabled);

    /// <summary>Whether a daily check has anything to do: a channel wants the summary, or a job may become overdue.</summary>
    public bool NeedsDailyCheck(IEnumerable<Backups.BackupJob> jobs) =>
        AnyEnabled && (Channels.Any(channel => channel.Enabled && channel.DailySummary) || jobs.Any(job => job.Notify && job.OverdueDays > 0));
}
