using System.Globalization;
using BorgStudio.App.Controls;
using BorgStudio.App.Resources;
using BorgStudio.Core.Backups;
using BorgStudio.Core.Storage;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BorgStudio.App.ViewModels;

/// <summary>Everything the statistics of a repository are made of (collected by the main window).</summary>
/// <param name="Runs">Each job with its runs (newest first, as the history keeps them).</param>
/// <param name="Archives">How many archives the repository has, if the list was loaded once.</param>
public sealed record RepositoryStatisticsData(
    StorageUsage? Usage,
    IReadOnlyList<StoragePoint> History,
    StorageForecast? Forecast,
    IReadOnlyList<(BackupJob Job, IReadOnlyList<BackupRunSummary> Runs)> Runs,
    int? Archives,
    DateTimeOffset Now);

/// <summary>
/// The "Statistik" tab of a repository: its storage over time with the forecast, what each backup newly stored, how
/// reliable and how fast the backups are, what deduplication and compression save, and the activity of the last weeks.
/// </summary>
public sealed partial class RepositoryStatisticsViewModel : ObservableObject
{
    /// <summary>Backups shown as columns.</summary>
    public const int ColumnCount = 30;

    /// <summary>Weeks in the activity calendar.</summary>
    public const int CalendarWeeks = 12;

    /// <summary>How far the forecast line reaches at most.</summary>
    public static readonly TimeSpan ProjectionReach = TimeSpan.FromDays(26 * 7);

    private readonly RepositoryStatisticsData _data;

    public RepositoryStatisticsViewModel(RepositoryStatisticsData data)
    {
        _data = data;
        JobOptions =
        [
            new Option<Guid?>(null, Strings.StatsAllJobs),
            .. data.Runs.Select(entry => new Option<Guid?>(entry.Job.Id, entry.Job.Name)),
        ];
        SelectedJob = JobOptions[0];

        (StoragePoints, Projection) = StorageSeries(data);
        Days = Calendar(data);
    }

    // --- Storage over time ---

    public IReadOnlyList<ChartPoint> StoragePoints { get; }

    public IReadOnlyList<ChartPoint> Projection { get; }

    public bool HasStorageChart => StoragePoints.Count >= 2;

    /// <summary>The total the storage grows towards (disk, share or capacity).</summary>
    public double? Limit => _data.Usage?.Total;

    public string LimitLabel => _data.Usage?.Source == StorageSource.Capacity ? Strings.StatsCapacity : Strings.StatsDiskSize;

    public string ForecastText => _data.Forecast is { } forecast ? BorgTexts.Forecast(forecast, _data.Now) : "";

    public bool HasForecast => _data.Forecast is not null;

    // --- Savings ---

    /// <summary>"2,30 TB" – what the archives would take up without deduplication and compression.</summary>
    public string OriginalText => _data.Usage?.Repository is { } stats ? BorgTexts.Size(stats.TotalSize) : "–";

    /// <summary>"132 GB" – what the repository takes up.</summary>
    public string StoredText => _data.Usage?.RepositoryBytes is { } bytes ? BorgTexts.Size(bytes) : "–";

    public string SavingsText => _data.Usage?.Repository?.Savings is { } savings ? $"{Math.Round(savings * 100)} %" : "–";

    public string DeduplicationText => Ratio(_data.Usage?.Repository?.DeduplicationRatio);

    public string CompressionText => Ratio(_data.Usage?.Repository?.CompressionRatio);

    /// <summary>"42 Archive"; empty while the archive list was never loaded.</summary>
    public string ArchivesText => _data.Archives is { } count ? BorgTexts.Format(Strings.StatsArchivesFormat, count) : "";

    public bool HasStats => _data.Usage?.Repository is not null;

    private static string Ratio(double? ratio) => ratio is { } value ? $"{value.ToString("0.0", CultureInfo.CurrentCulture)} : 1" : "–";

    // --- Backups ---

    public IReadOnlyList<Option<Guid?>> JobOptions { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Columns), nameof(HasColumns), nameof(SuccessRateText), nameof(SuccessDetailText), nameof(AverageDurationText),
        nameof(AverageNewText), nameof(NewPerWeekText), nameof(LastSuccessText))]
    public partial Option<Guid?> SelectedJob { get; set; }

    /// <summary>The runs of the chosen job (or all), newest first.</summary>
    private IEnumerable<(BackupJob Job, BackupRunSummary Run)> Runs =>
        _data.Runs.Where(entry => SelectedJob?.Value is not { } id || entry.Job.Id == id)
            .SelectMany(entry => entry.Runs.Select(run => (entry.Job, Run: run)))
            .Where(entry => entry.Run.Outcome != BackupOutcome.Cancelled)
            .OrderByDescending(entry => entry.Run.Started);

    private List<BackupRunSummary> RecentRuns =>
        [.. Runs.Select(entry => entry.Run).Where(run => _data.Now - run.Started <= TimeSpan.FromDays(30))];

    /// <summary>The last backups, oldest left: what each newly stored; failed runs red, warnings yellow.</summary>
    public IReadOnlyList<ChartColumn> Columns =>
    [
        .. Runs.Take(ColumnCount).Reverse().Select(entry =>
        {
            var run = entry.Run;
            var when = run.Started.ToLocalTime();
            var kind = run.Outcome switch
            {
                BackupOutcome.Succeeded => "series",
                BackupOutcome.Warnings => "warn",
                _ => "fail",
            };
            var tip = $"{entry.Job.Name} · {when.ToString("g", CultureInfo.CurrentCulture)}\n{BorgTexts.BackupOutcome(run.Outcome)}"
                + (run.Outcome is BackupOutcome.Succeeded or BackupOutcome.Warnings
                    ? $" · {BorgTexts.Format(Strings.StatsColumnTip, BorgTexts.Size(run.DeduplicatedSize), BorgTexts.Duration(run.Duration))}"
                    : "");
            return new ChartColumn(when.ToString("dd.MM.", CultureInfo.CurrentCulture), run.DeduplicatedSize, kind, tip);
        }),
    ];

    public bool HasColumns => Columns.Count > 0;

    /// <summary>"97 %" of the backups of the last 30 days worked (with or without warnings).</summary>
    public string SuccessRateText
    {
        get
        {
            var runs = RecentRuns;
            return runs.Count == 0 ? "–" : $"{Math.Round(100.0 * runs.Count(Worked) / runs.Count)} %";
        }
    }

    /// <summary>"58 von 60 Läufen (30 Tage)".</summary>
    public string SuccessDetailText
    {
        get
        {
            var runs = RecentRuns;
            return BorgTexts.Format(Strings.StatsSuccessDetail, runs.Count(Worked), runs.Count);
        }
    }

    /// <summary>"4:12" – how long a backup that worked took on average (30 days).</summary>
    public string AverageDurationText => RecentRuns.Where(Worked).ToList() is { Count: > 0 } worked
        ? BorgTexts.Duration(TimeSpan.FromSeconds(worked.Average(run => run.Duration.TotalSeconds)))
        : "–";

    /// <summary>"1,8 GB" – what a backup newly stored on average (30 days).</summary>
    public string AverageNewText => RecentRuns.Where(Worked).ToList() is { Count: > 0 } worked
        ? BorgTexts.Size((long)worked.Average(run => run.DeduplicatedSize))
        : "–";

    /// <summary>"12,6 GB" – newly stored in the last 7 days.</summary>
    public string NewPerWeekText => BorgTexts.Size(Runs.Select(entry => entry.Run)
        .Where(run => Worked(run) && _data.Now - run.Started <= TimeSpan.FromDays(7))
        .Sum(run => run.DeduplicatedSize));

    /// <summary>"vor 3 Stunden" / "nie".</summary>
    public string LastSuccessText => Runs.Select(entry => entry.Run).FirstOrDefault(Worked) is { } last
        ? Ago(_data.Now - last.Started)
        : Strings.StatsNever;

    private static bool Worked(BackupRunSummary run) => run.Outcome is BackupOutcome.Succeeded or BackupOutcome.Warnings;

    private static string Ago(TimeSpan age) => age.TotalHours < 1 ? BorgTexts.Format(Strings.StatsAgoMinutes, Math.Max(1, (int)age.TotalMinutes))
        : age.TotalDays < 1 ? BorgTexts.Format(Strings.StatsAgoHours, (int)age.TotalHours)
        : BorgTexts.Format(Strings.StatsAgoDays, (int)age.TotalDays);

    // --- Activity ---

    public IReadOnlyList<CalendarDay> Days { get; }

    /// <summary>The measurements as points, and the forecast from the last one on (up to full, at most half a year).</summary>
    private static (IReadOnlyList<ChartPoint> Points, IReadOnlyList<ChartPoint> Projection) StorageSeries(RepositoryStatisticsData data)
    {
        List<ChartPoint> points = [.. data.History.Where(point => point.GrowingBytes is not null)
            .Select(point => new ChartPoint(point.Measured, point.GrowingBytes!.Value))];
        if (points.Count < 2 || data.Forecast is not { Growing: true } forecast)
            return (points, []);

        var last = points[^1];
        var end = forecast.FullAt is { } full && full - last.Time <= ProjectionReach ? full : last.Time + ProjectionReach;
        var value = forecast.FullAt == end && data.Usage?.Total is { } total
            ? total
            : last.Value + forecast.BytesPerDay * (end - last.Time).TotalDays;
        return (points, [last, new ChartPoint(end, value)]);
    }

    /// <summary>The days of the last weeks with the worst outcome of each (all jobs).</summary>
    private static IReadOnlyList<CalendarDay> Calendar(RepositoryStatisticsData data)
    {
        var today = DateOnly.FromDateTime(data.Now.ToLocalTime().Date);
        var mondayThisWeek = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        var first = mondayThisWeek.AddDays(-7 * (CalendarWeeks - 1));
        var byDay = data.Runs.SelectMany(entry => entry.Runs.Select(run => (entry.Job, Run: run)))
            .Where(entry => entry.Run.Outcome != BackupOutcome.Cancelled)
            .GroupBy(entry => DateOnly.FromDateTime(entry.Run.Started.ToLocalTime().Date))
            .ToDictionary(group => group.Key, group => group.ToList());

        var days = new List<CalendarDay>();
        for (var date = first; date <= today; date = date.AddDays(1))
        {
            var label = date.ToString("ddd, d. MMM", CultureInfo.CurrentCulture);
            if (!byDay.TryGetValue(date, out var runs))
            {
                days.Add(new CalendarDay(date, "", $"{label}: {Strings.StatsNoBackup}"));
                continue;
            }
            var kind = runs.Any(entry => entry.Run.Outcome is BackupOutcome.Failed or BackupOutcome.Skipped) ? "fail"
                : runs.Any(entry => entry.Run.Outcome == BackupOutcome.Warnings) ? "warn"
                : "ok";
            var worked = runs.Count(entry => Worked(entry.Run));
            days.Add(new CalendarDay(date, kind, $"{label}: {BorgTexts.Format(Strings.StatsDayTip, worked, runs.Count)}"));
        }
        return days;
    }
}
