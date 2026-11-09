using System.Diagnostics;
using BorgStudio.App.Resources;
using BorgStudio.App.Services;
using BorgStudio.Core.Borg;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BorgStudio.App.ViewModels;

/// <summary>Reads the contents of an archive from borg; <paramref name="read"/> counts the entries listed so far.</summary>
public delegate Task<BorgResult<ArchiveTree>> ContentsReader(BorgArchive archive, IProgress<long> read, CancellationToken cancellationToken);

/// <summary>The archives of a repository and how to get at their contents – to look through all of them.</summary>
public sealed class ArchiveContentsSource
{
    public required string RepositoryName { get; init; }

    /// <summary>Newest first.</summary>
    public required IReadOnlyList<BorgArchive> Archives { get; init; }

    /// <summary>Whether the contents are cached (no borg needed).</summary>
    public required Func<BorgArchive, bool> IsCached { get; init; }

    /// <summary>The cached contents; <c>null</c> if not (or no longer) readable.</summary>
    public required Func<BorgArchive, ArchiveTree?> LoadCached { get; init; }

    public required Action<BorgArchive, ArchiveTree> Save { get; init; }

    /// <summary>Asks borg; <c>null</c> without login or passphrase (then only cached archives can be looked through).</summary>
    public ContentsReader? Read { get; init; }

    /// <summary>About how long borg needs to list one archive (last time).</summary>
    public TimeSpan? ExpectedRead { get; init; }

    /// <summary>Remembers how long listing one archive took.</summary>
    public Action<TimeSpan>? RecordRead { get; init; }

    /// <summary>The restore dialog for entries of an archive; <c>null</c> if restoring isn't possible (no passphrase).</summary>
    public Func<BorgArchive, IReadOnlyList<ArchiveNode>, RestoreViewModel>? RestoreFactory { get; init; }
}

/// <summary>What looking through the archives came to.</summary>
/// <param name="Searched">The archives looked through, newest first.</param>
/// <param name="Skipped">Not looked through: never read and the user chose not to read them now.</param>
public sealed record ArchiveScanOutcome(IReadOnlyList<BorgArchive> Searched, int Skipped, bool Cancelled, string? Error);

/// <summary>
/// Looking through all archives of a repository one by one (only one archive's contents in memory at a time): the
/// cached ones at once, the others read by borg (once – then cached) – after asking, since that can take minutes. Shows
/// "Archive 7 of 42" with a progress bar and the remaining time.
/// </summary>
public abstract partial class ArchiveScanViewModel(ArchiveContentsSource source) : ViewModelBase
{
    /// <summary>Reading an archive from borg takes about this many times as long as opening a cached one.</summary>
    internal const double ReadWeight = 40;

    private CancellationTokenSource? _cancellation;

    /// <summary>Whether archives never read may be read by borg: <c>null</c> = not asked yet.</summary>
    private bool? _readMissing;

    public ArchiveContentsSource Source { get; } = source;

    /// <summary>Where questions and further windows appear; set by the window.</summary>
    public IDialogService? Dialogs { get; set; }

    public ProgressViewModel Progress { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadMissingCommand))]
    public partial bool IsScanning { get; private set; }

    partial void OnIsScanningChanged(bool value) => OnScanningChanged();

    /// <summary>Lets derived commands refresh their state.</summary>
    protected virtual void OnScanningChanged()
    {
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; protected set; }

    public bool HasError => Error is not null;

    /// <summary>"12 archives were not searched: their file lists were never read." – with "Read them" next to it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSkipped))]
    [NotifyCanExecuteChangedFor(nameof(LoadMissingCommand))]
    public partial string? SkippedText { get; private set; }

    public bool HasSkipped => SkippedText is not null;

    /// <summary>Tells how many archives were left out (and offers to read them).</summary>
    protected void ShowSkipped(int skipped) =>
        SkippedText = skipped switch
        {
            <= 0 => null,
            1 => Source.Read is null ? Strings.ScanSkippedNoAccessOne : Strings.ScanSkippedOne,
            _ => BorgTexts.Format(Source.Read is null ? Strings.ScanSkippedNoAccess : Strings.ScanSkipped, skipped),
        };

    /// <summary>Reads the archives skipped before and looks again.</summary>
    [RelayCommand(CanExecute = nameof(CanLoadMissing))]
    private Task LoadMissingAsync()
    {
        _readMissing = true;
        return RescanAsync();
    }

    private bool CanLoadMissing() => HasSkipped && Source.Read is not null && !IsScanning;

    /// <summary>Runs the last scan again (after "read the missing ones").</summary>
    protected abstract Task RescanAsync();

    /// <summary>Stops looking (the window was closed).</summary>
    public void Abandon() => _cancellation?.Cancel();

    /// <summary>
    /// Looks through the archives, newest first, calling <paramref name="visit"/> (on a background thread) for each.
    /// <c>null</c> if the user did not want to wait for borg and nothing is cached.
    /// </summary>
    protected async Task<ArchiveScanOutcome?> ScanAsync(string text, Action<BorgArchive, ArchiveTree, CancellationToken> visit)
    {
        Error = null;
        var archives = Source.Archives;
        var missing = archives.Where(archive => !Source.IsCached(archive)).ToList();
        if (missing.Count > 0 && Source.Read is not null && _readMissing is null)
            _readMissing = await AskReadMissingAsync(missing.Count, archives.Count);
        var read = Source.Read is not null && _readMissing == true;
        var included = archives.Where(archive => read || !missing.Contains(archive)).ToList();
        ShowSkipped(archives.Count - included.Count);
        if (included.Count == 0)
            return null;

        _cancellation?.Cancel();
        using var cancellation = _cancellation = new CancellationTokenSource();
        var toRead = read ? missing.Count : 0;
        var total = included.Count - toRead + toRead * ReadWeight;
        double done = 0;
        IsScanning = true;
        Progress.Start(text, toRead > 0 ? Source.ExpectedRead * toRead : null, cancellation.Cancel);
        var searched = new List<BorgArchive>();
        try
        {
            for (var index = 0; index < included.Count; index++)
            {
                var archive = included[index];
                var detail = BorgTexts.Format(Strings.ScanArchiveOf, index + 1, included.Count, archive.Name);
                Progress.Report(done, total, detail);
                var cached = missing.Contains(archive) ? null : await Task.Run(() => Source.LoadCached(archive), cancellation.Token);
                var tree = cached;
                if (tree is null)
                {
                    if (!read)
                        continue; // became unreadable meanwhile (e.g. locked): left out
                    var start = done;
                    var expected = archive.Stats?.Files;
                    var progress = new ScanProgress(this, entries =>
                    {
                        if (expected is > 0 and var files)
                            Progress.Report(start + ReadWeight * Math.Min(entries / (double)files, 0.98), total, detail);
                    });
                    var result = await ReadAsync(archive, progress, cancellation.Token);
                    if (result.Value is null)
                    {
                        if (result.Error!.Kind == BorgErrorKind.Cancelled)
                            return new ArchiveScanOutcome(searched, archives.Count - included.Count, Cancelled: true, null);
                        Error = BorgTexts.Format(Strings.ScanReadFailed, archive.Name, BorgTexts.Describe(result.Error));
                        return new ArchiveScanOutcome(searched, archives.Count - included.Count, Cancelled: false, Error);
                    }
                    tree = result.Value;
                }
                await Task.Run(() => visit(archive, tree, cancellation.Token), cancellation.Token);
                searched.Add(archive);
                done += cached is null ? ReadWeight : 1;
                Progress.Report(done, total, detail);
            }
            return new ArchiveScanOutcome(searched, archives.Count - included.Count, Cancelled: false, null);
        }
        catch (OperationCanceledException)
        {
            return new ArchiveScanOutcome(searched, archives.Count - included.Count, Cancelled: true, null);
        }
        finally
        {
            Progress.Stop();
            IsScanning = false;
            if (_cancellation == cancellation)
                _cancellation = null;
        }
    }

    /// <summary>The contents of one archive, for restoring from it: from the cache, or read by borg with progress.</summary>
    protected async Task<ArchiveTree?> GetTreeAsync(BorgArchive archive)
    {
        Error = null;
        if (Source.IsCached(archive) && await Task.Run(() => Source.LoadCached(archive)) is { } cached)
            return cached;
        if (Source.Read is null)
            return null;

        _cancellation?.Cancel();
        using var cancellation = _cancellation = new CancellationTokenSource();
        IsScanning = true;
        var expected = archive.Stats?.Files;
        Progress.Start(Strings.BrowserLoading, Source.ExpectedRead, cancellation.Cancel);
        try
        {
            var progress = new ScanProgress(this, entries =>
            {
                if (expected is > 0 and var files)
                    Progress.Report(Math.Min(entries / (double)files, 0.98), 1, BorgTexts.Format(Strings.BrowserEntriesOf, entries, files));
                else
                    Progress.SetDetail(BorgTexts.Format(Strings.BrowserEntriesRead, entries));
            });
            var result = await ReadAsync(archive, progress, cancellation.Token);
            if (result.Value is null && result.Error!.Kind != BorgErrorKind.Cancelled)
                Error = BorgTexts.Format(Strings.ScanReadFailed, archive.Name, BorgTexts.Describe(result.Error));
            return result.Value;
        }
        finally
        {
            Progress.Stop();
            IsScanning = false;
            if (_cancellation == cancellation)
                _cancellation = null;
        }
    }

    /// <summary>Lets borg read an archive and caches what it read.</summary>
    private async Task<BorgResult<ArchiveTree>> ReadAsync(BorgArchive archive, IProgress<long> progress, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        var result = await Task.Run(() => Source.Read!(archive, progress, cancellationToken), CancellationToken.None);
        if (result.Value is { } tree)
        {
            Source.RecordRead?.Invoke(watch.Elapsed);
            await Task.Run(() => Source.Save(archive, tree), CancellationToken.None);
        }
        return result;
    }

    /// <summary>"12 of 42 archives were never opened … about 6:00." – <c>true</c> to read them now.</summary>
    private async Task<bool> AskReadMissingAsync(int missing, int total)
    {
        if (Dialogs is null)
            return false;
        var estimate = Source.ExpectedRead is { } perArchive
            ? BorgTexts.Format(Strings.ScanReadEstimate, BorgTexts.Duration(perArchive * missing))
            : Strings.ScanReadEstimateUnknown;
        var message = BorgTexts.Format(Strings.ScanReadMessage, missing, total) + "\n\n" + estimate;
        var onlyCached = missing < total ? Strings.ScanReadOnlyCached : Strings.Cancel;
        return await Dialogs.ConfirmAsync(Strings.ScanReadTitle, message, Strings.ScanReadConfirm, onlyCached);
    }

    /// <summary>borg reports on a background thread; the bar is updated on the view model's.</summary>
    private sealed class ScanProgress(ArchiveScanViewModel owner, Action<long> report) : IProgress<long>
    {
        public void Report(long value) => owner.OnViewModelThread(() => report(value));
    }
}
