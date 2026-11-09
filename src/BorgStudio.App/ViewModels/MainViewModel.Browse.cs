using BorgStudio.App.Resources;
using BorgStudio.App.Services;
using BorgStudio.Core.Borg;
using BorgStudio.Core.Progress;
using BorgStudio.Plugins;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BorgStudio.App.ViewModels;

/// <summary>
/// Opening an archive from the "Archives" tab: its contents come from the cache at once, or are read from borg
/// (<c>borg list --json-lines</c>) while the browser shows how far it is, and are cached then – archives never change.
/// </summary>
public partial class MainViewModel
{
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenArchiveCommand))]
    public partial ArchiveItemViewModel? SelectedArchive { get; set; }

    /// <summary>The loading started for the open browser (tests wait for it).</summary>
    internal Task BrowserLoad { get; private set; } = Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanOpenArchive))]
    private async Task OpenArchiveAsync()
    {
        var item = SelectedRepository!;
        var archive = SelectedArchive!;
        if (!TryGetLocation(item, out var location))
            return;

        var repositoryId = item.Config.Id;
        var browser = new ArchiveBrowserViewModel(archive.Archive, archive.JobName) { Dialogs = _services.Dialogs };
        using var cancellation = new CancellationTokenSource();
        BrowserLoad = Task.CompletedTask;
        var cached = _services.Archives.LoadContents(repositoryId, archive.Archive.Id);
        // Login and passphrase before the browser opens (nothing may ask on top of it later); usually known from the
        // tab already. Without them a cached archive can still be looked at, but not restored.
        var access = await GetArchiveAccessAsync(item, location);
        if (access is { } known)
            browser.RestoreFactory = (items, whole) =>
                NewRestore(repositoryId, location, known, archive.Archive, items, whole, browser.Dialogs ?? _services.Dialogs);
        var source = NewContentsSource(item, location, access);
        browser.VersionsFactory = node => new FileVersionsViewModel(source, node);
        if (cached is not null)
            browser.Show(cached);
        else if (access is null)
            return;
        // The PIN may just have been entered for the passphrase: then the cache is readable now.
        else if (_services.Archives.LoadContents(repositoryId, archive.Archive.Id) is { } unlocked)
            browser.Show(unlocked);
        else
            BrowserLoad = LoadContentsAsync(repositoryId, location, access.Value, archive.Archive, browser, cancellation);

        await _services.Dialogs.ShowArchiveBrowserAsync(browser);
        // Closed (or locked away) while borg was still reading: stop it.
        await cancellation.CancelAsync();
        await BrowserLoad;
    }

    private bool CanOpenArchive() =>
        SelectedArchive is not null && !IsBackupRunning && !IsWorking && SelectedRepository?.Location is not null
        && Borg is { Support: BorgSupport.Supported };

    /// <summary>Searches file names in all archives of the repository (tab "Archives").</summary>
    [RelayCommand(CanExecute = nameof(CanSearchArchives))]
    private async Task SearchArchivesAsync()
    {
        var item = SelectedRepository!;
        if (!TryGetLocation(item, out var location))
            return;
        var access = await GetArchiveAccessAsync(item, location);
        var source = NewContentsSource(item, location, access);
        // Without the passphrase only archives read before can be searched.
        if (access is null && !source.Archives.Any(source.IsCached))
            return;
        await _services.Dialogs.ShowArchiveSearchAsync(new ArchiveSearchViewModel(source));
    }

    private bool CanSearchArchives() =>
        Archives.Count > 0 && !IsBackupRunning && !IsWorking && SelectedRepository?.Location is not null
        && Borg is { Support: BorgSupport.Supported };

    /// <summary>The archives in the tab and how to get at their contents: the cache, or borg (with login and passphrase).</summary>
    private ArchiveContentsSource NewContentsSource(RepositoryItemViewModel item, RepositoryLocation location,
        (SshAccessHolder Ssh, PassphraseHolder Passphrase)? access)
    {
        var repositoryId = item.Config.Id;
        var key = DurationHistory.Key(repositoryId, "contents");
        var borg = Borg!;
        return new ArchiveContentsSource
        {
            RepositoryName = item.Name,
            Archives = [.. Archives.Select(archive => archive.Archive)],
            IsCached = archive => _services.Archives.HasContents(repositoryId, archive.Id),
            LoadCached = archive => _services.Archives.LoadContents(repositoryId, archive.Id),
            Save = (archive, tree) => _services.Archives.SaveContents(repositoryId, archive.Id, tree),
            Read = access is { } known
                ? (archive, read, cancellationToken) => _services.BorgClient.ListContentsAsync(borg, location, archive.Name,
                    known.Passphrase.Value, known.Ssh.Access, read, cancellationToken)
                : null,
            ExpectedRead = _services.Durations.Get(key),
            RecordRead = elapsed => _services.Durations.Record(key, elapsed),
            RestoreFactory = access is { } granted
                ? (archive, items) => NewRestore(repositoryId, location, granted, archive, items, whole: false, _services.Dialogs)
                : null,
        };
    }

    private RestoreViewModel NewRestore(Guid repositoryId, RepositoryLocation location,
        (SshAccessHolder Ssh, PassphraseHolder Passphrase) access, BorgArchive archive, IReadOnlyList<ArchiveNode> items, bool whole,
        IDialogService dialogs)
    {
        var borg = Borg!;
        return new RestoreViewModel(archive.Name, items, whole, borg.Runtime,
            (run, progress, cancellationToken) => Task.Run(() => _services.BorgClient.ExtractAsync(borg, location, archive.Name, run.Paths,
                run.TargetDirectory, run.StripComponents, access.Passphrase.Value, access.Ssh.Access, progress, cancellationToken)),
            dialogs,
            // borg reads the archive's file list to work out the size first: about as long as listing it took.
            _services.Durations.Get(DurationHistory.Key(repositoryId, "contents")));
    }

    private async Task LoadContentsAsync(Guid repositoryId, RepositoryLocation location,
        (SshAccessHolder Ssh, PassphraseHolder Passphrase) access, BorgArchive archive, ArchiveBrowserViewModel browser,
        CancellationTokenSource cancellation)
    {
        // borg reports no progress here: the number of files (if the details are known) or last time's duration tell how far.
        var key = DurationHistory.Key(repositoryId, "contents");
        long? expected = archive.Stats?.Files;
        browser.StartLoading(expected is null ? _services.Durations.Get(key) : null, cancellation.Cancel);
        var progress = new ViewModelProgress<long>(this, read => browser.ReportLoading(read, expected));
        var borg = Borg!;

        var result = await Task.Run(() => _services.BorgClient.ListContentsAsync(borg, location, archive.Name, access.Passphrase.Value,
            access.Ssh.Access, progress, cancellation.Token));
        if (result.Value is { } tree)
        {
            _services.Durations.Record(key, browser.Progress.Elapsed);
            browser.Show(tree);
            await Task.Run(() => _services.Archives.SaveContents(repositoryId, archive.Id, tree));
        }
        else
            browser.Fail(result.Error!.Kind == BorgErrorKind.Cancelled
                ? Strings.BrowserLoadCancelled
                : BorgTexts.Format(Strings.BrowserLoadFailed, BorgTexts.Describe(result.Error)));
    }
}
