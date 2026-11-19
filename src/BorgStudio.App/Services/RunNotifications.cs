using System.Globalization;
using BorgStudio.App.Resources;
using BorgStudio.App.ViewModels;
using BorgStudio.Core.Backups;
using BorgStudio.Core.Notifications;
using BorgStudio.Core.Repositories;
using BorgStudio.Core.Security;
using BorgStudio.Core.Storage;

namespace BorgStudio.App.Services;

/// <summary>
/// Reports a finished run through the notification channels: decides its event (leaving out repeats if wanted), words
/// it in the app's language and sends it. Used after every run – by hand, in the background and scheduled ones.
/// Sending problems go to logs/notifications.log; they never fail the backup.
/// </summary>
public sealed class RunNotifications(AppServices services)
{
    public const string LogFile = "notifications.log";

    public NotificationPolicy Policy { get; } = NotificationPolicy.ForDataDirectory(services.Jobs.DataDirectory);

    /// <param name="vault">The vault to read the channels' secrets from (opened by the caller, if a PIN is set up).</param>
    public async Task<IReadOnlyList<ChannelResult>> NotifyAsync(BackupJob job, BackupRunSummary run, Vault vault,
        CancellationToken cancellationToken = default)
    {
        if (services.Notifier is not { } notifier || !job.Notify)
            return [];

        // Decided for every run, so a job's open problem is known when a channel is switched on later.
        var decision = Policy.Decide(job.Id, run);
        var settings = services.Settings.Load().Notifications;
        if (decision is null || !settings.AnyEnabled || (decision.IsRepeat && settings.SuppressRepeats))
            return [];

        var repository = services.RepositoryStore.Load().Repositories.FirstOrDefault(candidate => candidate.Id == job.RepositoryId)?.Name;
        var message = Compose(decision.Event, job, repository, run);
        var secrets = new NotificationSecrets(vault, services.SecretStore);
        var results = await notifier.SendAsync(settings, message, secrets.Get, cancellationToken);
        foreach (var result in results)
            AppLog.Write(LogFile, $"{job.Name}: {decision.Event} → {result.Channel}: {result.Error ?? "sent"}", services.Jobs.DataDirectory);
        return results;
    }

    /// <summary>
    /// Reports a repository's storage when it got almost full or full (see <see cref="StorageLevels.Evaluate"/>). Called
    /// after each run, with the measurement taken then (<c>null</c>: not measured – only a backup that ran out of space counts).
    /// </summary>
    public async Task<IReadOnlyList<ChannelResult>> NotifyStorageAsync(RepositoryConfig repository, StorageUsage? usage, Vault vault,
        CancellationToken cancellationToken = default)
    {
        if (services.Notifier is not { } notifier)
            return [];
        var settings = services.Settings.Load().Notifications;
        var level = StorageLevels.Evaluate(usage, repository, services.Jobs.Load(), settings.StorageFull);
        var decision = Policy.DecideStorage(repository.Id, level);
        if (decision is null || !settings.AnyEnabled || (decision.IsRepeat && settings.SuppressRepeats))
            return [];

        var message = ComposeStorage(decision.Event, repository.Name, usage);
        var secrets = new NotificationSecrets(vault, services.SecretStore);
        var results = await notifier.SendAsync(settings, message, secrets.Get, cancellationToken);
        foreach (var result in results)
            AppLog.Write(LogFile, $"{repository.Name}: {decision.Event} → {result.Channel}: {result.Error ?? "sent"}", services.Jobs.DataDirectory);
        return results;
    }

    public static NotificationMessage ComposeStorage(NotificationEvent notificationEvent, string repository, StorageUsage? usage)
    {
        var lines = new List<string>();
        if (usage is not null && BorgTexts.StorageUsage(usage) is { } text)
            lines.Add(text);
        if (notificationEvent == NotificationEvent.StorageFull)
            lines.Add(Strings.NotifyStorageFullHint);
        lines.Add(BorgTexts.Format(Strings.NotifyLineComputer, Environment.MachineName));
        var title = BorgTexts.Format(notificationEvent == NotificationEvent.StorageFull ? Strings.NotifyTitleStorageFull : Strings.NotifyTitleStorageLow,
            repository);
        return new NotificationMessage(notificationEvent, title, string.Join("\n", lines)) { Repository = repository, Storage = usage };
    }

    public static string Title(NotificationEvent notificationEvent, string jobName) => BorgTexts.Format(notificationEvent switch
    {
        NotificationEvent.Succeeded => Strings.NotifyTitleSucceeded,
        NotificationEvent.Warnings => Strings.NotifyTitleWarnings,
        NotificationEvent.Skipped => Strings.NotifyTitleSkipped,
        NotificationEvent.Recovered => Strings.NotifyTitleRecovered,
        _ => Strings.NotifyTitleFailed,
    }, jobName);

    /// <summary>The run in a few lines: where, when, how much, and what went wrong.</summary>
    public static NotificationMessage Compose(NotificationEvent notificationEvent, BackupJob job, string? repository, BackupRunSummary run)
    {
        var lines = new List<string>();
        if (repository is not null)
            lines.Add(BorgTexts.Format(Strings.NotifyLineRepository, repository));
        lines.Add(BorgTexts.Format(Strings.NotifyLineStarted, run.Started.ToLocalTime().ToString("g", CultureInfo.CurrentCulture),
            BorgTexts.Duration(run.Duration)));

        if (run.SkipReason is { } skip)
            lines.Add(BorgTexts.Format(Strings.NotifyLineReason, BorgTexts.SkipReason(skip)));
        else if (run.Outcome == BackupOutcome.Failed && run.Message is { } error)
            lines.Add(BorgTexts.Format(Strings.NotifyLineReason, error));

        if (run.Outcome is BackupOutcome.Succeeded or BackupOutcome.Warnings)
        {
            if (run.ArchiveName is { } archive)
                lines.Add(BorgTexts.Format(Strings.NotifyLineArchive, archive));
            lines.Add(BorgTexts.Format(Strings.NotifyLineSizes, BorgTexts.Size(run.OriginalSize), BorgTexts.Size(run.DeduplicatedSize),
                run.Files.ToString("N0", CultureInfo.CurrentCulture)));
            if (run.Outcome == BackupOutcome.Warnings && run.Message is { } warning)
                lines.Add(BorgTexts.Format(Strings.NotifyLineWarning, warning));
            if (run.Cleanup is { } cleanup)
                lines.Add(BorgTexts.Format(Strings.NotifyLineCleanup, BorgTexts.Cleanup(cleanup)));
        }
        lines.Add(BorgTexts.Format(Strings.NotifyLineComputer, Environment.MachineName));

        return new NotificationMessage(notificationEvent, Title(notificationEvent, job.Name), string.Join("\n", lines))
        {
            Job = job.Name,
            JobId = job.Id,
            Repository = repository,
            Run = run,
        };
    }

    /// <summary>What the "Test" button sends.</summary>
    public static NotificationMessage TestMessage(string channelName) =>
        new(NotificationEvent.Succeeded, Strings.NotifyTestTitle, BorgTexts.Format(Strings.NotifyTestBody, channelName))
        {
            IsTest = true,
        };
}
