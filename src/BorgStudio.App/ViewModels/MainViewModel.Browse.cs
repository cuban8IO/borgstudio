using BorgStudio.App.Resources;
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
        var browser = new ArchiveBrowserViewModel(archive.Archive, archive.JobName);
        using var cancellation = new CancellationTokenSource();
        BrowserLoad = Task.CompletedTask;
        if (_services.Archives.LoadContents(repositoryId, archive.Archive.Id) is { } cached)
            browser.Show(cached);
        else
        {
            // Asked before the browser opens: nothing may ask on top of it later.
            if (await GetArchiveAccessAsync(item, location) is not { } access)
                return;
            // The PIN may just have been entered for the passphrase: then the cache is readable now.
            if (_services.Archives.LoadContents(repositoryId, archive.Archive.Id) is { } unlocked)
                browser.Show(unlocked);
            else
                BrowserLoad = LoadContentsAsync(repositoryId, location, access, archive.Archive, browser, cancellation);
        }

        await _services.Dialogs.ShowArchiveBrowserAsync(browser);
        // Closed (or locked away) while borg was still reading: stop it.
        await cancellation.CancelAsync();
        await BrowserLoad;
    }

    private bool CanOpenArchive() =>
        SelectedArchive is not null && !IsBackupRunning && !IsWorking && SelectedRepository?.Location is not null
        && Borg is { Support: BorgSupport.Supported };

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
