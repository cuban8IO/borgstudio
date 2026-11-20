using System.Globalization;
using BorgStudio.App.Controls;
using BorgStudio.App.Resources;
using BorgStudio.Core.Backups;
using BorgStudio.Core.Storage;
using CommunityToolkit.Mvvm.Input;

namespace BorgStudio.App.ViewModels;

/// <summary>A repository with its jobs and their runs (newest first), for the overview.</summary>
public sealed record DashboardRepository(RepositoryItemViewModel Item, IReadOnlyList<(BackupJob Job, IReadOnlyList<BackupRunSummary> Runs)> Jobs);

/// <summary>
/// "Alle Repositories": what all repositories take up and save, how reliable the backups are, which storage fills up
/// first – and each repository with its state, problems first. Clicking a repository opens it.
/// </summary>
public sealed class DashboardViewModel
{
    private readonly IReadOnlyList<DashboardRepository> _repositories;
    private readonly DateTimeOffset _now;

    public DashboardViewModel(IReadOnlyList<DashboardRepository> repositories, DateTimeOffset now, Action<RepositoryItemViewModel> open)
    {
        _repositories = repositories;
        _now = now;
        Rows = [.. repositories.Select(repository => new DashboardRowViewModel(repository, now, open))
            .OrderByDescending(row => row.Level)
            .ThenBy(row => row.Item.Forecast?.FullAt ?? DateTimeOffset.MaxValue)
            .ThenBy(row => row.Item.Storage?.FreeFraction ?? 1)];
        Days = RepositoryStatisticsViewModel.Calendar(repositories.SelectMany(repository => repository.Jobs), now);
    }

    /// <summary>The repositories, problems first, then the one that fills up first.</summary>
    public IReadOnlyList<DashboardRowViewModel> Rows { get; }

    public IReadOnlyList<CalendarDay> Days { get; }

    private IEnumerable<StorageUsage> Usages => _repositories.Select(repository => repository.Item.Storage).OfType<StorageUsage>();

    // --- Key figures ---

    /// <summary>"1,42 TB" – what all repositories take up.</summary>
    public string StoredText => BorgTexts.Size(Usages.Sum(usage => usage.RepositoryBytes ?? 0));

    /// <summary>"3 Repositories".</summary>
    public string RepositoriesText => BorgTexts.Format(Strings.DashRepositories, _repositories.Count);

    /// <summary>"94 %" – what deduplication and compression save over all repositories borg reported on.</summary>
    public string SavingsText => Stats is { Count: > 0 } stats && stats.Sum(stat => (double)stat.TotalSize) is > 0 and var total
        ? $"{Math.Round(100 * (1 - stats.Sum(stat => (double)stat.UniqueCompressedSize) / total))} %"
        : "–";

    /// <summary>"23,8 TB → 1,42 TB".</summary>
    public string SavingsDetailText => Stats is { Count: > 0 } stats
        ? $"{BorgTexts.Size(stats.Sum(stat => stat.TotalSize))} → {BorgTexts.Size(stats.Sum(stat => stat.UniqueCompressedSize))}"
        : Strings.DashNoStats;

    private List<RepositoryStats> Stats => [.. Usages.Select(usage => usage.Repository).OfType<RepositoryStats>()];

    private List<BackupRunSummary> RecentRuns =>
    [
        .. _repositories.SelectMany(repository => repository.Jobs).SelectMany(entry => entry.Runs)
            .Where(run => run.Outcome != BackupOutcome.Cancelled && _now - run.Started <= TimeSpan.FromDays(30)),
    ];

    /// <summary>"97 %" of the backups of the last 30 days worked.</summary>
    public string SuccessRateText => RecentRuns is { Count: > 0 } runs
        ? $"{Math.Round(100.0 * runs.Count(Worked) / runs.Count)} %"
        : "–";

    public string SuccessDetailText => BorgTexts.Format(Strings.StatsSuccessDetail, RecentRuns.Count(Worked), RecentRuns.Count);

    private DashboardRowViewModel? FillsFirst =>
        Rows.Where(row => row.Item.Forecast?.FullAt is not null).MinBy(row => row.Item.Forecast!.FullAt);

    /// <summary>"~4 Mon." until the first storage is full; "–" if none is foreseeable.</summary>
    public string EarliestFullText => FillsFirst?.Item.Forecast?.FullAt is { } at ? Until(at - _now) : "–";

    /// <summary>"NAS, Ende April 2027" / "nicht absehbar".</summary>
    public string EarliestFullDetailText => FillsFirst is { Item.Forecast.FullAt: { } at } row
        ? $"{row.Item.Name}, {at.ToLocalTime().ToString("MMMM yyyy", CultureInfo.CurrentCulture)}"
        : Strings.DashNotForeseeable;

    /// <summary>The first storage is full within 90 days.</summary>
    public bool IsFullSoon => FillsFirst?.Item.Forecast?.FullAt is { } at && at - _now < TimeSpan.FromDays(90);

    /// <summary>"~5 Tage", "~3 Wo.", "~4 Mon.", "~2 J.".</summary>
    internal static string Until(TimeSpan time) => time.TotalDays switch
    {
        < 1 => Strings.DashToday,
        < 14 => BorgTexts.Format(Strings.DashDays, Math.Round(time.TotalDays)),
        < 60 => BorgTexts.Format(Strings.DashWeeks, Math.Round(time.TotalDays / 7)),
        < 730 => BorgTexts.Format(Strings.DashMonths, Math.Round(time.TotalDays / 30.44)),
        _ => BorgTexts.Format(Strings.DashYears, Math.Round(time.TotalDays / 365.25)),
    };

    private static bool Worked(BackupRunSummary run) => run.Outcome is BackupOutcome.Succeeded or BackupOutcome.Warnings;
}

/// <summary>A repository in the overview: its storage bar, its state (the worst of its jobs and its storage), its last backup.</summary>
public sealed partial class DashboardRowViewModel
{
    private readonly Action<RepositoryItemViewModel> _open;

    public DashboardRowViewModel(DashboardRepository repository, DateTimeOffset now, Action<RepositoryItemViewModel> open)
    {
        _open = open;
        Item = repository.Item;
        var local = now.LocalDateTime;
        var health = repository.Jobs.Select(entry => (entry.Job, Health: JobHealth.Evaluate(entry.Job, entry.Runs, local))).ToList();

        // Each problem with its level; the worst one is told, the others counted.
        var problems = new List<(HealthLevel Level, string Text)>();
        if (Item.StorageLevel == StorageLevel.Full)
            problems.Add((HealthLevel.Critical, Strings.DashStorageFull));
        else if (Item.StorageLevel == StorageLevel.Low)
            problems.Add((HealthLevel.Warning, Strings.DashStorageLow));
        foreach (var (job, state) in health.Where(entry => entry.Health.Level is HealthLevel.Warning or HealthLevel.Critical))
            problems.Add((state.Level, BorgTexts.Format(Reason(state, now), job.Name, state.LastSuccess is { } at ? (int)(now - at).TotalDays : 0)));

        if (problems.Count > 0)
        {
            var worst = problems.OrderByDescending(problem => problem.Level).First();
            Level = worst.Level;
            HealthText = problems.Count > 1 ? BorgTexts.Format(Strings.DashMoreProblems, worst.Text, problems.Count - 1) : worst.Text;
        }
        else
        {
            Level = health.Any(entry => entry.Health.Level == HealthLevel.Ok) ? HealthLevel.Ok : HealthLevel.Info;
            HealthText = repository.Jobs.Count == 0 ? Strings.DashNoJobs
                : Level == HealthLevel.Ok ? Strings.DashAllWell
                : Strings.DashByHandOnly;
        }

        var lastSuccess = health.Select(entry => entry.Health.LastSuccess).Max();
        LastBackupText = lastSuccess is { } last
            ? BorgTexts.Format(Strings.DashLastBackup, Ago(now - last))
            : Strings.DashNeverBackedUp;
    }

    public RepositoryItemViewModel Item { get; }

    public HealthLevel Level { get; }

    public string HealthText { get; }

    public string LastBackupText { get; }

    /// <summary>"ok", "warn", "fail" or "" (no judgement): the color of the state dot.</summary>
    public string LevelKind => Level switch
    {
        HealthLevel.Critical => "fail",
        HealthLevel.Warning => "warn",
        HealthLevel.Ok => "ok",
        _ => "",
    };

    public bool IsCritical => Level == HealthLevel.Critical;

    public bool IsWarning => Level == HealthLevel.Warning;

    public bool IsOk => Level == HealthLevel.Ok;

    public bool IsInfo => Level == HealthLevel.Info;

    /// <summary>"Frei 240 GB von 1000 GB (24 %)", or the repository's size alone.</summary>
    public string StorageText => Item.HasStorageBar ? Item.StorageFreeText : Item.StorageText;

    /// <summary>"voll ~Ende Apr. 2027"; empty if not foreseeable.</summary>
    public string FullText => Item.Forecast?.FullAt is { } at
        ? BorgTexts.Format(Strings.DashFullBy, at.ToLocalTime().ToString("MMM yyyy", CultureInfo.CurrentCulture))
        : "";

    public bool HasFullText => FullText.Length > 0;

    [RelayCommand]
    private void Open() => _open(Item);

    private static string Reason(JobHealth health, DateTimeOffset now) => health.Reason switch
    {
        HealthReason.NeverBackedUp => Strings.DashReasonNever,
        HealthReason.LastRunFailed => Strings.DashReasonFailed,
        HealthReason.Missed => Strings.DashReasonMissed,
        HealthReason.MissedTwice => Strings.DashReasonMissedTwice,
        HealthReason.Overdue => Strings.DashReasonOverdue,
        _ => "{0}",
    };

    private static string Ago(TimeSpan age) => age.TotalHours < 1 ? BorgTexts.Format(Strings.StatsAgoMinutes, Math.Max(1, (int)age.TotalMinutes))
        : age.TotalDays < 1 ? BorgTexts.Format(Strings.StatsAgoHours, (int)age.TotalHours)
        : BorgTexts.Format(Strings.StatsAgoDays, (int)age.TotalDays);
}
