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

    /// <summary>Once a month on the <see cref="BackupSchedule.Week"/>th <see cref="BackupSchedule.Weekday"/> (e.g. the first Sunday).</summary>
    MonthlyWeekday,

    /// <summary>Once a month on day <see cref="BackupSchedule.DayOfMonth"/>.</summary>
    MonthlyDay,

    /// <summary>
    /// Every <see cref="BackupSchedule.Minutes"/> minutes around the clock, on the hour and the minutes after it (every 15
    /// minutes: :00, :15, :30, :45) – so every system scheduler can start it at exactly the same times.
    /// </summary>
    EveryMinutes,
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

    /// <summary>One of <see cref="MinuteSteps"/>; for <see cref="ScheduleKind.EveryMinutes"/>.</summary>
    public int Minutes { get; init; } = 15;

    /// <summary>The minute steps that divide an hour evenly (each hour then starts the same way).</summary>
    public static IReadOnlyList<int> MinuteSteps { get; } = [1, 2, 3, 5, 10, 15, 20, 30];

    /// <summary>For <see cref="Week"/> and <see cref="DayOfMonth"/>: the last one of the month.</summary>
    public const int Last = -1;

    /// <summary>1–4 or <see cref="Last"/>; for <see cref="ScheduleKind.MonthlyWeekday"/>.</summary>
    public int Week { get; init; } = 1;

    /// <summary>For <see cref="ScheduleKind.MonthlyWeekday"/>.</summary>
    public DayOfWeek Weekday { get; init; } = DayOfWeek.Sunday;

    /// <summary>1–28 or <see cref="Last"/> (months differ in length after the 28th); for <see cref="ScheduleKind.MonthlyDay"/>.</summary>
    public int DayOfMonth { get; init; } = 1;

    public bool IsMonthly => Kind is ScheduleKind.MonthlyWeekday or ScheduleKind.MonthlyDay;

    /// <summary>Whether the schedule says anything (weekdays need at least one day).</summary>
    public bool IsValid => Kind switch
    {
        ScheduleKind.Weekdays => Days.Count > 0,
        ScheduleKind.EveryHours => Hours is >= 1 and <= 12,
        ScheduleKind.EveryMinutes => MinuteSteps.Contains(Minutes),
        ScheduleKind.MonthlyWeekday => Week is (>= 1 and <= 4) or Last,
        ScheduleKind.MonthlyDay => DayOfMonth is (>= 1 and <= 28) or Last,
        _ => true,
    };

    /// <summary>The times of day this schedule starts the job (one, or several for every N hours).</summary>
    public IReadOnlyList<TimeOnly> TimesOfDay()
    {
        if (Kind == ScheduleKind.EveryMinutes && IsValid)
            return [.. Enumerable.Range(0, 24 * 60 / Minutes).Select(step => TimeOnly.FromTimeSpan(TimeSpan.FromMinutes(step * Minutes)))];
        if (Kind != ScheduleKind.EveryHours || !IsValid)
            return [Time];
        var times = new List<TimeOnly>();
        for (var at = Time.ToTimeSpan(); at < TimeSpan.FromDays(1); at += TimeSpan.FromHours(Hours))
            times.Add(TimeOnly.FromTimeSpan(at));
        return times;
    }

    /// <summary>Whether the schedule runs on <paramref name="date"/> at all.</summary>
    public bool RunsOn(DateTime date)
    {
        var daysInMonth = DateTime.DaysInMonth(date.Year, date.Month);
        return Kind switch
        {
            ScheduleKind.Weekdays => Days.Contains(date.DayOfWeek),
            ScheduleKind.MonthlyWeekday => date.DayOfWeek == Weekday
                && (Week == Last ? date.Day > daysInMonth - 7 : (date.Day - 1) / 7 + 1 == Week),
            ScheduleKind.MonthlyDay => date.Day == (DayOfMonth == Last ? daysInMonth : DayOfMonth),
            _ => true,
        };
    }
}

/// <summary>Works out when scheduled jobs run – in local time, as the user set them up.</summary>
public static class ScheduleCalendar
{
    /// <summary>How far to look for a schedule's next or previous time: every schedule runs at least once in two months.</summary>
    private const int SearchDays = 62;

    /// <summary>The first time after <paramref name="after"/> any of the schedules starts the job; null without schedules.</summary>
    public static DateTime? Next(IEnumerable<BackupSchedule> schedules, DateTime after)
    {
        DateTime? next = null;
        foreach (var schedule in schedules.Where(schedule => schedule.IsValid))
        {
            for (var day = after.Date; day <= after.Date.AddDays(SearchDays); day = day.AddDays(1))
            {
                if (!schedule.RunsOn(day))
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
            for (var day = now.Date; day >= now.Date.AddDays(-SearchDays); day = day.AddDays(-1))
            {
                if (!schedule.RunsOn(day))
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
    public static bool IsMissed(BackupJob job, DateTime now) =>
        IsMissed(job.Schedules, job.CatchUp, job.LastRun?.Started.LocalDateTime, now);

    /// <summary>Whether a scheduled time has passed since <paramref name="lastRun"/> (for anything that catches up).</summary>
    public static bool IsMissed(IEnumerable<BackupSchedule> schedules, bool catchUp, DateTime? lastRun, DateTime now) =>
        catchUp && Previous(schedules, now) is { } due && (lastRun is null || lastRun < due);

    /// <summary>How late a run may start and still count as on time (the scheduler looks every half minute).</summary>
    public static readonly TimeSpan OnTime = TimeSpan.FromMinutes(5);

    /// <summary>
    /// For a scheduler that looks now and then (BorgStudio in the background): whether a scheduled time of the job lies
    /// after <paramref name="since"/> (the last look) and at or before <paramref name="now"/>. A time long past (the
    /// computer slept) only counts for jobs that catch up.
    /// </summary>
    public static bool IsDue(BackupJob job, DateTime since, DateTime now) => IsDue(job.Schedules, job.CatchUp, since, now);

    public static bool IsDue(IEnumerable<BackupSchedule> schedules, bool catchUp, DateTime since, DateTime now)
    {
        if (Previous(schedules, now) is not { } due || due <= since)
            return false;
        return catchUp || now - due <= OnTime;
    }

    /// <summary>
    /// For a run the system started (launchd can only start monthly runs weekly or on several days): whether one of the
    /// schedules' times lies within the last day.
    /// </summary>
    public static bool IsScheduledNow(IEnumerable<BackupSchedule> schedules, DateTime now) =>
        Previous(schedules, now) is { } due && now - due < TimeSpan.FromDays(1);
}
