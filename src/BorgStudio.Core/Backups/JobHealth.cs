namespace BorgStudio.Core.Backups;

public enum HealthLevel
{
    /// <summary>Nothing to judge (a job without schedule, a repository without jobs).</summary>
    Info,

    Ok,

    Warning,

    Critical,
}

/// <summary>Why a job is in its state.</summary>
public enum HealthReason
{
    Fine,

    /// <summary>Started by hand only: the age of its last backup is shown, not judged.</summary>
    Unscheduled,

    /// <summary>Scheduled, but never backed up yet.</summary>
    NeverBackedUp,

    /// <summary>Its last run failed or was skipped.</summary>
    LastRunFailed,

    /// <summary>Its last scheduled time passed without a successful backup.</summary>
    Missed,

    /// <summary>The last two scheduled times passed without a successful backup.</summary>
    MissedTwice,

    /// <summary>No successful backup for longer than its <see cref="BackupJob.OverdueDays"/>.</summary>
    Overdue,
}

/// <summary>How a job is doing, judged by its schedule.</summary>
/// <param name="LastSuccess">When its last backup that worked (with or without warnings) started.</param>
public sealed record JobHealth(HealthLevel Level, HealthReason Reason, DateTimeOffset? LastSuccess)
{
    /// <summary>Time a scheduled run gets to finish before its time counts as missed.</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromHours(1);

    /// <summary>
    /// Scheduled jobs: a warning when the last scheduled time passed without a successful backup (or the last run
    /// failed), critical after two such times or after the job's overdue days. Jobs started by hand are only judged by a
    /// failed last run and their overdue days.
    /// </summary>
    /// <param name="history">The job's runs, newest first.</param>
    public static JobHealth Evaluate(BackupJob job, IReadOnlyList<BackupRunSummary> history, DateTime now)
    {
        var runs = history.Append(job.LastRun).OfType<BackupRunSummary>().Where(run => run.Outcome != BackupOutcome.Cancelled).ToList();
        var lastSuccess = runs.Where(run => run.Outcome is BackupOutcome.Succeeded or BackupOutcome.Warnings)
            .Select(run => (DateTimeOffset?)run.Started).Max();
        var last = runs.MaxBy(run => run.Started);
        var lastFailed = last?.Outcome is BackupOutcome.Failed or BackupOutcome.Skipped;

        if (job.OverdueDays > 0 && lastSuccess is { } success && new DateTimeOffset(now) - success > TimeSpan.FromDays(job.OverdueDays))
            return new JobHealth(HealthLevel.Critical, HealthReason.Overdue, lastSuccess);

        if (!job.IsScheduled)
            return lastFailed
                ? new JobHealth(HealthLevel.Warning, HealthReason.LastRunFailed, lastSuccess)
                : new JobHealth(HealthLevel.Info, HealthReason.Unscheduled, lastSuccess);

        if (runs.Count == 0)
            return new JobHealth(HealthLevel.Warning, HealthReason.NeverBackedUp, null);

        // A run started a little before its time (or on time) counts for it.
        bool Covered(DateTime due) => lastSuccess is { } at && at.LocalDateTime >= due - ScheduleCalendar.OnTime;
        var due = ScheduleCalendar.Previous(job.Schedules, now - Grace);
        if (due is null || Covered(due.Value))
            return lastFailed
                ? new JobHealth(HealthLevel.Warning, HealthReason.LastRunFailed, lastSuccess)
                : new JobHealth(HealthLevel.Ok, HealthReason.Fine, lastSuccess);

        var before = ScheduleCalendar.Previous(job.Schedules, due.Value.AddMinutes(-1));
        return before is { } earlier && !Covered(earlier)
            ? new JobHealth(HealthLevel.Critical, HealthReason.MissedTwice, lastSuccess)
            : new JobHealth(HealthLevel.Warning, lastFailed ? HealthReason.LastRunFailed : HealthReason.Missed, lastSuccess);
    }
}
