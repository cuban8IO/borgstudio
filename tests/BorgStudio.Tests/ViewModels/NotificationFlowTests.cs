using System.Text.Json.Nodes;
using BorgStudio.App.Resources;
using BorgStudio.App.Services;
using BorgStudio.App.ViewModels;
using BorgStudio.Core.Backups;
using BorgStudio.Core.Notifications;
using BorgStudio.Core.Repositories;
using BorgStudio.Core.Settings;
using BorgStudio.Tests.TestSupport;

namespace BorgStudio.Tests.ViewModels;

/// <summary>Notifications after runs – by hand and scheduled – and the window that sets them up.</summary>
public sealed class NotificationFlowTests : IDisposable
{
    private const string Pin = "123456";

    private readonly TestServices _test = new();
    private readonly RepositoryConfig _repository;

    public NotificationFlowTests()
    {
        _repository = new RepositoryConfig
        {
            Id = Guid.NewGuid(),
            Name = "Disk",
            ProviderId = "local",
            ProviderValues = new Dictionary<string, string> { ["path"] = RepositoryFolder },
            PassphraseMode = PassphraseMode.None,
            EncryptionMode = "none",
        };
        Directory.CreateDirectory(Source);
    }

    public void Dispose() => _test.Dispose();

    private string Source => _test.Directory.Combine("Documents");

    private string RepositoryFolder => _test.Directory.Combine("repo");

    private BackupJob SaveJob(bool notify = true)
    {
        var job = new BackupJob
        {
            Id = Guid.NewGuid(),
            Name = "Documents",
            RepositoryId = _repository.Id,
            Sources = [Source],
            Exclusions = new BackupExclusions { CachesAndTemp = false },
            Notify = notify,
        };
        _test.Store.Save([_repository]);
        _test.Jobs.Save([job]);
        return job;
    }

    private void Configure(NotificationSettings notifications) => _test.Settings.Save(new AppSettings { Notifications = notifications });

    private static readonly List<NotificationEvent> AllEvents =
        [NotificationEvent.Succeeded, NotificationEvent.Warnings, NotificationEvent.Failed, NotificationEvent.Skipped, NotificationEvent.Recovered];

    private static NotificationSettings WebhookOnly(bool suppressRepeats = true, string headerName = "") => new()
    {
        Webhook = new WebhookChannel { Enabled = true, Events = AllEvents, Url = "https://example.com/hook", HeaderName = headerName },
        SuppressRepeats = suppressRepeats,
    };

    private List<string?> PostedEvents => [.. _test.Http.Requests.Select(request => (string?)JsonNode.Parse(request.Body)!["event"])];

    private ScheduledJobRunner NewRunner() => new(_test.Services, MainViewModel.CreateScheduledAccess(_test.Services));

    // --- Runs started by hand ---

    [Fact]
    public async Task A_backup_by_hand_is_reported_through_the_system()
    {
        Directory.CreateDirectory(RepositoryFolder);
        SaveJob();
        Configure(new NotificationSettings { System = new SystemNotificationChannel { Enabled = true, Events = AllEvents } });
        var main = new MainViewModel(_test.Services);
        await main.CheckBorgCommand.ExecuteAsync(null);

        await main.RunJobCommand.ExecuteAsync(null);

        var call = Assert.Single(_test.NotifyProcesses.Calls);
        Assert.Equal("notify-send", call.FileName);
        Assert.Contains(RunNotifications.Title(NotificationEvent.Succeeded, "Documents"), call.Arguments);
        Assert.Contains(call.Arguments, argument => argument.Contains(BorgTexts.Format(Strings.NotifyLineRepository, "Disk")));
    }

    [Fact]
    public async Task Events_a_channel_does_not_report_and_jobs_left_out_stay_quiet()
    {
        Directory.CreateDirectory(RepositoryFolder);
        SaveJob(notify: false);
        Configure(new NotificationSettings { System = new SystemNotificationChannel { Enabled = true, Events = AllEvents } });
        var main = new MainViewModel(_test.Services);
        await main.CheckBorgCommand.ExecuteAsync(null);
        await main.RunJobCommand.ExecuteAsync(null);
        Assert.Empty(_test.NotifyProcesses.Calls);

        // The default events: problems only.
        SaveJob();
        Configure(new NotificationSettings { System = new SystemNotificationChannel { Enabled = true } });
        main = new MainViewModel(_test.Services);
        await main.CheckBorgCommand.ExecuteAsync(null);
        await main.RunJobCommand.ExecuteAsync(null);
        Assert.Empty(_test.NotifyProcesses.Calls);

        _test.Borg.CreateWarnings.Add("file1: Permission denied");
        await main.RunJobCommand.ExecuteAsync(null);
        Assert.Empty(_test.NotifyProcesses.Calls);
    }

    // --- Scheduled runs ---

    [Fact]
    public async Task A_scheduled_problem_is_reported_once_until_the_job_works_again()
    {
        var job = SaveJob();
        Configure(WebhookOnly());
        var runner = NewRunner();

        // No disk: failed – twice, reported once.
        await runner.RunAsync(job.Id);
        await runner.RunAsync(job.Id);
        Assert.Equal(["failed"], PostedEvents);
        Assert.Equal("Documents", (string?)JsonNode.Parse(_test.Http.Requests[0].Body)!["job"]);

        Directory.CreateDirectory(RepositoryFolder);
        await runner.RunAsync(job.Id);
        await runner.RunAsync(job.Id);
        Assert.Equal(["failed", "recovered", "succeeded"], PostedEvents);

        // Without leaving out repeats, every failure is reported.
        Directory.Delete(RepositoryFolder);
        Configure(WebhookOnly(suppressRepeats: false));
        await runner.RunAsync(job.Id);
        await runner.RunAsync(job.Id);
        Assert.Equal(["failed", "recovered", "succeeded", "failed", "failed"], PostedEvents);
    }

    [Fact]
    public async Task A_sending_problem_does_not_fail_the_run_and_is_logged()
    {
        Directory.CreateDirectory(RepositoryFolder);
        var job = SaveJob();
        Configure(WebhookOnly());
        _test.Http.Status = System.Net.HttpStatusCode.InternalServerError;

        var result = await NewRunner().RunAsync(job.Id);

        Assert.Equal(BackupOutcome.Succeeded, result.Summary.Outcome);
        var log = File.ReadAllText(_test.Directory.Combine("logs", RunNotifications.LogFile));
        Assert.Contains("500", log);
    }

    [Fact]
    public async Task Scheduled_runs_read_the_token_from_the_vault_with_their_key()
    {
        Directory.CreateDirectory(RepositoryFolder);
        var job = SaveJob();
        Configure(WebhookOnly(headerName: "Authorization"));
        _test.Vault.SetUp(Pin);
        new NotificationSecrets(_test.Vault, _test.Secrets).Set(NotificationSecrets.WebhookHeader, "Bearer t0ken");
        Assert.Empty(_test.Secrets.Secrets);
        _test.Vault.Lock();

        // Not allowed to open the vault: sent without the token.
        await NewRunner().RunAsync(job.Id);
        Assert.False(_test.Http.Requests[^1].Headers.ContainsKey("Authorization"));

        _test.Vault.Unlock(Pin);
        MainViewModel.CreateScheduledAccess(_test.Services).Enable();
        _test.Vault.Lock();
        await NewRunner().RunAsync(job.Id);
        Assert.Equal("Bearer t0ken", _test.Http.Requests[^1].Headers["Authorization"]);
    }

    // --- The window ---

    [Fact]
    public void The_window_saves_channels_and_keeps_secrets_in_the_keychain_without_pin()
    {
        var main = new MainViewModel(_test.Services);
        var window = new NotificationsViewModel(main)
        {
            EmailEnabled = true,
            EmailHost = "smtp.example.com",
            EmailPort = "465",
            EmailUserName = "me",
            EmailPassword = "pw",
            EmailFrom = "me@example.com",
            EmailTo = "me@example.com",
            WebhookEnabled = true,
            WebhookUrl = "https://gotify.example.com/message",
            SuppressRepeats = false,
        };
        window.EmailSecurity = window.SecurityOptions.Single(option => option.Value == SmtpSecurity.SslOnConnect);
        window.WebhookFormat = window.FormatOptions.Single(option => option.Value == WebhookFormat.Gotify);
        Assert.Equal("X-Gotify-Key", window.WebhookHeaderName);
        window.WebhookHeaderValue = "app-token";
        window.EmailEvents.Single(option => option.Event == NotificationEvent.Succeeded).IsChecked = true;
        var closed = false;
        window.CloseRequested += (_, _) => closed = true;

        window.SaveCommand.Execute(null);

        Assert.True(closed);
        var saved = _test.Settings.Load().Notifications;
        Assert.Equal(465, saved.Email.Port);
        Assert.Equal(SmtpSecurity.SslOnConnect, saved.Email.Security);
        Assert.Contains(NotificationEvent.Succeeded, saved.Email.Events);
        Assert.Equal(WebhookFormat.Gotify, saved.Webhook.Format);
        Assert.False(saved.SuppressRepeats);
        Assert.Equal("pw", _test.Secrets.Secrets[NotificationSecrets.SmtpPassword]);
        Assert.Equal("app-token", _test.Secrets.Secrets[NotificationSecrets.WebhookHeader]);
        Assert.DoesNotContain("pw", File.ReadAllText(_test.Settings.FilePath));

        // Opened again: everything is back.
        var again = new NotificationsViewModel(main);
        Assert.Equal("pw", again.EmailPassword);
        Assert.Equal("app-token", again.WebhookHeaderValue);
        Assert.True(again.EmailEvents.Single(option => option.Event == NotificationEvent.Succeeded).IsChecked);
    }

    [Fact]
    public void With_a_pin_the_secrets_go_to_the_vault()
    {
        _test.Vault.SetUp(Pin);
        var main = new MainViewModel(_test.Services);
        var window = new NotificationsViewModel(main)
        {
            WebhookEnabled = true,
            WebhookUrl = "https://example.com/hook",
            WebhookHeaderName = "Authorization",
            WebhookHeaderValue = "Bearer x",
        };
        Assert.Equal(Strings.NotifySecretsVault, window.SecretsHint);

        window.SaveCommand.Execute(null);

        Assert.Empty(_test.Secrets.Secrets);
        Assert.Equal("Bearer x", _test.Vault.GetSecret(NotificationSecrets.WebhookHeader));
    }

    [Fact]
    public void An_incomplete_channel_is_not_saved()
    {
        var main = new MainViewModel(_test.Services);
        var window = new NotificationsViewModel(main) { EmailEnabled = true, EmailHost = "smtp.example.com", EmailPort = "x", WebhookEnabled = true };

        window.SaveCommand.Execute(null);

        Assert.True(window.HasErrors);
        Assert.Contains(BorgTexts.Format(Strings.NotifyErrorIn, Strings.NotifyChannelEmail, Strings.NotifyErrorPort), window.Errors);
        Assert.Contains(BorgTexts.Format(Strings.NotifyErrorIn, Strings.NotifyChannelWebhook, Strings.NotifyErrorUrl), window.Errors);
        Assert.False(_test.Settings.Load().Notifications.Email.Enabled);
    }

    [Fact]
    public async Task The_test_button_sends_through_a_channel_that_is_not_switched_on_yet()
    {
        var main = new MainViewModel(_test.Services);
        var window = new NotificationsViewModel(main) { WebhookUrl = "https://example.com/hook" };

        await window.TestSystemCommand.ExecuteAsync(null);
        await window.TestWebhookCommand.ExecuteAsync(null);

        Assert.Equal(Strings.NotifyTestSent, window.SystemTestResult);
        Assert.Equal(Strings.NotifyTestSent, window.WebhookTestResult);
        Assert.True((bool?)JsonNode.Parse(Assert.Single(_test.Http.Requests).Body)!["test"]);

        _test.Http.Status = System.Net.HttpStatusCode.NotFound;
        await window.TestWebhookCommand.ExecuteAsync(null);
        Assert.StartsWith(BorgTexts.Format(Strings.NotifyTestFailed, "404"), window.WebhookTestResult);

        await window.TestEmailCommand.ExecuteAsync(null);
        Assert.Equal(Strings.NotifyErrorHost, window.EmailTestResult);
    }

    [Fact]
    public async Task The_secrets_travel_with_an_export()
    {
        new NotificationSecrets(_test.Vault, _test.Secrets).Set(NotificationSecrets.SmtpPassword, "pw");
        var main = new MainViewModel(_test.Services);

        var export = await SettingsTransfer.CollectAsync(main, includeBorgKeys: false);

        Assert.Equal("pw", export.Contents.Secrets[NotificationSecrets.SmtpPassword]);
    }
}
