using BorgStudio.App.Resources;
using BorgStudio.App.ViewModels;
using BorgStudio.Core.Backups;
using BorgStudio.Core.Repositories;
using BorgStudio.Core.Storage;
using BorgStudio.Tests.TestSupport;

namespace BorgStudio.Tests.ViewModels;

/// <summary>The "Statistik" tab: storage over time with forecast, backups, savings and activity.</summary>
public sealed class RepositoryStatisticsTests : IDisposable
{
    private const long Gigabyte = 1024L * 1024 * 1024;

    // Friday, 20 November 2026, noon (local).
    private static readonly DateTimeOffset Now = new(new DateTime(2026, 11, 20, 12, 0, 0, DateTimeKind.Local));

    private readonly TestServices _test = new();

    public void Dispose() => _test.Dispose();

    private static BackupJob Job(string name) => new() { Id = Guid.NewGuid(), Name = name, RepositoryId = Guid.Empty };

    private static BackupRunSummary Run(double daysAgo, BackupOutcome outcome = BackupOutcome.Succeeded, long added = Gigabyte, int minutes = 4) => new()
    {
        Started = Now.AddDays(-daysAgo),
        Duration = TimeSpan.FromMinutes(minutes),
        Outcome = outcome,
        DeduplicatedSize = outcome is BackupOutcome.Succeeded or BackupOutcome.Warnings ? added : 0,
    };

    private static StorageUsage Usage => new()
    {
        Measured = Now,
        Source = StorageSource.Disk,
        Total = 1000 * Gigabyte,
        Free = 300 * Gigabyte,
        Repository = new RepositoryStats
        {
            TotalSize = 4000 * Gigabyte, TotalCompressedSize = 2000 * Gigabyte, UniqueSize = 400 * Gigabyte, UniqueCompressedSize = 200 * Gigabyte,
        },
    };

    private static RepositoryStatisticsViewModel Statistics(IReadOnlyList<StoragePoint>? history = null, StorageForecast? forecast = null,
        params (BackupJob, IReadOnlyList<BackupRunSummary>)[] runs) =>
        new(new RepositoryStatisticsData(Usage, history ?? [], forecast, runs, 42, Now));

    [Fact]
    public void Backups_give_rates_durations_and_columns_per_job()
    {
        var documents = Job("Documents");
        var photos = Job("Photos");
        // History lists runs newest first.
        IReadOnlyList<BackupRunSummary> documentRuns =
            [Run(0.1, added: 2 * Gigabyte, minutes: 6), Run(1, BackupOutcome.Failed), Run(2, BackupOutcome.Warnings), Run(40), Run(3, BackupOutcome.Cancelled)];
        IReadOnlyList<BackupRunSummary> photoRuns = [Run(0.5, added: 10 * Gigabyte, minutes: 20)];

        var statistics = Statistics(runs: [(documents, documentRuns), (photos, photoRuns)]);

        Assert.Equal([Strings.StatsAllJobs, "Documents", "Photos"], statistics.JobOptions.Select(option => option.Label));
        // Last 30 days, without the stopped one: 3 of 4 worked.
        Assert.Equal("75 %", statistics.SuccessRateText);
        Assert.Equal(BorgTexts.Format(Strings.StatsSuccessDetail, 3, 4), statistics.SuccessDetailText);
        Assert.Equal(BorgTexts.Format(Strings.StatsAgoHours, 2), statistics.LastSuccessText);
        Assert.Equal(BorgTexts.Size(13 * Gigabyte), statistics.NewPerWeekText);
        // Oldest left, colored by outcome.
        Assert.Equal(["series", "warn", "fail", "series", "series"], statistics.Columns.Select(column => column.Kind));
        Assert.Equal(10 * Gigabyte, statistics.Columns[^2].Value);

        statistics.SelectedJob = statistics.JobOptions[2];
        Assert.Equal("100 %", statistics.SuccessRateText);
        Assert.Equal("20:00", statistics.AverageDurationText);
        Assert.Single(statistics.Columns);
    }

    [Fact]
    public void The_forecast_line_runs_from_the_last_measurement_to_full()
    {
        var history = Enumerable.Range(0, 5).Select(week => new StoragePoint
        {
            Measured = Now.AddDays(-28 + week * 7),
            Total = 1000 * Gigabyte,
            Free = (340 - week * 10) * Gigabyte,
        }).ToList();
        var forecast = new StorageForecast(10.0 * Gigabyte / 7, ForecastBasis.Trend, Now.AddDays(150), null, null);

        var statistics = Statistics(history, forecast);

        Assert.True(statistics.HasStorageChart);
        Assert.Equal(5, statistics.StoragePoints.Count);
        Assert.Equal(statistics.StoragePoints[^1], statistics.Projection[0]);
        Assert.Equal(Now.AddDays(150), statistics.Projection[1].Time);
        Assert.Equal(1000.0 * Gigabyte, statistics.Projection[1].Value);
        Assert.Equal(1000.0 * Gigabyte, statistics.Limit);
        Assert.Equal(Strings.StatsDiskSize, statistics.LimitLabel);

        // Full only in years: the line stops after half a year; shrinking: no line.
        var far = Statistics(history, forecast with { FullAt = Now.AddDays(2000) });
        Assert.Equal(statistics.StoragePoints[^1].Time + RepositoryStatisticsViewModel.ProjectionReach, far.Projection[1].Time);
        Assert.Empty(Statistics(history, forecast with { BytesPerDay = -1 }).Projection);
        Assert.False(Statistics().HasStorageChart);
    }

    [Fact]
    public void Savings_and_activity_come_from_borg_and_the_runs()
    {
        var job = Job("Documents");
        var statistics = Statistics(runs: [(job, [Run(0), Run(0.2, BackupOutcome.Warnings), Run(1, BackupOutcome.Failed), Run(3)])]);

        Assert.Equal(BorgTexts.Size(4000 * Gigabyte), statistics.OriginalText);
        Assert.Equal("95 %", statistics.SavingsText);
        Assert.Equal($"{10.0:0.0} : 1", statistics.DeduplicationText);
        Assert.Equal(BorgTexts.Format(Strings.StatsArchivesFormat, 42), statistics.ArchivesText);

        // Twelve weeks up to today (a Friday): 11 full weeks and Monday to Friday.
        Assert.Equal(11 * 7 + 5, statistics.Days.Count);
        Assert.Equal(DayOfWeek.Monday, statistics.Days[0].Date.DayOfWeek);
        var today = statistics.Days[^1];
        Assert.Equal("warn", today.Kind);
        Assert.Contains(BorgTexts.Format(Strings.StatsDayTip, 2, 2), today.Tip);
        Assert.Equal("fail", statistics.Days[^2].Kind);
        Assert.Equal("", statistics.Days[^3].Kind);
        Assert.Equal("ok", statistics.Days[^4].Kind);
    }

    [Fact]
    public async Task The_tab_builds_the_statistics_of_the_selected_repository()
    {
        var repository = new RepositoryConfig
        {
            Id = Guid.NewGuid(),
            Name = "Disk",
            ProviderId = "local",
            ProviderValues = new Dictionary<string, string> { ["path"] = _test.Directory.Combine("repo") },
            PassphraseMode = PassphraseMode.None,
            EncryptionMode = "none",
        };
        _test.Store.Save([repository]);
        _test.Jobs.Save([Job("Documents") with { RepositoryId = repository.Id }]);
        var main = new MainViewModel(_test.Services);
        await main.CheckBorgCommand.ExecuteAsync(null);
        Assert.Null(main.Statistics);

        main.SelectedDetailsTab = MainViewModel.StatisticsTab;

        Assert.NotNull(main.Statistics);
        Assert.Equal([Strings.StatsAllJobs, "Documents"], main.Statistics.JobOptions.Select(option => option.Label));
        Assert.False(main.Statistics.HasColumns);
    }
}
