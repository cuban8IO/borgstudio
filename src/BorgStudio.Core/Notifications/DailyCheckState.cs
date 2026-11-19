using System.Text.Json;
using BorgStudio.Core.Backups;

namespace BorgStudio.Core.Notifications;

/// <summary>
/// When the daily check last ran, and since when a job without any successful backup is known (it counts as overdue from
/// then). Kept in notifications/daily.json, shared by the window, the background and the system's scheduler.
/// </summary>
public sealed class DailyCheckState(string filePath)
{
    public static DailyCheckState ForDataDirectory(string dataDirectory) => new(Path.Combine(dataDirectory, "notifications", "daily.json"));

    private static readonly object Gate = new();

    public DateTimeOffset? LastCheck
    {
        get
        {
            lock (Gate)
                return Load().LastCheck;
        }
    }

    /// <summary>
    /// Whether the check of <paramref name="time"/> is due at <paramref name="now"/>: its last occurrence (today or yesterday)
    /// came after the last check. Never checked yet: not due – the first check is the next occurrence.
    /// </summary>
    public bool IsDue(TimeOnly time, DateTime now)
    {
        var last = LastCheck;
        if (last is null)
        {
            Record(now);
            return false;
        }
        var occurrence = now.Date + time.ToTimeSpan();
        if (occurrence > now)
            occurrence = occurrence.AddDays(-1);
        return last.Value.LocalDateTime < occurrence;
    }

    /// <summary>The check ran at <paramref name="now"/>.</summary>
    public void Record(DateTime now)
    {
        lock (Gate)
        {
            var contents = Load();
            Save(contents with { LastCheck = new DateTimeOffset(now) });
        }
    }

    /// <summary>
    /// The jobs overdue at <paramref name="now"/>: with <see cref="BackupJob.OverdueDays"/> set and no successful backup
    /// for longer (by their history; a job that never backed up since it was first seen here).
    /// </summary>
    public IReadOnlyList<(BackupJob Job, DateTimeOffset Since)> Overdue(IEnumerable<BackupJob> jobs, BackupRunHistory history, DateTime now)
    {
        lock (Gate)
        {
            var contents = Load();
            var changed = false;
            var overdue = new List<(BackupJob, DateTimeOffset)>();
            foreach (var job in jobs.Where(job => job.OverdueDays > 0))
            {
                var key = job.Id.ToString("N");
                var since = LastSuccess(job, history);
                if (since is null)
                {
                    if (!contents.FirstSeen.TryGetValue(key, out var seen))
                    {
                        seen = new DateTimeOffset(now);
                        contents.FirstSeen[key] = seen;
                        changed = true;
                    }
                    since = seen;
                }
                if (new DateTimeOffset(now) - since.Value > TimeSpan.FromDays(job.OverdueDays))
                    overdue.Add((job, since.Value));
            }
            if (changed)
                Save(contents);
            return overdue;
        }
    }

    /// <summary>The start of the job's last backup that worked (with or without warnings).</summary>
    public static DateTimeOffset? LastSuccess(BackupJob job, BackupRunHistory history) =>
        history.Get(job.Id).Append(job.LastRun).OfType<BackupRunSummary>()
            .Where(run => run.Outcome is BackupOutcome.Succeeded or BackupOutcome.Warnings)
            .Select(run => (DateTimeOffset?)run.Started)
            .Max();

    private sealed record Contents
    {
        public DateTimeOffset? LastCheck { get; init; }

        public Dictionary<string, DateTimeOffset> FirstSeen { get; init; } = [];
    }

    private Contents Load()
    {
        try
        {
            return File.Exists(filePath) ? JsonSerializer.Deserialize<Contents>(File.ReadAllText(filePath)) ?? new Contents() : new Contents();
        }
        catch (Exception exception) when (exception is JsonException or IOException)
        {
            return new Contents();
        }
    }

    private void Save(Contents contents)
    {
        try
        {
            AtomicFile.WriteAllText(filePath, JsonSerializer.Serialize(contents));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Then the check may run once more.
        }
    }
}
