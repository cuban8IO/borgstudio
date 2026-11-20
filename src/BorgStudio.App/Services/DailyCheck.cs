using System.Globalization;
using BorgStudio.App.Resources;
using BorgStudio.App.ViewModels;
using BorgStudio.Core.Backups;
using BorgStudio.Core.Notifications;
using BorgStudio.Core.Security;
using BorgStudio.Core.Storage;

namespace BorgStudio.App.Services;

/// <summary>
/// Once a day (at <see cref="NotificationSettings.DailyTime"/>): reports jobs that have not backed up for too long, and
/// sends the summary of the day – the runs since the last check, the storage of each repository and the overdue jobs –
/// to the channels that want it. Started by the system's scheduler (<c>--daily-check</c>, or made up for by
/// <c>--catch-up</c>) or by BorgStudio in the background.
/// </summary>
public sealed class DailyCheck(AppServices services)
{
    /// <summary>Lines of runs in one summary; more are counted only.</summary>
    public const int MaxRunLines = 30;

    public DailyCheckState State { get; } = DailyCheckState.ForDataDirectory(services.Jobs.DataDirectory);

    /// <summary>Runs the check if its time came since the last one (made up for once); <c>false</c> if it was not due.</summary>
    public async Task<bool> RunIfDueAsync(DateTime now, Vault vault, CancellationToken cancellationToken = default)
    {
        var settings = services.Settings.Load().Notifications;
        if (!settings.NeedsDailyCheck(services.Jobs.Load()) || !State.IsDue(settings.DailyTime, now))
            return false;
        await RunAsync(now, vault, cancellationToken);
        return true;
    }

    /// <summary>The check, now (the system's scheduler started it at its time).</summary>
    /// <param name="vault">Where the channels' secrets are (opened by the caller, if a PIN is set up).</param>
    public async Task RunAsync(DateTime now, Vault vault, CancellationToken cancellationToken = default)
    {
        var since = State.LastCheck ?? new DateTimeOffset(now.AddDays(-1));
        State.Record(now);

        var settings = services.Settings.Load().Notifications;
        var jobs = services.Jobs.Load();
        var history = new BackupRunHistory(Path.Combine(services.Jobs.DataDirectory, "history"));
        var policy = NotificationPolicy.ForDataDirectory(services.Jobs.DataDirectory);
        var notifier = services.Notifier;
        var secrets = new NotificationSecrets(vault, services.SecretStore);

        // Overdue jobs: each once, until it backs up again.
        var overdue = State.Overdue(jobs.Where(job => job.Notify), history, now);
        foreach (var job in jobs.Where(job => job.Notify && job.OverdueDays > 0))
        {
            var entry = overdue.FirstOrDefault(candidate => candidate.Job.Id == job.Id);
            var decision = policy.DecideOverdue(job.Id, entry.Job is not null);
            if (decision is null || notifier is null || !settings.AnyEnabled || (decision.IsRepeat && settings.SuppressRepeats))
                continue;
            var message = new NotificationMessage(NotificationEvent.Overdue, BorgTexts.Format(Strings.NotifyTitleOverdue, job.Name),
                OverdueLine(job, entry.Since, now))
            {
                Job = job.Name,
                JobId = job.Id,
                Repository = RepositoryName(job.RepositoryId),
            };
            Log(await notifier.SendAsync(settings, message, secrets.Get, cancellationToken), job.Name, NotificationEvent.Overdue);
        }

        // The summary, for the channels that want it.
        if (notifier is null || !settings.Channels.Any(channel => channel.Reports(NotificationEvent.Summary)))
            return;
        var summary = Summary(jobs, history, since, overdue, now, out var problems);
        if (problems == 0 && !settings.SummaryWhenAllWell)
            return;
        Log(await notifier.SendAsync(settings, summary, secrets.Get, cancellationToken), "summary", NotificationEvent.Summary);
    }

    private NotificationMessage Summary(IReadOnlyList<BackupJob> jobs, BackupRunHistory history, DateTimeOffset since,
        IReadOnlyList<(BackupJob Job, DateTimeOffset Since)> overdue, DateTime now, out int problems)
    {
        problems = 0;
        var lines = new List<string>();

        // Runs since the last check, oldest first.
        var runs = jobs.SelectMany(job => history.Get(job.Id).Where(run => run.Started > since).Select(run => (Job: job, Run: run)))
            .OrderBy(entry => entry.Run.Started)
            .ToList();
        problems += runs.Count(entry => entry.Run.Outcome is not (BackupOutcome.Succeeded or BackupOutcome.Cancelled));
        lines.Add(BorgTexts.Format(Strings.SummaryRunsSince, since.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)));
        if (runs.Count == 0)
            lines.Add("  " + Strings.SummaryNoRuns);
        foreach (var (job, run) in runs.Take(MaxRunLines))
            lines.Add("  • " + RunLine(job, run));
        if (runs.Count > MaxRunLines)
            lines.Add("  " + BorgTexts.Format(Strings.SummaryMoreRuns, runs.Count - MaxRunLines));

        // Storage of each repository, as last measured.
        var store = StorageUsageStore.ForDataDirectory(services.Jobs.DataDirectory);
        var criteria = services.Settings.Load().Notifications.StorageFull;
        var storage = new List<string>();
        foreach (var repository in services.RepositoryStore.Load().Repositories)
        {
            if (store.Get(repository.Id) is not { } usage || BorgTexts.StorageUsage(usage) is not { } text)
                continue;
            var level = StorageLevels.Evaluate(usage, repository, jobs, criteria);
            if (level != StorageLevel.Ok)
                problems++;
            var suffix = level switch
            {
                StorageLevel.Full => $" – {Strings.NotifyEventStorageFull}",
                StorageLevel.Low => $" – {Strings.NotifyEventStorageLow}",
                _ => "",
            };
            storage.Add($"  • {repository.Name}: {text}{suffix}");
        }
        if (storage.Count > 0)
        {
            lines.Add("");
            lines.Add(Strings.SummaryStorage);
            lines.AddRange(storage);
        }

        if (overdue.Count > 0)
        {
            problems += overdue.Count;
            lines.Add("");
            lines.Add(Strings.SummaryOverdue);
            lines.AddRange(overdue.Select(entry => "  • " + OverdueLine(entry.Job, entry.Since, now)));
        }

        lines.Add("");
        lines.Add(BorgTexts.Format(Strings.NotifyLineComputer, Environment.MachineName));
        var title = problems == 0 ? Strings.SummaryTitleAllWell : BorgTexts.Format(Strings.SummaryTitleProblems, problems);
        return new NotificationMessage(NotificationEvent.Summary, title, string.Join("\n", lines));
    }

    /// <summary>"19.11.2026 08:00 Dokumente: Erfolgreich – 1,2 GB gelesen, 30 MB neu".</summary>
    private static string RunLine(BackupJob job, BackupRunSummary run)
    {
        var line = $"{run.Started.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)} {job.Name}: {BorgTexts.BackupOutcome(run.Outcome)}";
        if (run.Outcome is BackupOutcome.Succeeded or BackupOutcome.Warnings)
            return line + " – " + BorgTexts.Format(Strings.SummaryRunSizes, BorgTexts.Size(run.OriginalSize), BorgTexts.Size(run.DeduplicatedSize));
        var reason = run.SkipReason is { } skip ? BorgTexts.SkipReason(skip) : run.Message;
        return reason is null ? line : $"{line} – {reason}";
    }

    /// <summary>"Dokumente: keine erfolgreiche Sicherung seit 12.11.2026 (7 Tage)".</summary>
    private static string OverdueLine(BackupJob job, DateTimeOffset since, DateTime now) =>
        BorgTexts.Format(Strings.SummaryOverdueLine, job.Name, since.ToLocalTime().ToString("d", CultureInfo.CurrentCulture),
            (int)(new DateTimeOffset(now) - since).TotalDays);

    private string? RepositoryName(Guid repositoryId) =>
        services.RepositoryStore.Load().Repositories.FirstOrDefault(repository => repository.Id == repositoryId)?.Name;

    private void Log(IReadOnlyList<ChannelResult> results, string what, NotificationEvent notificationEvent)
    {
        foreach (var result in results)
            AppLog.Write(RunNotifications.LogFile, $"{what}: {notificationEvent} → {result.Channel}: {result.Error ?? "sent"}",
                services.Jobs.DataDirectory);
    }
}
