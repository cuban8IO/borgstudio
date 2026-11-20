using System.Text.Json;
using BorgStudio.Core.Backups;
using BorgStudio.Core.Borg;
using BorgStudio.Core.Storage;
using BorgStudio.Tests.TestSupport;

namespace BorgStudio.Tests.Storage;

/// <summary>borg's repository statistics, the storage history and the "full by" forecast.</summary>
public sealed class ForecastTests : IDisposable
{
    private const long Gigabyte = 1024L * 1024 * 1024;

    private static readonly DateTimeOffset Now = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);

    private readonly TemporaryDirectory _directory = new();

    public void Dispose() => _directory.Dispose();

    private static StoragePoint Point(double daysAgo, double usedGigabytes, long totalGigabytes = 1000) => new()
    {
        Measured = Now.AddDays(-daysAgo),
        Total = totalGigabytes * Gigabyte,
        Free = (long)((totalGigabytes - usedGigabytes) * Gigabyte),
    };

    private static StorageUsage Current(double usedGigabytes, long totalGigabytes = 1000) => new()
    {
        Measured = Now,
        Source = StorageSource.Disk,
        Total = totalGigabytes * Gigabyte,
        Free = (long)((totalGigabytes - usedGigabytes) * Gigabyte),
    };

    // --- borg's statistics ---

    [Fact]
    public void Borg_reports_the_repository_statistics_with_info_and_create()
    {
        const string cache = "\"cache\": {\"stats\": {\"total_size\": 1000, \"total_csize\": 500, \"unique_size\": 100, \"unique_csize\": 50}}";
        var info = BorgClient.ParseInfo("{\"encryption\": {\"mode\": \"none\"}, \"repository\": {\"id\": \"1\"}, " + cache + "}");
        Assert.Equal(new RepositoryStats { TotalSize = 1000, TotalCompressedSize = 500, UniqueSize = 100, UniqueCompressedSize = 50 }, info.Stats);
        Assert.Equal(2.0, info.Stats!.CompressionRatio);
        Assert.Equal(10.0, info.Stats.DeduplicationRatio);
        Assert.Equal(0.95, info.Stats.Savings!.Value, 3);

        var created = BorgClient.ParseCreate(
            "{\"archive\": {\"name\": \"a\", \"duration\": 1.5, \"stats\": {\"original_size\": 10, \"compressed_size\": 8, \"deduplicated_size\": 2, \"nfiles\": 3}}, "
            + cache + "}");
        Assert.Equal(50, created.Repository!.UniqueCompressedSize);

        using var none = JsonDocument.Parse("""{"cache": {"stats": {"total_size": 1}}}""");
        Assert.Null(RepositoryStats.FromBorgJson(none.RootElement));
    }

    // --- History ---

    [Fact]
    public void The_history_keeps_one_measurement_a_day_for_two_years()
    {
        var store = StorageUsageStore.ForDataDirectory(_directory.Path);
        var repository = Guid.NewGuid();
        var stats = new RepositoryStats { TotalSize = 10, TotalCompressedSize = 5, UniqueSize = 2, UniqueCompressedSize = 1 };

        store.Set(repository, Current(100) with { Measured = Now.AddDays(-800) });
        store.Set(repository, Current(500) with { Measured = Now.AddDays(-1), Repository = stats });
        store.Set(repository, Current(600) with { Measured = Now.AddHours(-1) });
        store.Set(repository, Current(610));

        var points = store.History.Get(repository);
        // The day-old one and today's last one; the one from two years ago has gone.
        Assert.Equal([500 * Gigabyte, 610 * Gigabyte], points.Select(point => point.GrowingBytes));
        // A measurement without statistics keeps the ones borg reported before.
        Assert.Equal(stats, store.Get(repository)!.Repository);

        store.Remove(repository);
        Assert.Empty(store.History.Get(repository));
    }

    // --- Forecast ---

    [Fact]
    public void A_steady_trend_tells_when_the_storage_will_be_full()
    {
        // 10 GB a week for 12 weeks, now 700 of 1000 GB: full in 30 weeks.
        var history = Enumerable.Range(0, 13).Select(week => Point(84 - week * 7, 580 + week * 10)).ToList();

        var forecast = StorageForecast.Compute(history, Current(700), [], Now)!;

        Assert.Equal(ForecastBasis.Trend, forecast.Basis);
        Assert.Equal(10 * Gigabyte, forecast.BytesPerWeek, 1e6);
        Assert.Equal(Now.AddDays(210).Date, forecast.FullAt!.Value.Date);
        // Exactly on the line: no spread.
        Assert.Equal(forecast.FullAt.Value.Date, forecast.Earliest!.Value.Date);
    }

    [Fact]
    public void A_noisy_trend_gives_a_range_and_a_shrinking_one_no_date()
    {
        var noisy = Enumerable.Range(0, 13).Select(week => Point(84 - week * 7, 580 + week * 10 + (week % 2 == 0 ? 15 : -15))).ToList();
        var forecast = StorageForecast.Compute(noisy, Current(700), [], Now)!;
        Assert.True(forecast.Earliest < forecast.FullAt);
        Assert.True(forecast.Latest is null || forecast.Latest > forecast.FullAt);

        var shrinking = Enumerable.Range(0, 5).Select(day => Point(10 - day * 2, 700 - day * 5)).ToList();
        var flat = StorageForecast.Compute(shrinking, Current(680), [], Now)!;
        Assert.False(flat.Growing);
        Assert.Null(flat.FullAt);
    }

    [Fact]
    public void Without_a_trend_the_backups_give_an_estimate()
    {
        BackupRunSummary Run(double daysAgo, long added, long freed = 0) => new()
        {
            Started = Now.AddDays(-daysAgo),
            Duration = TimeSpan.Zero,
            Outcome = BackupOutcome.Succeeded,
            DeduplicatedSize = added,
            Cleanup = freed > 0 ? new CleanupSummary { PrunedArchives = 1, FreedBytes = freed } : null,
        };
        // Two measurements only, but backups: 14 GB added, 4 GB freed in 14 days.
        BackupRunSummary[] runs = [Run(14, 7 * Gigabyte), Run(7, 7 * Gigabyte, 4 * Gigabyte), Run(40, 100 * Gigabyte)];

        var forecast = StorageForecast.Compute([Point(1, 690), Point(0, 700)], Current(700), runs, Now)!;

        Assert.Equal(ForecastBasis.Backups, forecast.Basis);
        Assert.Equal(5 * Gigabyte, forecast.BytesPerWeek, 1e6);
        Assert.NotNull(forecast.FullAt);

        // Without total (the repository's size only): a growth, no date.
        var sizeOnly = StorageForecast.Compute([], new StorageUsage { Measured = Now, Source = StorageSource.RepositoryOnly, RepositorySize = 1 }, runs, Now)!;
        Assert.True(sizeOnly.Growing);
        Assert.Null(sizeOnly.FullAt);

        Assert.Null(StorageForecast.Compute([], Current(700), [], Now));
    }
}
