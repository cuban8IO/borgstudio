using System.Globalization;
using BorgStudio.App.Resources;
using BorgStudio.App.ViewModels;
using BorgStudio.Core.Backups;
using BorgStudio.Core.Notifications;
using BorgStudio.Core.Security;

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
