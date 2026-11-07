using System.Collections.ObjectModel;
using System.Globalization;
using BorgStudio.App.Resources;
using BorgStudio.Core.Borg;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BorgStudio.App.ViewModels;

/// <summary>
/// Searching file and folder names in all archives of a repository: one row per path with how many versions it has and
/// in how many archives it is; its versions open from there.
/// </summary>
public sealed partial class ArchiveSearchViewModel(ArchiveContentsSource source) : ArchiveScanViewModel(source)
{
    private ArchiveSearch? _search;
    private int _skipped;
    private string _searchedText = "";

    public string Title => BorgTexts.Format(Strings.SearchTitle, Source.RepositoryName);

    /// <summary>"42 archives, 30 of them read before".</summary>
    public string ScopeText
    {
        get
        {
            var cached = Source.Archives.Count(Source.IsCached);
            return cached == Source.Archives.Count
                ? BorgTexts.Format(Strings.SearchScopeAll, Source.Archives.Count)
                : BorgTexts.Format(Strings.SearchScope, Source.Archives.Count, cached);
        }
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SearchCommand))]
    public partial string SearchText { get; set; } = "";

    public ObservableCollection<SearchHitViewModel> Hits { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ShowVersionsCommand))]
    public partial SearchHitViewModel? SelectedHit { get; set; }

    /// <summary>"12 hits for „invoice“ in 42 archives".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    public partial string? StatusText { get; private set; }

    public bool HasStatus => StatusText is not null;

    /// <summary>The search running (tests wait for it).</summary>
    public Task Searching { get; private set; } = Task.CompletedTask;

    protected override void OnScanningChanged()
    {
        SearchCommand.NotifyCanExecuteChanged();
        ShowVersionsCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanSearch))]
    private Task SearchAsync() => Searching = RunAsync(SearchText.Trim());

    private bool CanSearch() => !string.IsNullOrWhiteSpace(SearchText) && !IsScanning;

    protected override Task RescanAsync() => Searching = RunAsync(_searchedText);

    private async Task RunAsync(string text)
    {
        if (text.Length == 0)
            return;
        _searchedText = text;
        var search = new ArchiveSearch(ArchiveBrowserViewModel.Matcher(text), ArchiveBrowserViewModel.MaximumResults);
        StatusText = null;
        var outcome = await ScanAsync(BorgTexts.Format(Strings.SearchRunning, text), (archive, tree, cancellationToken) =>
            search.Add(archive, tree, cancellationToken));
        OnPropertyChanged(nameof(ScopeText)); // archives read meanwhile are cached now
        if (outcome is null)
            return;

        _search = search;
        _skipped = outcome.Skipped;
        Hits.Clear();
        foreach (var hit in search.Hits)
            Hits.Add(new SearchHitViewModel(hit, search.Searched));
        SelectedHit = Hits.FirstOrDefault();

        var status = search.Hits.Count == 0 ? BorgTexts.Format(Strings.SearchNone, text, search.Searched.Count)
            : search.TooMany ? BorgTexts.Format(Strings.BrowserSearchTooMany, ArchiveBrowserViewModel.MaximumResults)
            : BorgTexts.Format(search.Hits.Count == 1 ? Strings.SearchResultOne : Strings.SearchResults, search.Hits.Count, text,
                search.Searched.Count);
        if (outcome.Cancelled)
            status = BorgTexts.Format(Strings.ScanCancelled, search.Searched.Count, Source.Archives.Count) + " · " + status;
        StatusText = status;
    }

    [RelayCommand(CanExecute = nameof(CanShowVersions))]
    private async Task ShowVersionsAsync()
    {
        if (Dialogs is not { } dialogs)
            return;
        var versions = new FileVersionsViewModel(Source, SelectedHit!.Hit, _search!.Searched, _skipped);
        await dialogs.ShowFileVersionsAsync(versions);
    }

    private bool CanShowVersions() => SelectedHit is not null && _search is not null && !IsScanning;
}

/// <summary>A path found in one or more archives.</summary>
public sealed class SearchHitViewModel
{
    public SearchHitViewModel(ArchiveSearchHit hit, IReadOnlyList<BorgArchive> searched)
    {
        Hit = hit;
        VersionCount = hit.Versions(searched).Count(version => !version.IsMissing);
        ArchivesText = BorgTexts.Format(Strings.SearchInArchives, hit.Archives.Count, searched.Count);
    }

    public ArchiveSearchHit Hit { get; }

    public string Name => Hit.Name;

    public bool IsFolder => Hit.IsDirectory;

    public bool IsLink => Hit.Kind == ArchiveEntryKind.Symlink;

    public string FolderText => Hit.FolderDisplayPath;

    /// <summary>Different states over the archives searched.</summary>
    public int VersionCount { get; }

    public int ArchiveCount => Hit.Archives.Count;

    /// <summary>"12 of 42".</summary>
    public string ArchivesText { get; }

    /// <summary>The date in the newest archive that has it.</summary>
    public DateTimeOffset Modified => Hit.Newest.Modified;

    public string ModifiedText => IsFolder || Modified == DateTimeOffset.MinValue ? "" : Modified.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);

    public long Size => Hit.Newest.Size;

    public string SizeText => IsLink ? "" : BorgTexts.Size(Size);

    /// <summary>The newest archive that has it.</summary>
    public string LastSeenText => Hit.Archives[0].Start.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);

    public DateTimeOffset LastSeen => Hit.Archives[0].Start;
}
