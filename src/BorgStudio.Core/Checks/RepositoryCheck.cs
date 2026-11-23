using System.Text.Json;
using System.Text.Json.Serialization;
using BorgStudio.Core.Repositories;

namespace BorgStudio.Core.Checks;

/// <summary>How thoroughly <c>borg check</c> looks.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CheckDepth>))]
public enum CheckDepth
{
    /// <summary>The repository's segments and the archives' metadata (over SSH the server reads the segments itself).</summary>
    Normal,

    /// <summary>Also downloads and decrypts all data (<c>--verify-data</c>) – as much traffic as the repository is big.</summary>
    Full,

    /// <summary>Repairs what it can (<c>--repair</c>); may lose damaged data.</summary>
    Repair,
}

[JsonConverter(typeof(JsonStringEnumConverter<CheckOutcome>))]
public enum CheckOutcome
{
    /// <summary>No damage found.</summary>
    Ok,

    /// <summary>borg found damage.</summary>
    ProblemsFound,

    /// <summary>A repair ran (it found and fixed what it could; a check afterwards tells whether all is well).</summary>
    Repaired,

    /// <summary>The check could not run (e.g. no connection, wrong passphrase).</summary>
    Failed,

    /// <summary>Stopped by the user.</summary>
    Cancelled,
}

/// <summary>One check of a repository.</summary>
/// <param name="Messages">What borg reported (errors and warnings), for the log.</param>
/// <param name="Error">Why the check could not run (<see cref="CheckOutcome.Failed"/>).</param>
public sealed record CheckRecord(DateTimeOffset Started, TimeSpan Duration, CheckDepth Depth, CheckOutcome Outcome)
{
    public IReadOnlyList<string> Messages { get; init; } = [];

    public string? Error { get; init; }

    /// <summary>Started by itself (after a backup or by a schedule), not by hand.</summary>
    public bool Automatic { get; init; }

    /// <summary>The check ran to its end and tells the repository's state (a repair is followed by a check that does).</summary>
    [JsonIgnore]
    public bool IsVerdict => Outcome is CheckOutcome.Ok or CheckOutcome.ProblemsFound;
}

/// <summary>When a repository is checked by itself – always a normal check (a thorough one only by hand).</summary>
public sealed record CheckPolicy
{
    /// <summary>After a backup to the repository, if it was not checked for <see cref="IntervalDays"/>.</summary>
    public bool AfterBackup { get; init; }

    public int IntervalDays { get; init; } = 30;

    /// <summary>At set times, by the system's scheduler or BorgStudio in the background.</summary>
    public List<Backups.BackupSchedule> Schedules { get; init; } = [];

    /// <summary>A scheduled check missed while the computer was off follows when it is back.</summary>
    public bool CatchUp { get; init; } = true;

    [JsonIgnore]
    public bool IsScheduled => Schedules.Any(schedule => schedule.IsValid);

    [JsonIgnore]
    public bool IsAutomatic => AfterBackup || IsScheduled;

    /// <summary>Equal by value, the schedules included (a loaded policy equals the saved one).</summary>
    public bool Equals(CheckPolicy? other) =>
        other is not null && AfterBackup == other.AfterBackup && IntervalDays == other.IntervalDays && CatchUp == other.CatchUp
        && Schedules.Count == other.Schedules.Count
        && Schedules.Zip(other.Schedules).All(pair => pair.First with { Days = NoDays } == pair.Second with { Days = NoDays }
            && pair.First.Days.SequenceEqual(pair.Second.Days));

    /// <summary>One list for both sides, so the records compare everything but their days.</summary>
    private static readonly List<DayOfWeek> NoDays = [];

    public override int GetHashCode() => HashCode.Combine(AfterBackup, IntervalDays, CatchUp, Schedules.Count);

    /// <summary>Whether a backup that just finished should be followed by a check.</summary>
    public bool IsDueAfterBackup(CheckRecord? lastVerdict, DateTimeOffset now) =>
        AfterBackup && (lastVerdict is null || now - lastVerdict.Started >= TimeSpan.FromDays(Math.Max(1, IntervalDays)));
}

public enum CheckHealthReason
{
    Fine,

    /// <summary>The last completed check found damage.</summary>
    Damaged,

    /// <summary>The last automatic check could not run (e.g. no connection).</summary>
    NotRun,

    /// <summary>
    /// A scheduled check is more than a day late, or the last check is long older than the interval. A repository never
    /// checked is not judged (it shows as "never checked").
    /// </summary>
    Overdue,
}

/// <summary>How a repository is doing as far as its checks tell.</summary>
public static class CheckHealth
{
    /// <summary>Time a check gets after its scheduled time before it counts as overdue (checks can take hours).</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromDays(1);

    /// <summary>With checks after backups only: a check older than the interval plus this counts as overdue.</summary>
    public static readonly TimeSpan AfterBackupGrace = TimeSpan.FromDays(7);

    /// <param name="checks">Newest first.</param>
    public static (Backups.HealthLevel Level, CheckHealthReason Reason) Evaluate(CheckPolicy policy, IReadOnlyList<CheckRecord> checks, DateTime now)
    {
        var verdict = checks.FirstOrDefault(check => check.IsVerdict);
        if (verdict?.Outcome == CheckOutcome.ProblemsFound)
            return (Backups.HealthLevel.Critical, CheckHealthReason.Damaged);
        if (checks.FirstOrDefault() is { Automatic: true, Outcome: CheckOutcome.Failed })
            return (Backups.HealthLevel.Warning, CheckHealthReason.NotRun);

        var last = verdict?.Started.LocalDateTime;
        if (policy.IsScheduled && Backups.ScheduleCalendar.Previous(policy.Schedules, now - Grace) is { } due && last is { } checkedAt && checkedAt < due)
            return (Backups.HealthLevel.Warning, CheckHealthReason.Overdue);
        if (policy.AfterBackup && last is { } lastCheck && now - lastCheck > TimeSpan.FromDays(Math.Max(1, policy.IntervalDays)) + AfterBackupGrace)
            return (Backups.HealthLevel.Warning, CheckHealthReason.Overdue);
        return (verdict is null ? Backups.HealthLevel.Info : Backups.HealthLevel.Ok, CheckHealthReason.Fine);
    }
}

/// <summary>
/// The checks of each repository, newest first, in checks/&lt;repository&gt;.json in the data folder (the last
/// <see cref="MaximumRecords"/>). Shared by the window and scheduled runs in other processes.
/// </summary>
public sealed class RepositoryCheckStore(string directory)
{
    public const int MaximumRecords = 50;

    private static readonly object Gate = new();

    public static RepositoryCheckStore ForDataDirectory(string dataDirectory) => new(Path.Combine(dataDirectory, "checks"));

    public IReadOnlyList<CheckRecord> Get(Guid repositoryId)
    {
        lock (Gate)
            return Load(repositoryId);
    }

    /// <summary>The last check that ran to its end, i.e. the repository's known state; <c>null</c> if never checked.</summary>
    public CheckRecord? LastVerdict(Guid repositoryId) => Get(repositoryId).FirstOrDefault(record => record.IsVerdict);

    public void Add(Guid repositoryId, CheckRecord record)
    {
        lock (Gate)
        {
            List<CheckRecord> records = [record, .. Load(repositoryId)];
            try
            {
                AtomicFile.WriteAllText(FilePath(repositoryId),
                    JsonSerializer.Serialize(records.OrderByDescending(entry => entry.Started).Take(MaximumRecords), RepositoryStore.JsonOptions));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // The check itself is done; only its record is missing.
            }
        }
    }

    public void Remove(Guid repositoryId)
    {
        lock (Gate)
        {
            try
            {
                File.Delete(FilePath(repositoryId));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private string FilePath(Guid repositoryId) => Path.Combine(directory, $"{repositoryId:N}.json");

    private List<CheckRecord> Load(Guid repositoryId)
    {
        var path = FilePath(repositoryId);
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<List<CheckRecord>>(File.ReadAllText(path), RepositoryStore.JsonOptions) ?? []
                : [];
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
