using System.Collections.ObjectModel;
using System.Globalization;
using BorgStudio.App.Resources;
using BorgStudio.Core.Borg;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BorgStudio.App.ViewModels;

/// <summary>
/// The versions of a file (or folder) across all archives of a repository – one row per change, not per archive – with
/// the archives it is missing from. Any version can be restored (from the newest archive that has it).
/// </summary>
public sealed partial class FileVersionsViewModel : ArchiveScanViewModel
{
    private readonly string _borgPath;
    private Task? _loading;

    /// <summary>For an entry of an archive (from the browser): the other archives are looked through when the window opens.</summary>
    public FileVersionsViewModel(ArchiveContentsSource source, ArchiveNode node)
        : this(source, node.BorgPath, node.Name, node.Kind)
    {
    }

    /// <summary>For a hit of the search over all archives: its versions are known already.</summary>
    public FileVersionsViewModel(ArchiveContentsSource source, ArchiveSearchHit hit, IReadOnlyList<BorgArchive> searched, int skipped)
        : this(source, hit.BorgPath, hit.Name, hit.Kind)
    {
        Show(searched.Select(archive => (archive, hit.StateIn(archive))).ToList(), skipped);
        ShowSkipped(skipped);
        _loading = Task.CompletedTask;
    }

    private FileVersionsViewModel(ArchiveContentsSource source, string borgPath, string name, ArchiveEntryKind kind) : base(source)
    {
        _borgPath = borgPath;
        Name = name;
        IsFolder = kind == ArchiveEntryKind.Directory;
        PathText = ArchiveTree.DisplayPathOf(borgPath);
    }

    public string Name { get; }

    public bool IsFolder { get; }

    public string Title => BorgTexts.Format(Strings.VersionsTitle, Name);

    /// <summary>"C:\Users\me\Dokumente\letter.txt".</summary>
    public string PathText { get; }

    public ObservableCollection<FileVersionRowViewModel> Versions { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RestoreCommand))]
    public partial FileVersionRowViewModel? SelectedVersion { get; set; }

    /// <summary>"3 versions in 12 of 42 archives".</summary>
    [ObservableProperty]
    public partial string SummaryText { get; private set; } = "";

    /// <summary>Looking through the archives, started when the window opens (tests wait for it).</summary>
    public Task Loading => _loading ?? Task.CompletedTask;

    /// <summary>Starts looking through the archives, unless done already.</summary>
    public Task Begin() => _loading ??= LoadAsync();

    protected override Task RescanAsync() => _loading = LoadAsync();

    protected override void OnScanningChanged() => RestoreCommand.NotifyCanExecuteChanged();

    private async Task LoadAsync()
    {
        var states = new List<(BorgArchive, ArchiveEntryState?)>();
        var outcome = await ScanAsync(Strings.VersionsLoading, (archive, tree, _) =>
        {
            var node = tree.Find(_borgPath);
            lock (states)
                states.Add((archive, node is null ? null : ArchiveEntryState.Of(node)));
        });
        if (outcome is null)
        {
            SummaryText = "";
            return;
        }
        Show(states, outcome.Skipped);
        if (outcome.Cancelled)
            SummaryText = BorgTexts.Format(Strings.ScanCancelled, outcome.Searched.Count, Source.Archives.Count) + " · " + SummaryText;
    }

    private void Show(IReadOnlyList<(BorgArchive Archive, ArchiveEntryState? State)> states, int skipped)
    {
        var selected = SelectedVersion?.Version.Newest;
        Versions.Clear();
        var versions = FileVersions.Group(states);
        var newest = versions.FirstOrDefault(version => !version.IsMissing);
        foreach (var version in versions)
            Versions.Add(new FileVersionRowViewModel(version, version == newest));
        SelectedVersion = Versions.FirstOrDefault(row => row.Version.Newest == selected) ?? Versions.FirstOrDefault(row => !row.IsMissing);
        var present = states.Count(state => state.State is not null);
        var count = versions.Count(version => !version.IsMissing);
        SummaryText = BorgTexts.Format(count == 1 ? Strings.VersionsSummaryOne : Strings.VersionsSummary, count, present, states.Count + skipped);
    }

    [RelayCommand(CanExecute = nameof(CanRestore))]
    private async Task RestoreAsync()
    {
        var archive = SelectedVersion!.Version.Newest;
        if (await GetTreeAsync(archive) is not { } tree)
            return;
        if (tree.Find(_borgPath) is not { } node)
        {
            Error = BorgTexts.Format(Strings.VersionsNotFound, archive.Name);
            return;
        }
        var restore = Source.RestoreFactory!(archive, [node]);
        if (Dialogs is { } dialogs)
        {
            restore.Dialogs = dialogs;
            await dialogs.ShowRestoreAsync(restore);
        }
    }

    private bool CanRestore() => SelectedVersion is { IsMissing: false } && Source.RestoreFactory is not null && !IsScanning;
}

/// <summary>A version in the list: how the file looked, and in which archives.</summary>
public sealed class FileVersionRowViewModel(FileVersion version, bool isNewest)
{
    public FileVersion Version { get; } = version;

    public bool IsMissing => Version.IsMissing;

    /// <summary>The most recent state – what the newest archive holding it has.</summary>
    public bool IsNewest { get; } = isNewest;

    /// <summary>The file's date, or "not in these archives".</summary>
    public string ModifiedText => Version.State is { } state
        ? state.Kind == ArchiveEntryKind.Directory || state.Modified == DateTimeOffset.MinValue
            ? "–"
            : state.Modified.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)
        : Strings.VersionsMissing;

    public DateTimeOffset Modified => Version.State?.Modified ?? DateTimeOffset.MinValue;

    public string SizeText => Version.State is { } state
        ? state.Kind == ArchiveEntryKind.Directory
            ? BorgTexts.Format(Strings.VersionsFolderSize, BorgTexts.Size(state.Size), state.FileCount)
            : state.Kind == ArchiveEntryKind.Symlink ? "" : BorgTexts.Size(state.Size)
        : "";

    public long Size => Version.State?.Size ?? -1;

    public int ArchiveCount => Version.Archives.Count;

    /// <summary>"1 archive" / "12 archives".</summary>
    public string ArchivesText => BorgTexts.Format(ArchiveCount == 1 ? Strings.VersionsArchiveOne : Strings.VersionsArchives, ArchiveCount);

    /// <summary>When the archives were made: "06.11.2026 10:00" or "01.10.2026 – 06.11.2026".</summary>
    public string PeriodText => ArchiveCount == 1
        ? Format(Version.Newest.Start, "g")
        : $"{Format(Version.Oldest.Start, "d")} – {Format(Version.Newest.Start, "d")}";

    public DateTimeOffset NewestArchive => Version.Newest.Start;

    /// <summary>The archive a restore takes it from.</summary>
    public string Tip => BorgTexts.Format(Strings.VersionsFrom, Version.Newest.Name);

    private static string Format(DateTimeOffset time, string format) =>
        time == DateTimeOffset.MinValue ? "–" : time.ToLocalTime().ToString(format, CultureInfo.CurrentCulture);
}
