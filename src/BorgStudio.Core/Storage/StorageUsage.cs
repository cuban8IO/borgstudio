using System.Text.Json;
using System.Text.Json.Serialization;
using BorgStudio.Core.Backups;
using BorgStudio.Core.Repositories;

namespace BorgStudio.Core.Storage;

/// <summary>Where a measurement comes from.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<StorageSource>))]
public enum StorageSource
{
    /// <summary>The local disk the repository is on.</summary>
    Disk,

    /// <summary><c>df</c> on the repository's server.</summary>
    Server,

    /// <summary>The repository's size (borg) against the capacity entered for it.</summary>
    Capacity,

    /// <summary>Only the repository's size: neither the disk nor a capacity is known.</summary>
    RepositoryOnly,
}

/// <summary>How full a repository's storage is, as last measured.</summary>
public sealed record StorageUsage
{
    public required DateTimeOffset Measured { get; init; }

    public required StorageSource Source { get; init; }

    /// <summary>The size of the disk, server share or entered capacity; <c>null</c> if unknown.</summary>
    public long? Total { get; init; }

    public long? Free { get; init; }

    /// <summary>What the repository itself takes up (borg), where it was asked.</summary>
    public long? RepositorySize { get; init; }

    /// <summary>borg's statistics of the repository, as last reported (kept from earlier when the disk was measured alone).</summary>
    public RepositoryStats? Repository { get; init; }

    /// <summary>What the repository takes up: measured with borg, or from its last statistics.</summary>
    [JsonIgnore]
    public long? RepositoryBytes => RepositorySize ?? Repository?.UniqueCompressedSize;

    [JsonIgnore]
    public long? Used => Total is { } total && Free is { } free ? Math.Max(0, total - free) : null;

    /// <summary>Taken up by other data next to the repository (same disk or share); <c>null</c> if unknown.</summary>
    [JsonIgnore]
    public long? OtherBytes => Source is StorageSource.Disk or StorageSource.Server && Used is { } used && RepositoryBytes is { } repository
        ? Math.Max(0, used - repository)
        : null;

    /// <summary>What grows towards full: the used space (disk, share, capacity), or – without total – the repository.</summary>
    [JsonIgnore]
    public long? GrowingBytes => Used ?? RepositoryBytes;

    /// <summary>Free share 0..1; <c>null</c> without total.</summary>
    [JsonIgnore]
    public double? FreeFraction => Total is { } total && total > 0 && Free is { } free ? Math.Clamp((double)free / total, 0, 1) : null;
}

/// <summary>When storage counts as full (each switchable; at least one should stay on).</summary>
public sealed record StorageFullCriteria
{
    /// <summary>Less than 1 % free.</summary>
    public bool BelowOnePercent { get; init; } = true;

    /// <summary>Less free than the biggest last backup of the repository's jobs added.</summary>
    public bool NextBackupWontFit { get; init; } = true;

    /// <summary>A backup failed for lack of space (until a job of the repository backs up again).</summary>
    public bool BackupFailed { get; init; } = true;
}

public enum StorageLevel
{
    Ok,

    /// <summary>Less free than the repository's warning threshold.</summary>
    Low,

    Full,
}

public static class StorageLevels
{
    public const double OnePercent = 0.01;

    /// <param name="jobs">The repository's jobs: their last runs tell what the next backup adds and whether one ran out of space.</param>
    public static StorageLevel Evaluate(StorageUsage? usage, RepositoryConfig repository, IEnumerable<BackupJob> jobs, StorageFullCriteria criteria)
    {
        var runs = jobs.Where(job => job.RepositoryId == repository.Id).Select(job => job.LastRun).OfType<BackupRunSummary>().ToList();
        if (criteria.BackupFailed && runs.Any(run => run.OutOfSpace))
            return StorageLevel.Full;
        if (usage?.FreeFraction is not { } free)
            return StorageLevel.Ok;

        if (criteria.BelowOnePercent && free < OnePercent)
            return StorageLevel.Full;
        var next = runs.Where(run => run.Outcome is BackupOutcome.Succeeded or BackupOutcome.Warnings)
            .Select(run => run.DeduplicatedSize).DefaultIfEmpty(0).Max();
        if (criteria.NextBackupWontFit && next > 0 && usage.Free < next)
            return StorageLevel.Full;
        return repository.StorageWarnPercent > 0 && free * 100 < repository.StorageWarnPercent ? StorageLevel.Low : StorageLevel.Ok;
    }
}

/// <summary>
/// The last measurement per repository, in storage.json (shared by the window and scheduled runs), and every measurement
/// in the repository's <see cref="StorageHistory"/>.
/// </summary>
public sealed class StorageUsageStore(string filePath)
{
    public static StorageUsageStore ForDataDirectory(string dataDirectory) => new(Path.Combine(dataDirectory, "storage.json"));

    private static readonly object Gate = new();

    /// <summary>The measurements over time, next to storage.json.</summary>
    public StorageHistory History { get; } = new(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(filePath))!, "storage-history"));

    public StorageUsage? Get(Guid repositoryId)
    {
        lock (Gate)
            return Load().GetValueOrDefault(repositoryId.ToString("N"));
    }

    /// <summary>
    /// Keeps <paramref name="usage"/> as the repository's last measurement (with the statistics borg reported before, if it
    /// brings none) and adds it to the history.
    /// </summary>
    public void Set(Guid repositoryId, StorageUsage usage)
    {
        lock (Gate)
        {
            var all = Load();
            var key = repositoryId.ToString("N");
            if (usage.Repository is null && all.GetValueOrDefault(key)?.Repository is { } earlier)
                usage = usage with { Repository = earlier };
            all[key] = usage;
            Save(all);
            History.Add(repositoryId, usage);
        }
    }

    /// <summary>borg reported new statistics (e.g. with a backup): kept with the last measurement.</summary>
    public void SetStats(Guid repositoryId, RepositoryStats stats)
    {
        lock (Gate)
        {
            var all = Load();
            var key = repositoryId.ToString("N");
            if (all.GetValueOrDefault(key) is not { } usage)
                return;
            all[key] = usage with { Repository = stats };
            Save(all);
        }
    }

    public void Remove(Guid repositoryId)
    {
        lock (Gate)
        {
            var all = Load();
            if (all.Remove(repositoryId.ToString("N")))
                Save(all);
            History.Remove(repositoryId);
        }
    }

    private Dictionary<string, StorageUsage> Load()
    {
        try
        {
            return File.Exists(filePath)
                ? JsonSerializer.Deserialize<Dictionary<string, StorageUsage>>(File.ReadAllText(filePath), RepositoryStore.JsonOptions) ?? []
                : [];
        }
        catch (Exception exception) when (exception is JsonException or IOException)
        {
            return [];
        }
    }

    private void Save(Dictionary<string, StorageUsage> all)
    {
        try
        {
            AtomicFile.WriteAllText(filePath, JsonSerializer.Serialize(all, RepositoryStore.JsonOptions));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Measured again after the next backup.
        }
    }
}
