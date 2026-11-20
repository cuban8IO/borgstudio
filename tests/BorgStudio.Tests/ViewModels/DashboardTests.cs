using BorgStudio.App.Resources;
using BorgStudio.App.ViewModels;
using BorgStudio.Core.Backups;
using BorgStudio.Core.Repositories;
using BorgStudio.Core.Storage;
using BorgStudio.Tests.TestSupport;

namespace BorgStudio.Tests.ViewModels;

/// <summary>The overview of all repositories and how a job's state is judged.</summary>
public sealed class DashboardTests : IDisposable
{
    private const long Gigabyte = 1024L * 1024 * 1024;

    // Friday, 20 November 2026, 14:00.
    private static readonly DateTime Now = new(2026, 11, 20, 14, 0, 0, DateTimeKind.Local);

    private static readonly BackupSchedule DailyAtTwo = new() { Kind = ScheduleKind.Daily, Time = new TimeOnly(2, 0) };

    private readonly TestServices _test = new();

    public void Dispose() => _test.Dispose();

    private static BackupJob Job(string name = "Documents", bool scheduled = true, int overdueDays = 0) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        RepositoryId = Guid.Empty,
        Schedules = scheduled ? [DailyAtTwo] : [],
        OverdueDays = overdueDays,
    };

    private static BackupRunSummary Run(DateTime started, BackupOutcome outcome = BackupOutcome.Succeeded) => new()
    {
        Started = new DateTimeOffset(started),
        Duration = TimeSpan.FromMinutes(3),
        Outcome = outcome,
        DeduplicatedSize = Gigabyte,
    };

    // --- Health ---

    [Fact]
    public void A_scheduled_job_is_judged_by_its_scheduled_times()
    {
        var job = Job();
        var today = Now.Date.AddHours(2);

        Assert.Equal(HealthLevel.Ok, JobHealth.Evaluate(job, [Run(today)], Now).Level);
        // Today's 02:00 was missed, yesterday's worked.
        Assert.Equal((HealthLevel.Warning, HealthReason.Missed), State(JobHealth.Evaluate(job, [Run(today.AddDays(-1))], Now)));
        // Today's failed.
        Assert.Equal((HealthLevel.Warning, HealthReason.LastRunFailed),
            State(JobHealth.Evaluate(job, [Run(today, BackupOutcome.Failed), Run(today.AddDays(-1))], Now)));
        // Two missed in a row.
        Assert.Equal((HealthLevel.Critical, HealthReason.MissedTwice), State(JobHealth.Evaluate(job, [Run(today.AddDays(-2))], Now)));
        // A run that is still allowed to finish (an hour after its time) does not count as missed yet.
        Assert.Equal(HealthLevel.Ok, JobHealth.Evaluate(job, [Run(today.AddDays(-1))], today.AddMinutes(30)).Level);
        Assert.Equal((HealthLevel.Warning, HealthReason.NeverBackedUp), State(JobHealth.Evaluate(job, [], Now)));
    }

    [Fact]
    public void Jobs_by_hand_are_only_judged_by_failures_and_their_overdue_days()
    {
        Assert.Equal((HealthLevel.Info, HealthReason.Unscheduled), State(JobHealth.Evaluate(Job(scheduled: false), [Run(Now.AddDays(-30))], Now)));
        Assert.Equal((HealthLevel.Warning, HealthReason.LastRunFailed),
            State(JobHealth.Evaluate(Job(scheduled: false), [Run(Now.AddDays(-1), BackupOutcome.Failed)], Now)));
        Assert.Equal((HealthLevel.Critical, HealthReason.Overdue),
            State(JobHealth.Evaluate(Job(scheduled: false, overdueDays: 7), [Run(Now.AddDays(-8))], Now)));
    }

    private static (HealthLevel, HealthReason) State(JobHealth health) => (health.Level, health.Reason);

    // --- Overview ---

    private RepositoryItemViewModel Item(string name, long freeGigabytes, StorageForecast? forecast = null)
    {
        var item = new RepositoryItemViewModel(new RepositoryConfig
        {
            Id = Guid.NewGuid(),
            Name = name,
            ProviderId = "local",
            ProviderValues = new Dictionary<string, string> { ["path"] = _test.Directory.Combine(name) },
        }, _test.Services.Plugins)
        {
            Storage = new StorageUsage
            {
                Measured = Now,
                Source = StorageSource.Disk,
                Total = 1000 * Gigabyte,
                Free = freeGigabytes * Gigabyte,
                Repository = new RepositoryStats
                {
                    TotalSize = 2000 * Gigabyte, TotalCompressedSize = 1000 * Gigabyte, UniqueSize = 200 * Gigabyte, UniqueCompressedSize = 100 * Gigabyte,
                },
            },
            Forecast = forecast,
        };
        if (freeGigabytes < 100)
            item.StorageLevel = StorageLevel.Low;
        return item;
    }

    [Fact]
    public void The_overview_puts_problems_first_and_sums_up_all_repositories()
    {
        var today = Now.Date.AddHours(2);
        var fine = Item("Fine", 600, new StorageForecast(Gigabyte, ForecastBasis.Trend, new DateTimeOffset(Now).AddDays(400), null, null));
        var fillsUp = Item("Fills up", 500, new StorageForecast(Gigabyte, ForecastBasis.Trend, new DateTimeOffset(Now).AddDays(45), null, null));
        var broken = Item("Broken", 50);
        var documents = Job("Documents");
        var photos = Job("Photos");
        var opened = new List<RepositoryItemViewModel>();

        var dashboard = new DashboardViewModel(
        [
            new DashboardRepository(fine, [(Job(), [Run(today)])]),
            new DashboardRepository(fillsUp, [(Job(), [Run(today)])]),
            new DashboardRepository(broken, [(documents, [Run(today.AddDays(-2))]), (photos, [Run(today, BackupOutcome.Failed)])]),
        ], new DateTimeOffset(Now), opened.Add);

        // Critical first, then the one that fills up first.
        Assert.Equal(["Broken", "Fills up", "Fine"], dashboard.Rows.Select(row => row.Item.Name));
        var first = dashboard.Rows[0];
        Assert.True(first.IsCritical);
        Assert.Equal(BorgTexts.Format(Strings.DashMoreProblems, BorgTexts.Format(Strings.DashReasonMissedTwice, "Documents"), 2), first.HealthText);
        Assert.Equal(Strings.DashAllWell, dashboard.Rows[2].HealthText);

        Assert.Equal(BorgTexts.Size(300 * Gigabyte), dashboard.StoredText);
        Assert.Equal("95 %", dashboard.SavingsText);
        Assert.Equal("75 %", dashboard.SuccessRateText);
        Assert.Equal(BorgTexts.Format(Strings.DashWeeks, 6), dashboard.EarliestFullText);
        Assert.StartsWith("Fills up, ", dashboard.EarliestFullDetailText);
        Assert.True(dashboard.IsFullSoon);

        dashboard.Rows[1].OpenCommand.Execute(null);
        Assert.Same(fillsUp, Assert.Single(opened));
    }

    [Theory]
    [InlineData(0.5, "DashToday")]
    [InlineData(5, "DashDays")]
    [InlineData(30, "DashWeeks")]
    [InlineData(120, "DashMonths")]
    [InlineData(1000, "DashYears")]
    public void The_time_until_full_is_told_in_a_fitting_unit(double days, string format)
    {
        var text = DashboardViewModel.Until(TimeSpan.FromDays(days));
        var pattern = Strings.ResourceManager.GetString(format, Strings.Culture)!;
        Assert.StartsWith(pattern.Split('{')[0], text);
    }

    [Fact]
    public async Task The_app_starts_with_the_overview_and_a_repository_leaves_it()
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
        var main = new MainViewModel(_test.Services);
        await main.CheckBorgCommand.ExecuteAsync(null);

        Assert.True(main.ShowsOverview);
        Assert.False(main.ShowsRepository);
        Assert.Null(main.ListedRepository);
        // The repository stays selected underneath: its commands keep working.
        Assert.NotNull(main.SelectedRepository);
        Assert.Equal(["Disk"], main.Dashboard!.Rows.Select(row => row.Item.Name));

        main.Dashboard.Rows[0].OpenCommand.Execute(null);
        Assert.True(main.ShowsRepository);
        Assert.Same(main.SelectedRepository, main.ListedRepository);

        main.ShowOverviewCommand.Execute(null);
        Assert.True(main.ShowsOverview);
        main.ListedRepository = main.Repositories[0];
        Assert.False(main.IsOverviewShown);

        // Without repositories: the welcome, no overview.
        using var empty = new TestServices();
        Assert.False(new MainViewModel(empty.Services).ShowsOverview);
    }
}
