using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using BorgStudio.App.Resources;
using BorgStudio.App.ViewModels;
using BorgStudio.Core.Backups;
using BorgStudio.Core.Notifications;
using BorgStudio.Core.Repositories;
using BorgStudio.Core.Settings;
using BorgStudio.Core.Trigger;
using BorgStudio.Tests.TestSupport;

namespace BorgStudio.Tests.ViewModels;

/// <summary>The incoming webhook in the app: it runs jobs like scheduled ones, in background mode only.</summary>
public sealed class TriggerFlowTests : IDisposable
{
    private readonly TestServices _test = new();
    private readonly RepositoryConfig _repository;
    private readonly HttpClient _http = new();
    private MainViewModel? _main;

    public TriggerFlowTests()
    {
        _repository = new RepositoryConfig
        {
            Id = Guid.NewGuid(),
            Name = "Disk",
            ProviderId = "local",
            ProviderValues = new Dictionary<string, string> { ["path"] = _test.Directory.Combine("repo") },
            PassphraseMode = PassphraseMode.None,
            EncryptionMode = "none",
        };
        Directory.CreateDirectory(_test.Directory.Combine("repo"));
        Directory.CreateDirectory(_test.Directory.Combine("Documents"));
        _test.Store.Save([_repository]);
    }

    public void Dispose()
    {
        _main?.StopTrigger();
        _http.Dispose();
        _test.Dispose();
    }

    private BackupJob Job(string name, bool webhook) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        RepositoryId = _repository.Id,
        Sources = [_test.Directory.Combine("Documents")],
        Exclusions = new BackupExclusions { CachesAndTemp = false },
        WebhookTrigger = webhook,
    };

    /// <summary>The app in background mode with the webhook on (any free port) and a token.</summary>
    private async Task<(MainViewModel Main, string Token)> StartAsync(ScheduleRunner runner = ScheduleRunner.Background, bool schedulesPaused = false)
    {
        _test.Jobs.Save([Job("Documents", webhook: true), Job("Photos", webhook: false)]);
        _test.Settings.Save(new AppSettings
        {
            ScheduleRunner = runner,
            SchedulesPaused = schedulesPaused,
            Trigger = new TriggerSettings { Enabled = true, Port = 0 },
        });
        _main = new MainViewModel(_test.Services) { SchedulerExecutable = "BorgStudio.exe" };
        await _main.CheckBorgCommand.ExecuteAsync(null);
        var token = _main.CreateTriggerToken();
        if (_main.TriggerPort is { } port)
            _http.BaseAddress = new Uri($"http://127.0.0.1:{port}");
        return (_main, token);
    }

    private async Task<(HttpStatusCode Status, JsonNode Body)> SendAsync(HttpMethod method, string path, string token)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _http.SendAsync(request);
        return (response.StatusCode, JsonNode.Parse(await response.Content.ReadAsStringAsync())!);
    }

    [Fact]
    public async Task A_job_allowed_for_the_webhook_runs_and_its_outcome_comes_back()
    {
        var (main, token) = await StartAsync(schedulesPaused: true);
        Assert.Equal(BorgTexts.Format(Strings.TriggerListening, "127.0.0.1", main.TriggerPort), main.TriggerStatus);
        // The token is kept like a passphrase (here: the keychain), never in settings.json.
        Assert.Equal(token, _test.Secrets.Secrets[NotificationSecrets.TriggerToken]);
        Assert.DoesNotContain(token, File.ReadAllText(_test.Settings.FilePath));

        var (status, body) = await SendAsync(HttpMethod.Post, "/run/Documents?wait=1", token);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("Succeeded", (string?)body["outcome"]);
        Assert.Contains("create", _test.Borg.Commands);
        Assert.Equal(BackupOutcome.Succeeded, main.AllJobs.Single(job => job.Name == "Documents").LastRun!.Outcome);

        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Post, "/run/Photos", token)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(HttpMethod.Post, "/run/Documents", "old")).Status);
    }

    [Fact]
    public async Task Status_lists_the_jobs_and_cancel_needs_a_running_backup()
    {
        var (_, token) = await StartAsync();

        var (status, body) = await SendAsync(HttpMethod.Get, "/status", token);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Null(body["running"]);
        var jobs = body["jobs"]!.AsArray();
        Assert.Equal(["Documents", "Photos"], jobs.Select(job => (string?)job!["name"]));
        Assert.True((bool?)jobs[0]!["webhook"]);
        Assert.Equal("Disk", (string?)jobs[0]!["repository"]);

        Assert.False((bool?)(await SendAsync(HttpMethod.Post, "/cancel", token)).Body["cancelled"]);
    }

    [Fact]
    public async Task Only_the_background_listens_and_a_new_token_replaces_the_old()
    {
        var (main, token) = await StartAsync(ScheduleRunner.System);
        Assert.Null(main.TriggerPort);
        Assert.Equal(Strings.TriggerNeedsBackground, main.TriggerStatus);

        main.SetScheduleRunner(ScheduleRunner.Background);
        Assert.NotNull(main.TriggerPort);
        _http.BaseAddress = new Uri($"http://127.0.0.1:{main.TriggerPort}");
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, "/status", token)).Status);

        var newToken = main.CreateTriggerToken();
        _http.Dispose();
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{main.TriggerPort}") };
        using var request = new HttpRequestMessage(HttpMethod.Get, "/status") { Headers = { Authorization = new AuthenticationHeaderValue("Bearer", token) } };
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(request)).StatusCode);
        Assert.NotEqual(token, newToken);

        main.SetScheduleRunner(ScheduleRunner.System);
        Assert.Null(main.TriggerPort);
    }

    /// <summary>A port nobody listens on right now (asked from the system, then released).</summary>
    private static int FreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    [Fact]
    public void The_settings_window_switches_the_webhook_and_makes_a_token()
    {
        // The window keeps ports in 1..65535 (0, "any free port", is for the other tests only): a free one it is.
        var port = FreePort();
        _test.Settings.Save(new AppSettings { ScheduleRunner = ScheduleRunner.Background, Trigger = new TriggerSettings { Port = port } });
        var main = _main = new MainViewModel(_test.Services) { SchedulerExecutable = "BorgStudio.exe" };
        var settings = new SettingsViewModel(main, _test.Dialogs);
        Assert.Equal("", settings.TriggerToken);

        settings.TriggerEnabled = true;

        Assert.True(_test.Settings.Load().Trigger.Enabled);
        Assert.NotEqual("", settings.TriggerToken);
        Assert.Equal(port, main.TriggerPort);
        Assert.Contains("/run/<job>", settings.TriggerExample);

        settings.TriggerEnabled = false;
        Assert.Null(main.TriggerPort);
        settings.Detach();
    }

    [Fact]
    public void The_job_editor_allows_the_webhook()
    {
        var editor = new BackupJobEditorViewModel(_repository, FakeBorg.Installation, _test.Dialogs, Job("Documents", webhook: false));
        Assert.False(editor.WebhookTrigger);
        editor.WebhookTrigger = true;
        editor.SaveCommand.Execute(null);
        Assert.True(editor.Result!.WebhookTrigger);
    }
}
