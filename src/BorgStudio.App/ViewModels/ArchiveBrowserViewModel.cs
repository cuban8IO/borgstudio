using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.RegularExpressions;
using BorgStudio.App.Resources;
using BorgStudio.Core.Borg;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BorgStudio.App.ViewModels;

/// <summary>
/// Looking into an archive: folder tree, the entries of the current folder, a search over the whole archive and a
/// selection of files and folders (to restore them). The contents come from the cache at once or are read from borg
/// first, with progress.
/// </summary>
public sealed partial class ArchiveBrowserViewModel : ViewModelBase
{
    /// <summary>More search results than this are not shown.</summary>
    public const int MaximumResults = 2000;

    private readonly HashSet<ArchiveNode> _selected = [];
    private CancellationTokenSource? _search;

    public ArchiveBrowserViewModel(BorgArchive archive, string? jobName)
    {
        Archive = archive;
        JobName = jobName;
    }

    public BorgArchive Archive { get; }

    public string? JobName { get; }

    public string Title => BorgTexts.Format(Strings.BrowserTitle, Archive.Name);

    /// <summary>"Erstellt 06.11.2026 10:00 · Job „Dokumente“".</summary>
    public string Subtitle => JobName is null
        ? BorgTexts.Format(Strings.BrowserCreated, Archive.Start.ToLocalTime().ToString("g", CultureInfo.CurrentCulture))
        : BorgTexts.Format(Strings.BrowserCreatedByJob, Archive.Start.ToLocalTime().ToString("g", CultureInfo.CurrentCulture), JobName);

    /// <summary>Reading the file list from borg.</summary>
    public ProgressViewModel Progress { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReady))]
    public partial bool IsLoading { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; private set; }

    public bool HasError => Error is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReady))]
    [NotifyCanExecuteChangedFor(nameof(SearchCommand))]
    public partial ArchiveTree? Tree { get; private set; }

    public bool IsReady => Tree is not null && !IsLoading;

    /// <summary>"12.345 Dateien in 678 Ordnern · 3,2 GB".</summary>
    [ObservableProperty]
    public partial string SummaryText { get; private set; } = "";

    /// <summary>The top folders (e.g. "C:", "home") for the tree on the left.</summary>
    public ObservableCollection<FolderNodeViewModel> Folders { get; } = [];

    /// <summary>The folder selected in the tree.</summary>
    [ObservableProperty]
    public partial FolderNodeViewModel? SelectedFolder { get; set; }

    /// <summary>The folder whose entries are shown.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentPathText))]
    [NotifyCanExecuteChangedFor(nameof(UpCommand))]
    public partial ArchiveNode? CurrentFolder { get; private set; }

    public string CurrentPathText => CurrentFolder?.DisplayPath ?? "";

    /// <summary>The entries of the current folder, or the search results.</summary>
    public ObservableCollection<EntryViewModel> Entries { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoEntries))]
    public partial bool IsEmpty { get; private set; }

    public bool HasNoEntries => IsEmpty && !IsSearchResult;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SearchCommand))]
    public partial string SearchText { get; set; } = "";

    /// <summary>The search results are shown instead of a folder.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoEntries))]
    [NotifyCanExecuteChangedFor(nameof(UpCommand))]
    public partial bool IsSearchResult { get; private set; }

    [ObservableProperty]
    public partial bool IsSearching { get; private set; }

    [ObservableProperty]
    public partial string? SearchStatus { get; private set; }

    /// <summary>"3 ausgewählt · 12 MB" or "Nichts ausgewählt".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyCanExecuteChangedFor(nameof(ClearSelectionCommand))]
    public partial string SelectionText { get; private set; } = Strings.BrowserSelectionNone;

    public bool HasSelection => _selected.Count > 0;

    /// <summary>The selected files and folders, without those inside a selected folder.</summary>
    public IReadOnlyList<ArchiveNode> Selection =>
        [.. _selected.Where(node => !Ancestors(node).Any(_selected.Contains)).OrderBy(node => node.BorgPath, StringComparer.Ordinal)];

    // --- Loading ---

    /// <summary>Reading from borg starts; <paramref name="cancel"/> stops it.</summary>
    public void StartLoading(TimeSpan? expected, Action cancel)
    {
        Error = null;
        IsLoading = true;
        Progress.Start(Strings.BrowserLoading, expected, cancel);
    }

    /// <summary>How many entries borg listed so far; <paramref name="expected"/> is about how many there are (if known).</summary>
    public void ReportLoading(long read, long? expected)
    {
        if (expected is > 0 and var total)
        {
            // borg counts files only: with the folders there are a few more entries than that.
            Progress.Report(read, Math.Max(total, read * 1.02),
                BorgTexts.Format(Strings.BrowserEntriesOf, read, total));
        }
        else
            Progress.SetDetail(BorgTexts.Format(Strings.BrowserEntriesRead, read));
    }

    /// <summary>The contents are there (from the cache or from borg).</summary>
    public void Show(ArchiveTree tree)
    {
        Progress.Stop();
        IsLoading = false;
        Tree = tree;
        SummaryText = BorgTexts.Format(Strings.BrowserSummary, tree.Root.FileCount, tree.All().LongCount(node => node.IsDirectory),
            BorgTexts.Size(tree.Root.Size));
        Folders.Clear();
        foreach (var top in tree.Root.Children.Where(node => node.IsDirectory))
            Folders.Add(new FolderNodeViewModel(top));
        // Start where the data is: down through folders that only hold one folder (e.g. C: › Users › me).
        var start = tree.Root;
        while (start.Children is [{ IsDirectory: true } only])
            start = only;
        Navigate(start);
    }

    public void Fail(string error)
    {
        Progress.Stop();
        IsLoading = false;
        Error = error;
    }

    // --- Navigating ---

    partial void OnSelectedFolderChanged(FolderNodeViewModel? value)
    {
        if (value is not null && value.Node != CurrentFolder)
            Navigate(value.Node);
    }

    /// <summary>Opens a folder (double-click), or does nothing for files.</summary>
    [RelayCommand]
    private void Open(EntryViewModel? entry)
    {
        if (entry?.Node is { IsDirectory: true } folder)
            Navigate(folder);
        else if (entry is { IsSearchResult: true } && entry.Node.Parent is { } parent)
            Navigate(parent); // a file found: show it in its folder
    }

    [RelayCommand(CanExecute = nameof(CanGoUp))]
    private void Up()
    {
        if (IsSearchResult)
            Navigate(CurrentFolder ?? Tree!.Root);
        else if (CurrentFolder?.Parent is { } parent)
            Navigate(parent);
    }

    private bool CanGoUp() => IsSearchResult || CurrentFolder?.Parent is not null;

    public void Navigate(ArchiveNode folder)
    {
        CancelSearch();
        IsSearchResult = false;
        SearchStatus = null;
        CurrentFolder = folder;
        ShowEntries(folder.Children, searchResult: false);
        SyncTree(folder);
    }

    /// <summary>Expands the tree down to <paramref name="folder"/> and selects it there.</summary>
    private void SyncTree(ArchiveNode folder)
    {
        var path = Ancestors(folder).Reverse().Append(folder).Where(node => node.Parent is not null).ToList();
        IReadOnlyList<FolderNodeViewModel> level = Folders;
        FolderNodeViewModel? found = null;
        foreach (var node in path)
        {
            found = level.FirstOrDefault(candidate => candidate.Node == node);
            if (found is null)
                break;
            if (node != folder)
                found.IsExpanded = true;
            level = found.Children;
        }
        SelectedFolder = found;
    }

    private void ShowEntries(IEnumerable<ArchiveNode> nodes, bool searchResult)
    {
        Entries.Clear();
        foreach (var node in nodes)
            Entries.Add(new EntryViewModel(node, searchResult, this));
        IsEmpty = Entries.Count == 0;
    }

    // --- Searching ---

    [RelayCommand(CanExecute = nameof(CanSearch))]
    private async Task SearchAsync()
    {
        var text = SearchText.Trim();
        var tree = Tree!;
        CancelSearch();
        var cancellation = _search = new CancellationTokenSource();
        IsSearching = true;
        try
        {
            var matcher = Matcher(text);
            var results = await Task.Run(() => tree.All().Where(node => matcher(node.Name)).Take(MaximumResults + 1).ToList(),
                cancellation.Token);
            if (cancellation.IsCancellationRequested)
                return;
            IsSearchResult = true;
            ShowEntries(results.Take(MaximumResults), searchResult: true);
            SearchStatus = results.Count == 0 ? BorgTexts.Format(Strings.BrowserSearchNone, text)
                : results.Count > MaximumResults ? BorgTexts.Format(Strings.BrowserSearchTooMany, MaximumResults)
                : BorgTexts.Format(Strings.BrowserSearchResults, results.Count, text);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (_search == cancellation)
                IsSearching = false;
        }
    }

    private bool CanSearch() => Tree is not null && !string.IsNullOrWhiteSpace(SearchText);

    [RelayCommand]
    private void ClearSearch()
    {
        SearchText = "";
        if (IsSearchResult)
            Navigate(CurrentFolder ?? Tree!.Root);
    }

    private void CancelSearch()
    {
        _search?.Cancel();
        _search = null;
        IsSearching = false;
    }

    /// <summary>"rechnung" finds names containing it; with * or ? it is a pattern for the whole name ("*.pdf"). Case is ignored.</summary>
    internal static Func<string, bool> Matcher(string text)
    {
        if (text.IndexOfAny(['*', '?']) < 0)
            return name => name.Contains(text, StringComparison.CurrentCultureIgnoreCase);
        var pattern = new Regex("^" + Regex.Escape(text).Replace(@"\*", ".*", StringComparison.Ordinal).Replace(@"\?", ".", StringComparison.Ordinal) + "$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return pattern.IsMatch;
    }

    // --- Selection ---

    internal bool IsSelected(ArchiveNode node) => _selected.Contains(node);

    internal void SetSelected(ArchiveNode node, bool selected)
    {
        if (selected ? !_selected.Add(node) : !_selected.Remove(node))
            return;
        UpdateSelectionText();
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void ClearSelection()
    {
        _selected.Clear();
        foreach (var entry in Entries)
            entry.Refresh();
        UpdateSelectionText();
    }

    private void UpdateSelectionText()
    {
        var selection = Selection;
        SelectionText = selection.Count == 0 ? Strings.BrowserSelectionNone
            : BorgTexts.Format(Strings.BrowserSelection, selection.Count, BorgTexts.Size(selection.Sum(node => node.Size)));
    }

    private static IEnumerable<ArchiveNode> Ancestors(ArchiveNode node)
    {
        for (var parent = node.Parent; parent is not null; parent = parent.Parent)
            yield return parent;
    }
}

/// <summary>A folder in the tree on the left; its subfolders are created when first needed.</summary>
public sealed partial class FolderNodeViewModel(ArchiveNode node) : ObservableObject
{
    private IReadOnlyList<FolderNodeViewModel>? _children;

    public ArchiveNode Node { get; } = node;

    public string Name => Node.Name;

    public IReadOnlyList<FolderNodeViewModel> Children =>
        _children ??= [.. Node.Children.Where(child => child.IsDirectory).Select(child => new FolderNodeViewModel(child))];

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }
}

/// <summary>A file or folder in the list on the right.</summary>
public sealed partial class EntryViewModel(ArchiveNode node, bool isSearchResult, ArchiveBrowserViewModel owner) : ObservableObject
{
    public ArchiveNode Node { get; } = node;

    public bool IsSearchResult { get; } = isSearchResult;

    public string Name => Node.Name;

    public bool IsFolder => Node.IsDirectory;

    public bool IsLink => Node.Kind == ArchiveEntryKind.Symlink;

    /// <summary>Where a search result is ("C:\Users\me\Dokumente").</summary>
    public string LocationText => Node.Parent?.DisplayPath ?? "";

    public string SizeText => IsLink ? "" : BorgTexts.Size(Node.Size);

    public string ModifiedText => Node.Modified == DateTimeOffset.MinValue ? "" : Node.Modified.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);

    /// <summary>Folders: how many files; links: where they point.</summary>
    public string? Tip => IsFolder ? BorgTexts.Format(Strings.BrowserFolderFiles, Node.FileCount)
        : IsLink ? $"→ {Node.LinkTarget}" : null;

    public bool IsSelected
    {
        get => owner.IsSelected(Node);
        set
        {
            owner.SetSelected(Node, value);
            OnPropertyChanged();
        }
    }

    internal void Refresh() => OnPropertyChanged(nameof(IsSelected));
}
