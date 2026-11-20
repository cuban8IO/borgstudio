using BorgStudio.Core.Backups;

namespace BorgStudio.Core.Storage;

/// <summary>What a forecast is based on.</summary>
public enum ForecastBasis
{
    /// <summary>The trend of the measurements (linear, last 90 days).</summary>
    Trend,

    /// <summary>Not enough measurements yet: what the backups added, minus what clean-ups freed (last 30 days).</summary>
    Backups,
}

/// <summary>
/// How fast a repository's storage grows and when it will be full at that pace: <see cref="FullAt"/> with a range
/// (<see cref="Earliest"/>..<see cref="Latest"/>; <c>null</c> ends mean "not foreseeable"). Without a known total there
/// is a growth rate but no date.
/// </summary>
public sealed record StorageForecast(double BytesPerDay, ForecastBasis Basis, DateTimeOffset? FullAt, DateTimeOffset? Earliest,
    DateTimeOffset? Latest)
{
    /// <summary>Points over at least this many days make a trend.</summary>
    public const double MinimumTrendDays = 2;

    public static readonly TimeSpan TrendWindow = TimeSpan.FromDays(90);

    public static readonly TimeSpan BackupWindow = TimeSpan.FromDays(30);

    /// <summary>Further than this is "not foreseeable".</summary>
    public static readonly TimeSpan Horizon = TimeSpan.FromDays(3650);

    public double BytesPerWeek => BytesPerDay * 7;

    /// <summary>The storage does not grow (or shrinks).</summary>
    public bool Growing => BytesPerDay > 0;

    /// <param name="history">The repository's measurements, oldest first.</param>
    /// <param name="current">The last measurement (its free space decides the date).</param>
    /// <param name="runs">The runs of the repository's jobs (for the fallback before there is a trend).</param>
    /// <returns><c>null</c> if there is nothing to go by.</returns>
    public static StorageForecast? Compute(IReadOnlyList<StoragePoint> history, StorageUsage? current, IEnumerable<BackupRunSummary> runs,
        DateTimeOffset now)
    {
        var free = current?.Total is not null ? current.Free : null;
        return Trend(history, now) is { } trend
            ? Build(trend.PerDay, trend.Error, ForecastBasis.Trend, free, now)
            : FromBackups(runs, now) is { } perDay
                // A rough estimate: half as fast up to half again.
                ? Build(perDay, Math.Abs(perDay) / 2, ForecastBasis.Backups, free, now)
                : null;
    }

    /// <summary>Least squares over the last 90 days: bytes per day and its standard error.</summary>
    internal static (double PerDay, double Error)? Trend(IReadOnlyList<StoragePoint> history, DateTimeOffset now)
    {
        var points = history
            .Where(point => now - point.Measured <= TrendWindow && point.GrowingBytes is not null)
            .Select(point => (X: (point.Measured - now).TotalDays, Y: (double)point.GrowingBytes!.Value))
            .ToList();
        if (points.Count < 3 || points.Max(point => point.X) - points.Min(point => point.X) < MinimumTrendDays)
            return null;

        var meanX = points.Average(point => point.X);
        var meanY = points.Average(point => point.Y);
        var sxx = points.Sum(point => (point.X - meanX) * (point.X - meanX));
        var sxy = points.Sum(point => (point.X - meanX) * (point.Y - meanY));
        var slope = sxy / sxx;
        var intercept = meanY - slope * meanX;
        var residuals = points.Sum(point => Math.Pow(point.Y - (intercept + slope * point.X), 2));
        var error = points.Count > 2 ? Math.Sqrt(residuals / (points.Count - 2) / sxx) : 0;
        return (slope, error);
    }

    /// <summary>What the backups of the last 30 days added (deduplicated) minus what clean-ups freed, per day.</summary>
    internal static double? FromBackups(IEnumerable<BackupRunSummary> runs, DateTimeOffset now)
    {
        var recent = runs.Where(run => now - run.Started <= BackupWindow && run.Outcome is BackupOutcome.Succeeded or BackupOutcome.Warnings)
            .ToList();
        if (recent.Count == 0)
            return null;
        var added = recent.Sum(run => (double)run.DeduplicatedSize) - recent.Sum(run => (double)(run.Cleanup?.FreedBytes ?? 0));
        // At least a week: two backups an hour apart say nothing about a month.
        var days = Math.Max(7, (now - recent.Min(run => run.Started)).TotalDays);
        return added / days;
    }

    private static StorageForecast Build(double perDay, double error, ForecastBasis basis, long? free, DateTimeOffset now)
    {
        DateTimeOffset? At(double rate) =>
            free is { } bytes && rate > 0 && bytes / rate is var days && days <= Horizon.TotalDays ? now.AddDays(days) : null;

        return new StorageForecast(perDay, basis, At(perDay), At(perDay + error), At(perDay - error));
    }
}
