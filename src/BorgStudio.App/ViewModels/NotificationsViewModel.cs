using System.Collections.ObjectModel;
using System.Globalization;
using BorgStudio.App.Resources;
using BorgStudio.App.Services;
using BorgStudio.Core.Notifications;
using BorgStudio.Core.Secrets;
using BorgStudio.Core.Security;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BorgStudio.App.ViewModels;

/// <summary>An event a channel may report, as a check box.</summary>
public sealed partial class NotificationEventOption(NotificationEvent notificationEvent, string label, bool isChecked) : ObservableObject
{
    public NotificationEvent Event { get; } = notificationEvent;

    public string Label { get; } = label;

    [ObservableProperty]
    public partial bool IsChecked { get; set; } = isChecked;
}

/// <summary>
/// Extras → Notifications: the three channels (system notification, e-mail, webhook), which events each reports, a test
/// per channel, and leaving out repeated problems. The SMTP password and the webhook's token are kept like passphrases.
/// </summary>
public sealed partial class NotificationsViewModel : ViewModelBase
{
    private readonly MainViewModel _main;
    private readonly NotificationSecrets _secrets;
    private string _suggestedHeader;

    public NotificationsViewModel(MainViewModel main)
    {
        _main = main;
        var services = main.Services;
        _secrets = new NotificationSecrets(services.Vault, services.SecretStore);
        var settings = main.Settings.Notifications;

        SystemEnabled = settings.System.Enabled;
        SystemEvents = Events(settings.System);
        SystemDailySummary = settings.System.DailySummary;

        var email = settings.Email;
        EmailEnabled = email.Enabled;
        EmailEvents = Events(email);
        EmailDailySummary = email.DailySummary;
        EmailHost = email.Host;
        EmailPort = email.Port.ToString(CultureInfo.InvariantCulture);
        EmailSecurity = SecurityOptions.First(option => option.Value == email.Security);
        EmailUserName = email.UserName;
        EmailFrom = email.From;
        EmailTo = email.To;
        EmailPassword = ReadSecret(NotificationSecrets.SmtpPassword);

        var webhook = settings.Webhook;
        WebhookEnabled = webhook.Enabled;
        WebhookEvents = Events(webhook);
        WebhookDailySummary = webhook.DailySummary;
        WebhookUrl = webhook.Url;
        WebhookFormat = FormatOptions.First(option => option.Value == webhook.Format);
        _suggestedHeader = WebhookSender.SuggestedHeader(webhook.Format);
        WebhookHeaderName = webhook.HeaderName;
        WebhookHeaderValue = ReadSecret(NotificationSecrets.WebhookHeader);

        SuppressRepeats = settings.SuppressRepeats;
        FullBelowOnePercent = settings.StorageFull.BelowOnePercent;
        FullNextBackupWontFit = settings.StorageFull.NextBackupWontFit;
        FullBackupFailed = settings.StorageFull.BackupFailed;
        DailyTime = settings.DailyTime.ToTimeSpan();
        SummaryWhenAllWell = settings.SummaryWhenAllWell;
    }

    /// <summary>The window closes (after saving, or cancelled).</summary>
    public event EventHandler? CloseRequested;

    /// <summary>Where the password and token are kept – or that nothing can keep them.</summary>
    public string SecretsHint => !_secrets.CanStore ? Strings.NotifySecretsNowhere
        : _secrets.UsesVault ? Strings.NotifySecretsVault
        : Strings.NotifySecretsKeychain;

    public bool CanStoreSecrets => _secrets.CanStore;

    // --- System notification ---

    [ObservableProperty]
    public partial bool SystemEnabled { get; set; }

    public IReadOnlyList<NotificationEventOption> SystemEvents { get; }

    [ObservableProperty]
    public partial string? SystemTestResult { get; private set; }

    // --- E-mail ---

    [ObservableProperty]
    public partial bool EmailEnabled { get; set; }

    public IReadOnlyList<NotificationEventOption> EmailEvents { get; }

    [ObservableProperty]
    public partial string EmailHost { get; set; } = "";

    [ObservableProperty]
    public partial string EmailPort { get; set; } = "587";

    public IReadOnlyList<Option<SmtpSecurity>> SecurityOptions { get; } =
    [
        new(SmtpSecurity.Auto, Strings.NotifySecurityAuto),
        new(SmtpSecurity.StartTls, "STARTTLS"),
        new(SmtpSecurity.SslOnConnect, "SSL/TLS"),
        new(SmtpSecurity.None, Strings.NotifySecurityNone),
    ];

    [ObservableProperty]
    public partial Option<SmtpSecurity> EmailSecurity { get; set; }

    [ObservableProperty]
    public partial string EmailUserName { get; set; } = "";

    [ObservableProperty]
    public partial string EmailPassword { get; set; } = "";

    [ObservableProperty]
    public partial string EmailFrom { get; set; } = "";

    [ObservableProperty]
    public partial string EmailTo { get; set; } = "";

    [ObservableProperty]
    public partial string? EmailTestResult { get; private set; }

    // --- Webhook ---

    [ObservableProperty]
    public partial bool WebhookEnabled { get; set; }

    public IReadOnlyList<NotificationEventOption> WebhookEvents { get; }

    [ObservableProperty]
    public partial string WebhookUrl { get; set; } = "";

    public IReadOnlyList<Option<WebhookFormat>> FormatOptions { get; } =
    [
        new(Core.Notifications.WebhookFormat.Json, Strings.NotifyFormatJson),
        new(Core.Notifications.WebhookFormat.Ntfy, "ntfy"),
        new(Core.Notifications.WebhookFormat.Discord, "Discord"),
        new(Core.Notifications.WebhookFormat.Slack, "Slack"),
        new(Core.Notifications.WebhookFormat.Gotify, "Gotify"),
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WebhookUrlHint))]
    public partial Option<WebhookFormat> WebhookFormat { get; set; }

    /// <summary>What the URL looks like for the chosen format.</summary>
    public string WebhookUrlHint => WebhookFormat.Value switch
    {
        Core.Notifications.WebhookFormat.Ntfy => Strings.NotifyUrlHintNtfy,
        Core.Notifications.WebhookFormat.Discord => Strings.NotifyUrlHintDiscord,
        Core.Notifications.WebhookFormat.Slack => Strings.NotifyUrlHintSlack,
        Core.Notifications.WebhookFormat.Gotify => Strings.NotifyUrlHintGotify,
        _ => Strings.NotifyUrlHintJson,
    };

    [ObservableProperty]
    public partial string WebhookHeaderName { get; set; } = "";

    [ObservableProperty]
    public partial string WebhookHeaderValue { get; set; } = "";

    [ObservableProperty]
    public partial string? WebhookTestResult { get; private set; }

    partial void OnWebhookFormatChanged(Option<WebhookFormat> value)
    {
        // The header the format wants – unless the user typed another one.
        var suggestion = WebhookSender.SuggestedHeader(value.Value);
        if (string.IsNullOrWhiteSpace(WebhookHeaderName) || WebhookHeaderName == _suggestedHeader)
            WebhookHeaderName = suggestion;
        _suggestedHeader = suggestion;
    }

    // --- General ---

    [ObservableProperty]
    public partial bool SuppressRepeats { get; set; }

    /// <summary>Storage counts as full below 1 % free.</summary>
    [ObservableProperty]
    public partial bool FullBelowOnePercent { get; set; }

    /// <summary>… or with less free than the last backups added.</summary>
    [ObservableProperty]
    public partial bool FullNextBackupWontFit { get; set; }

    /// <summary>… or after a backup failed for lack of space.</summary>
    [ObservableProperty]
    public partial bool FullBackupFailed { get; set; }

    [ObservableProperty]
    public partial bool SystemDailySummary { get; set; }

    [ObservableProperty]
    public partial bool EmailDailySummary { get; set; }

    [ObservableProperty]
    public partial bool WebhookDailySummary { get; set; }

    /// <summary>When the daily check runs (summary, overdue jobs).</summary>
    [ObservableProperty]
    public partial TimeSpan? DailyTime { get; set; }

    /// <summary>The summary also comes when everything went well.</summary>
    [ObservableProperty]
    public partial bool SummaryWhenAllWell { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestSystemCommand), nameof(TestEmailCommand), nameof(TestWebhookCommand), nameof(SaveCommand))]
    public partial bool IsTesting { get; private set; }

    public ObservableCollection<string> Errors { get; } = [];

    public bool HasErrors => Errors.Count > 0;

    /// <summary>The settings as they are in the window now.</summary>
    public NotificationSettings Current => new()
    {
        System = new SystemNotificationChannel { Enabled = SystemEnabled, Events = Checked(SystemEvents), DailySummary = SystemDailySummary },
        Email = new EmailChannel
        {
            Enabled = EmailEnabled,
            Events = Checked(EmailEvents),
            DailySummary = EmailDailySummary,
            Host = EmailHost.Trim(),
            Port = int.TryParse(EmailPort.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var port) ? port : 0,
            Security = EmailSecurity.Value,
            UserName = EmailUserName.Trim(),
            From = EmailFrom.Trim(),
            To = EmailTo.Trim(),
        },
        Webhook = new WebhookChannel
        {
            Enabled = WebhookEnabled,
            Events = Checked(WebhookEvents),
            DailySummary = WebhookDailySummary,
            Url = WebhookUrl.Trim(),
            Format = WebhookFormat.Value,
            HeaderName = WebhookHeaderName.Trim(),
        },
        SuppressRepeats = SuppressRepeats,
        DailyTime = TimeOnly.FromTimeSpan(DailyTime ?? new TimeSpan(8, 0, 0)),
        SummaryWhenAllWell = SummaryWhenAllWell,
        StorageFull = new Core.Storage.StorageFullCriteria
        {
            BelowOnePercent = FullBelowOnePercent,
            NextBackupWontFit = FullNextBackupWontFit,
            BackupFailed = FullBackupFailed,
        },
    };

    [RelayCommand(CanExecute = nameof(CanTest))]
    private async Task TestSystemAsync()
    {
        SystemTestResult = Strings.NotifyTesting;
        SystemTestResult = await TestAsync(Current.System, Strings.NotifyChannelSystem, null);
    }

    [RelayCommand(CanExecute = nameof(CanTest))]
    private async Task TestEmailAsync()
    {
        var email = Current.Email;
        if (ProblemsOf(email) is [var problem, ..])
        {
            EmailTestResult = problem;
            return;
        }
        EmailTestResult = Strings.NotifyTesting;
        EmailTestResult = await TestAsync(email, Strings.NotifyChannelEmail, EmailPassword);
    }

    [RelayCommand(CanExecute = nameof(CanTest))]
    private async Task TestWebhookAsync()
    {
        var webhook = Current.Webhook;
        if (ProblemsOf(webhook) is [var problem, ..])
        {
            WebhookTestResult = problem;
            return;
        }
        WebhookTestResult = Strings.NotifyTesting;
        WebhookTestResult = await TestAsync(webhook, Strings.NotifyChannelWebhook, WebhookHeaderValue);
    }

    private bool CanTest() => !IsTesting;

    private async Task<string> TestAsync(NotificationChannel channel, string channelName, string? secret)
    {
        if (_main.Services.Notifier is not { } notifier)
            return Strings.NotifyUnavailable;
        IsTesting = true;
        try
        {
            var message = RunNotifications.TestMessage(channelName);
            var result = await Task.Run(() => notifier.TestAsync(channel, message, string.IsNullOrEmpty(secret) ? null : secret));
            return result.Error is { } error ? BorgTexts.Format(Strings.NotifyTestFailed, error) : Strings.NotifyTestSent;
        }
        finally
        {
            IsTesting = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanTest))]
    private void Save()
    {
        Errors.Clear();
        var settings = Current;
        if (settings.Email.Enabled)
            foreach (var problem in ProblemsOf(settings.Email))
                Errors.Add(BorgTexts.Format(Strings.NotifyErrorIn, Strings.NotifyChannelEmail, problem));
        if (settings.Webhook.Enabled)
            foreach (var problem in ProblemsOf(settings.Webhook))
                Errors.Add(BorgTexts.Format(Strings.NotifyErrorIn, Strings.NotifyChannelWebhook, problem));

        if (Errors.Count == 0)
        {
            try
            {
                StoreSecret(NotificationSecrets.SmtpPassword, settings.Email.UserName.Length > 0 ? EmailPassword : null);
                StoreSecret(NotificationSecrets.WebhookHeader, settings.Webhook.HeaderName.Length > 0 ? WebhookHeaderValue : null);
            }
            catch (Exception exception) when (exception is SecretStoreException or VaultLockedException)
            {
                Errors.Add(BorgTexts.Format(Strings.NotifySecretFailed, exception.Message));
            }
        }

        OnPropertyChanged(nameof(HasErrors));
        if (Errors.Count > 0)
            return;

        _main.UpdateSettings(_main.Settings with { Notifications = settings });
        // The daily check may have been switched on or off, or moved.
        _main.ApplySchedules();
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>What is missing for the channel to work (empty: nothing).</summary>
    private static List<string> ProblemsOf(NotificationChannel channel)
    {
        var problems = new List<string>();
        switch (channel)
        {
            case EmailChannel email:
                if (email.Host.Length == 0)
                    problems.Add(Strings.NotifyErrorHost);
                if (email.Port is < 1 or > 65535)
                    problems.Add(Strings.NotifyErrorPort);
                if (email.From.Length == 0)
                    problems.Add(Strings.NotifyErrorFrom);
                if (email.Recipients.Count == 0)
                    problems.Add(Strings.NotifyErrorTo);
                break;
            case WebhookChannel webhook:
                if (!Uri.TryCreate(webhook.Url, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https"))
                    problems.Add(Strings.NotifyErrorUrl);
                break;
        }
        return problems;
    }

    private void StoreSecret(string name, string? value)
    {
        // Unchanged: nothing to write (and no vault needed).
        if ((ReadSecret(name) is { Length: > 0 } stored ? stored : null) == (string.IsNullOrEmpty(value) ? null : value))
            return;
        _secrets.Set(name, value);
    }

    private string ReadSecret(string name) => _secrets.Get(name) ?? "";

    private static List<NotificationEvent> Checked(IEnumerable<NotificationEventOption> options) =>
        [.. options.Where(option => option.IsChecked).Select(option => option.Event)];

    private static IReadOnlyList<NotificationEventOption> Events(NotificationChannel channel) =>
    [
        new(NotificationEvent.Failed, Strings.NotifyEventFailed, channel.Events.Contains(NotificationEvent.Failed)),
        new(NotificationEvent.Skipped, Strings.NotifyEventSkipped, channel.Events.Contains(NotificationEvent.Skipped)),
        new(NotificationEvent.Warnings, Strings.NotifyEventWarnings, channel.Events.Contains(NotificationEvent.Warnings)),
        new(NotificationEvent.Recovered, Strings.NotifyEventRecovered, channel.Events.Contains(NotificationEvent.Recovered)),
        new(NotificationEvent.Succeeded, Strings.NotifyEventSucceeded, channel.Events.Contains(NotificationEvent.Succeeded)),
        new(NotificationEvent.StorageLow, Strings.NotifyEventStorageLow, channel.Events.Contains(NotificationEvent.StorageLow)),
        new(NotificationEvent.StorageFull, Strings.NotifyEventStorageFull, channel.Events.Contains(NotificationEvent.StorageFull)),
        new(NotificationEvent.Overdue, Strings.NotifyEventOverdue, channel.Events.Contains(NotificationEvent.Overdue)),
        new(NotificationEvent.CheckFailed, Strings.NotifyEventCheckFailed, channel.Events.Contains(NotificationEvent.CheckFailed)),
    ];
}
