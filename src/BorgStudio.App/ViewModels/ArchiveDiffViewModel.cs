using System.Collections.ObjectModel;
using System.Globalization;
using BorgStudio.App.Resources;
using BorgStudio.Core.Borg;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BorgStudio.App.ViewModels;

/// <summary>
/// Two archives compared (<c>borg diff</c>): what is new, changed or gone in the newer one – with a filter by kind, a search
/// by path and, on request, the changes of metadata only (permissions, owner, times).
/// </summary>
public sealed partial class ArchiveDiffViewModel : ViewModelBase
{
    /// <summary>The list shows at most this many paths (the summary counts all).</summary>
    public const int MaximumShown = 5000;

    private IReadOnlyList<ArchiveDiffEntry> _entries = [];

    public ArchiveDiffViewModel(BorgArchive older, BorgArchive newer)
    {
        Older = older;
        Newer = newer;
        SelectedKind = KindOptions[0];
    }

    public BorgArchive Older { get; }

    public BorgArchive Newer { get; }

    /// <summary>"Dokumente-pc-2026-11-19 → Dokumente-pc-2026-11-20".</summary>
    public string Title => BorgTexts.Format(Strings.DiffTitle, Older.Name, Newer.Name);

    /// <summary>Stops borg when the window closes while it still reads (set by the caller).</summary>
    public Action? Cancel { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LoadingText))]
    public partial bool IsLoading { get; private set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LoadingText))]
    public partial int EntriesRead { get; private set; }

    public string LoadingText => BorgTexts.Format(Strings.DiffLoading, EntriesRead.ToString("N0", CultureInfo.CurrentCulture));

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; private set; }

    public bool HasError => Error is not null;

    public IReadOnlyList<Option<DiffKind?>> KindOptions { get; } =
    [
        new(null, Strings.DiffKindAll),
        new(DiffKind.Added, Strings.DiffKindAdded),
        new(DiffKind.Modified, Strings.DiffKindModified),
        new(DiffKind.Removed, Strings.DiffKindRemoved),
    ];

    [ObservableProperty]
    public partial Option<DiffKind?> SelectedKind { get; set; }

    [ObservableProperty]
    public partial string? Search { get; set; }

    /// <summary>Also paths whose metadata changed only (permissions, owner, times).</summary>
    [ObservableProperty]
    public partial bool ShowMetadata { get; set; }

    public ObservableCollection<ArchiveDiffItemViewModel> Items { get; } = [];

    /// <summary>"124 neu · 18 geändert · 3 gelöscht · 2 nur Metadaten".</summary>
    [ObservableProperty]
    public partial string SummaryText { get; private set; } = "";

    /// <summary>"Neu hinzugekommen: 1,2 GB · gelöscht: 30 MB".</summary>
    [ObservableProperty]
    public partial string SizeText { get; private set; } = "";

    /// <summary>"Zeigt 5000 von 18 210 – Filter oder Suche grenzen ein."</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMoreText))]
    public partial string? MoreText { get; private set; }

    public bool HasMoreText => MoreText is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    public partial bool HasNoChanges { get; private set; }

    public bool IsEmpty => HasNoChanges;

    partial void OnSelectedKindChanged(Option<DiffKind?> value) => Refresh();

    partial void OnSearchChanged(string? value) => Refresh();

    partial void OnShowMetadataChanged(bool value) => Refresh();

    /// <summary>How many paths borg has reported so far.</summary>
    public void ReportRead(int count) => EntriesRead = count;

    /// <summary>borg is done: the changes, sorted by path.</summary>
    public void Show(IReadOnlyList<ArchiveDiffEntry> entries)
    {
        _entries = [.. entries.OrderBy(entry => entry.Path, StringComparer.Ordinal)];
        IsLoading = false;
        var count = (DiffKind kind) => _entries.Count(entry => entry.Kind == kind);
        SummaryText = BorgTexts.Format(Strings.DiffSummary, count(DiffKind.Added), count(DiffKind.Modified), count(DiffKind.Removed),
            count(DiffKind.Metadata));
        SizeText = BorgTexts.Format(Strings.DiffSizes,
            BorgTexts.Size(_entries.Where(entry => entry.Kind == DiffKind.Added).Sum(entry => entry.Size ?? 0)),
            BorgTexts.Size(_entries.Where(entry => entry.Kind == DiffKind.Removed).Sum(entry => entry.Size ?? 0)));
        HasNoChanges = _entries.Count == 0;
        Refresh();
    }

    public void Fail(string error)
    {
        IsLoading = false;
        Error = error;
    }

    private void Refresh()
    {
        if (IsLoading)
            return;
        var search = Search?.Trim() ?? "";
        var matching = _entries
            .Where(entry => SelectedKind.Value is { } kind ? entry.Kind == kind : ShowMetadata || entry.Kind != DiffKind.Metadata)
            .Where(entry => search.Length == 0 || entry.Path.Contains(search, StringComparison.OrdinalIgnoreCase))
            .ToList();
        Items.Clear();
        foreach (var entry in matching.Take(MaximumShown))
            Items.Add(new ArchiveDiffItemViewModel(entry));
        MoreText = matching.Count > MaximumShown
            ? BorgTexts.Format(Strings.DiffMore, MaximumShown.ToString("N0", CultureInfo.CurrentCulture), matching.Count.ToString("N0", CultureInfo.CurrentCulture))
            : null;
    }

    [RelayCommand]
    private void Close()
    {
        Cancel?.Invoke();
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? CloseRequested;
}

/// <summary>A path in the comparison.</summary>
public sealed class ArchiveDiffItemViewModel(ArchiveDiffEntry entry)
{
    public ArchiveDiffEntry Entry => entry;

    /// <summary>"/home/me/a.txt", inside WSL "C:\Users\me\a.txt".</summary>
    public string Path => ArchiveTree.DisplayPathOf(entry.Path);

    /// <summary>"Neu", "Geändert", "Gelöscht", "Metadaten".</summary>
    public string KindText => entry.Kind switch
    {
        DiffKind.Added => Strings.DiffKindAddedShort,
        DiffKind.Removed => Strings.DiffKindRemovedShort,
        DiffKind.Modified => Strings.DiffKindModifiedShort,
        _ => Strings.DiffKindMetadataShort,
    };

    public bool IsAdded => entry.Kind == DiffKind.Added;

    public bool IsRemoved => entry.Kind == DiffKind.Removed;

    public bool IsModified => entry.Kind == DiffKind.Modified;

    /// <summary>"4,2 MB", "+16 B / −2 B", "Ordner"; the metadata changes for those.</summary>
    public string SizeText => entry switch
    {
        { IsDirectory: true } => Strings.DiffFolder,
        { Kind: DiffKind.Modified } => $"+{BorgTexts.Size(entry.Added)} / −{BorgTexts.Size(entry.Removed)}",
        { Size: { } size } => BorgTexts.Size(size),
        _ => "",
    };

    public string DetailsText => string.Join(" · ", entry.Details);

    public bool HasDetails => entry.Kind == DiffKind.Metadata && entry.Details.Count > 0;
}
