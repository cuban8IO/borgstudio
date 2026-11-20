using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BorgStudio.App.ViewModels;

/// <summary>
/// The overview of all repositories, shown by the entry "Alle Repositories" above the list (and at startup). The selected
/// repository stays selected meanwhile – commands keep working on it – only the list shows no selection.
/// </summary>
public partial class MainViewModel
{
    /// <summary>The overview is shown instead of a repository.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ListedRepository), nameof(ShowsOverview), nameof(ShowsRepository))]
    public partial bool IsOverviewShown { get; private set; }

    public bool ShowsOverview => IsOverviewShown && HasRepositories;

    public bool ShowsRepository => HasSelection && !IsOverviewShown;

    /// <summary>What the list shows as selected: nothing while the overview is shown.</summary>
    public RepositoryItemViewModel? ListedRepository
    {
        get => IsOverviewShown ? null : SelectedRepository;
        set
        {
            if (value is null)
                return;
            SelectedRepository = value;
            IsOverviewShown = false;
        }
    }

    [ObservableProperty]
    public partial DashboardViewModel? Dashboard { get; private set; }

    [RelayCommand]
    private void ShowOverview() => IsOverviewShown = true;

    partial void OnIsOverviewShownChanged(bool value) => RefreshDashboard();

    /// <summary>Builds the overview anew (only while it is shown – it reads the runs' history).</summary>
    internal void RefreshDashboard()
    {
        if (!IsOverviewShown)
            return;
        var repositories = Repositories.Select(item => new DashboardRepository(item,
            [.. _jobs.Where(job => job.RepositoryId == item.Config.Id).Select(job => (job, Runner.History.Get(job.Id)))])).ToList();
        Dashboard = new DashboardViewModel(repositories, DateTimeOffset.Now, OpenRepository);
    }

    /// <summary>A repository clicked in the overview.</summary>
    private void OpenRepository(RepositoryItemViewModel item)
    {
        SelectedRepository = item;
        SelectedDetailsTab = OverviewTab;
        IsOverviewShown = false;
    }
}
