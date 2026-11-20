using System.Text.Json;
using BorgStudio.Core.Repositories;

namespace BorgStudio.Core.Storage;

/// <summary>One measurement of a repository's storage, as kept over time.</summary>
public sealed record StoragePoint
{
    public required DateTimeOffset Measured { get; init; }

    public long? Total { get; init; }

    public long? Free { get; init; }

    /// <summary>What the repository itself took up, if known.</summary>
    public long? RepositoryBytes { get; init; }

    /// <summary>What grows towards full: the used space, or – without total – the repository.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public long? GrowingBytes => Total is { } total && Free is { } free ? Math.Max(0, total - free) : RepositoryBytes;

    public static StoragePoint From(StorageUsage usage) => new()
    {
        Measured = usage.Measured,
        Total = usage.Total,
        Free = usage.Free,
        RepositoryBytes = usage.RepositoryBytes,
    };
}

/// <summary>
/// The measurements of each repository over time (storage-history/&lt;repository&gt;.json): the last one per day, for two
/// years – enough for trends and the "full by" forecast without growing without end.
/// </summary>
public sealed class StorageHistory(string directory)
{
    public static readonly TimeSpan Kept = TimeSpan.FromDays(730);

    private string FileOf(Guid repositoryId) => Path.Combine(directory, $"{repositoryId:N}.json");

    /// <summary>Oldest first.</summary>
    public IReadOnlyList<StoragePoint> Get(Guid repositoryId)
    {
        var file = FileOf(repositoryId);
        if (!File.Exists(file))
            return [];
        try
        {
            return JsonSerializer.Deserialize<List<StoragePoint>>(File.ReadAllText(file), RepositoryStore.JsonOptions) ?? [];
        }
        catch (Exception exception) when (exception is JsonException or IOException)
        {
            return [];
        }
    }

    /// <summary>Adds a measurement; one of the same (local) day is replaced, points older than two years go.</summary>
    public void Add(Guid repositoryId, StorageUsage usage)
    {
        var point = StoragePoint.From(usage);
        if (point.GrowingBytes is null)
            return;
        var day = point.Measured.ToLocalTime().Date;
        var points = Get(repositoryId)
            .Where(existing => existing.Measured.ToLocalTime().Date != day && point.Measured - existing.Measured < Kept)
            .Append(point)
            .OrderBy(existing => existing.Measured)
            .ToList();
        try
        {
            AtomicFile.WriteAllText(FileOf(repositoryId), JsonSerializer.Serialize(points, RepositoryStore.JsonOptions));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The trend just misses a day.
        }
    }

    public void Remove(Guid repositoryId)
    {
        var file = FileOf(repositoryId);
        if (File.Exists(file))
            File.Delete(file);
    }
}
