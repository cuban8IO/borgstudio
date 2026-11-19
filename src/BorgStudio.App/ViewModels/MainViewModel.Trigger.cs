using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using BorgStudio.App.Resources;
using BorgStudio.Core.Backups;
using BorgStudio.Core.Notifications;
using BorgStudio.Core.Trigger;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BorgStudio.App.ViewModels;

/// <summary>
/// The incoming webhook: in background mode BorgStudio can listen for <c>POST /run/&lt;job&gt;</c> and starts the job
/// like a scheduled one – after the one running now, without questions. Only jobs that allow it, only with the token.
/// </summary>
public partial class MainViewModel
{
    private TriggerServer? _trigger;
    private string? _triggerToken;

    /// <summary>"Hört auf 127.0.0.1:8473", why it does not, or <c>null</c> when switched off.</summary>
    [ObservableProperty]
    public partial string? TriggerStatus { get; private set; }

    /// <summary>The port the webhook listens on; <c>null</c> while it does not (tests ask for port 0).</summary>
    internal int? TriggerPort => _trigger?.Port;

    /// <summary>Starts, restarts or stops the webhook to match the settings and the mode.</summary>
    internal void ApplyTrigger()
    {
        StopTrigger();
        var settings = Settings.Trigger;
        if (!settings.Enabled)
        {
            TriggerStatus = null;
            return;
        }
        if (!IsBackgroundMode)
        {
            TriggerStatus = Strings.TriggerNeedsBackground;
            return;
        }
        _triggerToken = ReadTriggerToken();
        if (_triggerToken is null)
        {
            TriggerStatus = Strings.TriggerNoToken;
            return;
        }
        try
        {
            _trigger = new TriggerServer(new TriggerHandler(this), () => _triggerToken, settings);
            TriggerStatus = BorgTexts.Format(Strings.TriggerListening, settings.AllowNetwork ? Strings.TriggerAllAddresses : "127.0.0.1", _trigger.Port);
        }
        catch (SocketException exception)
        {
            TriggerStatus = BorgTexts.Format(Strings.TriggerFailed, settings.Port, exception.Message);
        }
    }

    internal void StopTrigger()
    {
        _trigger?.Dispose();
        _trigger = null;
    }

    /// <summary>The token, read with the key for scheduled runs when the window's vault is locked; <c>null</c> if there is none.</summary>
    private string? ReadTriggerToken()
    {
        var vault = OpenVaultCopy(scheduledAccess: true);
        try
        {
            return new NotificationSecrets(vault, _services.SecretStore).Get(NotificationSecrets.TriggerToken);
        }
        finally
        {
            vault.Lock();
        }
    }

    /// <summary>The token as stored (the window is unlocked when it shows it); <c>null</c> if none was made yet.</summary>
    public string? TriggerToken => new NotificationSecrets(_services.Vault, _services.SecretStore).Get(NotificationSecrets.TriggerToken);

    /// <summary>A new random token replaces the old one (callers with the old one are refused from now on).</summary>
    /// <exception cref="Core.Secrets.SecretStoreException" />
    /// <exception cref="Core.Security.VaultLockedException" />
    public string CreateTriggerToken()
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        new NotificationSecrets(_services.Vault, _services.SecretStore).Set(NotificationSecrets.TriggerToken, token);
        ApplyTrigger();
        return token;
    }

    /// <summary>Queues a run asked for by the webhook (also while schedules are paused).</summary>
    private TriggerStart QueueTriggeredRun(Guid jobId)
    {
        var queued = IsBackupRunning || _dueJobs.Count > 0;
        var waiter = new TaskCompletionSource<BackupRunSummary?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_runWaiters.TryGetValue(jobId, out var waiters))
            _runWaiters[jobId] = waiters = [];
        waiters.Add(waiter);
        if (!_dueJobs.Any(entry => entry.JobId == jobId && entry.Triggered))
            _dueJobs.Enqueue(new QueuedRun(jobId, Triggered: true));
        StartDueJobs();
        RaiseBackgroundStateChanged();
        return new TriggerStart(queued, waiter.Task);
    }

    /// <summary>GET /status: the running backup, the queue, and each job with its last and next run.</summary>
    private JsonObject TriggerStatusJson()
    {
        var now = Clock();
        var jobs = new JsonArray();
        foreach (var job in _jobs)
        {
            var next = job.IsScheduled ? ScheduleCalendar.Next(job.Schedules, now) : null;
            jobs.Add(new JsonObject
            {
                ["id"] = job.Id.ToString(),
                ["name"] = job.Name,
                ["repository"] = Repositories.FirstOrDefault(item => item.Config.Id == job.RepositoryId)?.Name,
                ["webhook"] = job.WebhookTrigger,
                ["scheduled"] = job.IsScheduled,
                ["nextRun"] = next is { } at ? new DateTimeOffset(at).ToString("O") : null,
                ["lastRun"] = job.LastRun is { } run
                    ? new JsonObject
                    {
                        ["outcome"] = run.Outcome.ToString(),
                        ["started"] = run.Started.ToString("O"),
                        ["durationSeconds"] = Math.Round(run.Duration.TotalSeconds),
                        ["message"] = run.Message,
                    }
                    : null,
            });
        }
        return new JsonObject
        {
            ["running"] = IsBackupRunning && CurrentBackup is { } current
                ? new JsonObject { ["job"] = current.Title, ["jobId"] = current.JobId.ToString() }
                : null,
            ["queued"] = new JsonArray([.. _dueJobs.Select(entry => (JsonNode?)(_jobs.FirstOrDefault(job => job.Id == entry.JobId)?.Name ?? entry.JobId.ToString()))]),
            ["schedulesPaused"] = Settings.SchedulesPaused,
            ["jobs"] = jobs,
        };
    }

    /// <summary>What the webhook may do, handed to the view model's thread.</summary>
    private sealed class TriggerHandler(MainViewModel main) : ITriggerHandler
    {
        public Task<TriggerJob?> FindAsync(string idOrName) => main.OnViewModelThreadAsync(() =>
        {
            var job = Guid.TryParse(idOrName, out var id)
                ? main._jobs.FirstOrDefault(candidate => candidate.Id == id)
                : main._jobs.FirstOrDefault(candidate => string.Equals(candidate.Name, idOrName, StringComparison.OrdinalIgnoreCase));
            return job is null ? null : new TriggerJob(job.Id, job.Name, job.WebhookTrigger);
        });

        public Task<TriggerStart> RunAsync(Guid jobId) => main.OnViewModelThreadAsync(() => main.QueueTriggeredRun(jobId));

        public Task<JsonObject> StatusAsync() => main.OnViewModelThreadAsync(main.TriggerStatusJson);

        public Task<bool> CancelAsync() => main.OnViewModelThreadAsync(() =>
        {
            if (!main.IsBackupRunning)
                return false;
            main.CancelBackupCommand.Execute(null);
            return true;
        });
    }
}
