namespace BorgStudio.Core.Backups;

/// <summary>How often a schedule starts its job.</summary>
public enum ScheduleKind
{
    /// <summary>Every day at <see cref="BackupSchedule.Time"/>.</summary>
    Daily,

    /// <summary>On <see cref="BackupSchedule.Days"/> at <see cref="BackupSchedule.Time"/>.</summary>
    Weekdays,

    /// <summary>Every <see cref="BackupSchedule.Hours"/> hours, from <see cref="BackupSchedule.Time"/> on each day.</summary>
    EveryHours,
}

/// <summary>When a backup job runs by itself; a job may have several (e.g. weekdays 12:30 and Sundays 20:00).</summary>
public sealed record BackupSchedule
{
    public ScheduleKind Kind { get; init; } = ScheduleKind.Daily;

    /// <summary>Local time of day; for <see cref="ScheduleKind.EveryHours"/> the first run of each day.</summary>
    public TimeOnly Time { get; init; } = new(20, 0);

    public List<DayOfWeek> Days { get; init; } = [];

    /// <summary>1–12; for <see cref="ScheduleKind.EveryHours"/>.</summary>
    public int Hours { get; init; } = 4;

    /// <summary>Whether the schedule says anything (weekdays need at least one day).</summary>
    public bool IsValid => Kind switch
    {
        ScheduleKind.Weekdays => Days.Count > 0,
        ScheduleKind.EveryHours => Hours is >= 1 and <= 12,
        _ => true,
    };

    /// <summary>The times of day this schedule starts the job (one, or several for every N hours).</summary>
    public IReadOnlyList<TimeOnly> TimesOfDay()
    {
        if (Kind != ScheduleKind.EveryHours || !IsValid)
            return [Time];
        var times = new List<TimeOnly>();
        for (var at = Time.ToTimeSpan(); at < TimeSpan.FromDays(1); at += TimeSpan.FromHours(Hours))
            times.Add(TimeOnly.FromTimeSpan(at));
        return times;
    }

    /// <summary>Whether the schedule runs on <paramref name="day"/> at all.</summary>
    public bool RunsOn(DayOfWeek day) => Kind != ScheduleKind.Weekdays || Days.Contains(day);
}

/// <summary>Works out when scheduled jobs run – in local time, as the user set them up.</summary>
public static class ScheduleCalendar
{
    /// <summary>The first time after <paramref name="after"/> any of the schedules starts the job; null without schedules.</summary>
    public static DateTime? Next(IEnumerable<BackupSchedule> schedules, DateTime after)
    {
        DateTime? next = null;
        foreach (var schedule in schedules.Where(schedule => schedule.IsValid))
        {
            for (var day = after.Date; day <= after.Date.AddDays(7); day = day.AddDays(1))
            {
                if (!schedule.RunsOn(day.DayOfWeek))
                    continue;
                var found = schedule.TimesOfDay().Select(time => day + time.ToTimeSpan()).Where(at => at > after).Cast<DateTime?>().FirstOrDefault();
                if (found is { } at)
                {
                    if (next is null || at < next)
                        next = at;
                    break;
                }
            }
        }
        return next;
    }

    /// <summary>The last time at or before <paramref name="now"/> the schedules should have started the job.</summary>
    public static DateTime? Previous(IEnumerable<BackupSchedule> schedules, DateTime now)
    {
        DateTime? previous = null;
        foreach (var schedule in schedules.Where(schedule => schedule.IsValid))
        {
            for (var day = now.Date; day >= now.Date.AddDays(-7); day = day.AddDays(-1))
            {
                if (!schedule.RunsOn(day.DayOfWeek))
                    continue;
                var found = schedule.TimesOfDay().Select(time => day + time.ToTimeSpan()).Where(at => at <= now)
                    .Cast<DateTime?>().LastOrDefault();
                if (found is { } at)
                {
                    if (previous is null || at > previous)
                        previous = at;
                    break;
                }
            }
        }
        return previous;
    }

    /// <summary>
    /// Whether a job with "catch up" should run now: a scheduled time has passed since its last run (the computer was
    /// off then). Once, however many times were missed.
    /// </summary>
    public static bool IsMissed(BackupJob job, DateTime now)
    {
        if (!job.CatchUp || Previous(job.Schedules, now) is not { } due)
            return false;
        var last = job.LastRun?.Started.LocalDateTime;
        return last is null || last < due;
    }
}
