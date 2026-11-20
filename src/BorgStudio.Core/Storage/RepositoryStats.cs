using System.Text.Json;
using System.Text.Json.Serialization;

namespace BorgStudio.Core.Storage;

/// <summary>
/// borg's statistics over all archives of a repository (the "cache.stats" of <c>borg info</c> and <c>borg create --json</c>):
/// what the archives hold, compressed, and what that takes up once deduplicated.
/// </summary>
public sealed record RepositoryStats
{
    /// <summary>The archives' files added up, as they are on disk.</summary>
    public required long TotalSize { get; init; }

    /// <summary>The same, compressed.</summary>
    public required long TotalCompressedSize { get; init; }

    /// <summary>Each chunk once, uncompressed.</summary>
    public required long UniqueSize { get; init; }

    /// <summary>Each chunk once, compressed: what the repository takes up.</summary>
    public required long UniqueCompressedSize { get; init; }

    /// <summary>How much smaller compression makes the data (2.0 = half); <c>null</c> without data.</summary>
    [JsonIgnore]
    public double? CompressionRatio => TotalCompressedSize > 0 ? (double)TotalSize / TotalCompressedSize : null;

    /// <summary>How many times each stored chunk is used across the archives (18 = 18 archives share it); <c>null</c> without data.</summary>
    [JsonIgnore]
    public double? DeduplicationRatio => UniqueCompressedSize > 0 ? (double)TotalCompressedSize / UniqueCompressedSize : null;

    /// <summary>What compression and deduplication save together (0..1): 0.94 = the archives would need 16× the space.</summary>
    [JsonIgnore]
    public double? Savings => TotalSize > 0 ? Math.Clamp(1 - (double)UniqueCompressedSize / TotalSize, 0, 1) : null;

    /// <summary>The statistics in borg's JSON output (its "cache" object); <c>null</c> if borg left them out.</summary>
    public static RepositoryStats? FromBorgJson(JsonElement root)
    {
        if (!root.TryGetProperty("cache", out var cache) || !cache.TryGetProperty("stats", out var stats))
            return null;
        long? Read(string name) => stats.TryGetProperty(name, out var value) && value.TryGetInt64(out var number) ? number : null;
        return Read("total_size") is { } total && Read("total_csize") is { } totalCompressed
               && Read("unique_size") is { } unique && Read("unique_csize") is { } uniqueCompressed
            ? new RepositoryStats
            {
                TotalSize = total,
                TotalCompressedSize = totalCompressed,
                UniqueSize = unique,
                UniqueCompressedSize = uniqueCompressed,
            }
            : null;
    }
}
