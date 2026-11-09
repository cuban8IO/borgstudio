using System.Collections.ObjectModel;
using BorgStudio.App.Resources;
using BorgStudio.Core.Borg;
using BorgStudio.Core.Progress;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BorgStudio.App.ViewModels;

/// <summary>
/// Mounting archives (<c>borg mount</c>) from the "Archives" tab – one archive or the whole repository – as read-only
/// folders the file manager opens. Several can be mounted at once; the status bar lists them. While a repository has
/// mounts, borg cannot write to it: backups and clean-ups ask to unmount first. Closing the app unmounts everything.
/// </summary>
public partial class MainViewModel
{
    /// <summary>What is mounted right now.</summary>
    public ObservableCollection<MountItemViewModel> Mounts { get; } = [];

    public bool HasMounts => Mounts.Count > 0;

    /// <summary>"2 archives mounted" (status bar).</summary>
    public string MountsText => Mounts.Count == 1 ? Strings.MountsStatusOne : BorgTexts.Format(Strings.MountsStatus, Mounts.Count);

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MountArchiveCommand), nameof(MountRepositoryCommand))]
    public partial bool IsMounting { get; private set; }

    /// <summary>Mounting can take a while (borg reads the archive's metadata first): how long it took last time tells how far.</summary>
    public ProgressViewModel MountProgress { get; } = new();

    /// <summary>The mounting started last (tests wait for it).</summary>
    internal Task MountTask { get; private set; } = Task.CompletedTask;

    /// <summary>Mounts the selected archive and opens it.</summary>
    [RelayCommand(CanExecute = nameof(CanMountArchive))]
    private Task MountArchiveAsync() => MountTask = MountAsync(SelectedArchive!.Archive.Name);

    /// <summary>Mounts the whole repository (one folder per archive) and opens it.</summary>
    [RelayCommand(CanExecute = nameof(CanMountRepository))]
    private Task MountRepositoryAsync() => MountTask = MountAsync(null);

    private bool CanMountArchive() => SelectedArchive is not null && CanMount();

    private bool CanMountRepository() => Archives.Count > 0 && CanMount();

    private bool CanMount() =>
        !IsMounting && !IsBackupRunning && !IsWorking && SelectedRepository?.Location is not null
        && Borg is { Support: BorgSupport.Supported };

    private async Task MountAsync(string? archive)
    {
        var item = SelectedRepository!;
        if (!TryGetLocation(item, out var location))
            return;

        var repositoryId = item.Config.Id;
        if (Mounts.FirstOrDefault(mount => mount.RepositoryId == repositoryId && mount.ArchiveName == archive) is { } mounted)
        {
            await _services.Dialogs.OpenFolderAsync(mounted.OpenPath);
            return;
        }

        var borg = Borg!;
        ArchivesError = null;
        IsMounting = true;
        try
        {
            if (!await EnsureMountSetupAsync(borg))
                return;
            if (await GetArchiveAccessAsync(item, location) is not { } access)
                return;

            var key = DurationHistory.Key(repositoryId, archive is null ? "mount-all" : "mount");
            using var cancellation = new CancellationTokenSource();
            MountProgress.Start(BorgTexts.Format(Strings.Mounting, archive ?? item.Name), _services.Durations.Get(key), cancellation.Cancel);
            var folder = BorgClient.MountFolder(item.Name, archive, Strings.MountAllFolder);
            var result = await Task.Run(() => _services.BorgClient.MountAsync(borg, location, archive, folder,
                access.Passphrase.Value, access.Ssh.Access, cancellation.Token));
            MountProgress.Stop();

            if (result.Value is not { } mount)
            {
                if (result.Error!.Kind != BorgErrorKind.Cancelled)
                    ArchivesError = BorgTexts.Format(Strings.MountFailed, BorgTexts.Describe(result.Error));
                return;
            }

            _services.Durations.Record(key, MountProgress.Elapsed);
            var entry = new MountItemViewModel(repositoryId, item.Name, archive, mount, OpenMountAsync, UnmountAsync);
            Mounts.Add(entry);
            OnMountsChanged();
            _ = WatchMountAsync(entry);
            await _services.Dialogs.OpenFolderAsync(mount.OpenPath);
        }
        finally
        {
            MountProgress.Stop();
            IsMounting = false;
        }
    }

    /// <summary>
    /// In WSL, Explorer reaches a mount only if FUSE allows other users: offers to add <c>user_allow_other</c> to
    /// /etc/fuse.conf (as root in WSL). <c>false</c> if mounting is not possible (the reason is shown).
    /// </summary>
    private async Task<bool> EnsureMountSetupAsync(BorgInstallation borg)
    {
        var setup = await Task.Run(() => _services.BorgClient.CheckMountSetupAsync(borg));
        if (setup == MountSetup.Unsupported)
        {
            ArchivesError = BorgTexts.Format(Strings.MountFailed, Strings.BorgErrorMountUnavailable);
            return false;
        }
        if (setup == MountSetup.Ready)
            return true;

        if (!await _services.Dialogs.ConfirmAsync(Strings.MountSetupTitle,
                BorgTexts.Format(Strings.MountSetupMessage, BorgClient.AllowOtherCommand), Strings.MountSetupConfirm))
            return false;
        if (await Task.Run(() => _services.BorgClient.EnableAllowOtherAsync()))
            return true;

        // Then by hand: the command is on the clipboard.
        await _services.Dialogs.CopyToClipboardAsync(BorgClient.AllowOtherCommand);
        await _services.Dialogs.ShowMessageAsync(Strings.MountSetupTitle,
            BorgTexts.Format(Strings.MountSetupFailed, BorgClient.AllowOtherCommand));
        return false;
    }

    private Task OpenMountAsync(MountItemViewModel mount) => _services.Dialogs.OpenFolderAsync(mount.OpenPath);

    /// <summary>Unmounts one; if a program still uses it, says so and leaves it mounted.</summary>
    private async Task UnmountAsync(MountItemViewModel mount)
    {
        if (!await UnmountAsync(mount, force: false))
            await _services.Dialogs.ShowMessageAsync(Strings.MountUnmount, BorgTexts.Format(Strings.MountBusy, mount.Title));
    }

    private async Task<bool> UnmountAsync(MountItemViewModel mount, bool force)
    {
        if (mount.IsUnmounting)
            return false;
        mount.IsUnmounting = true;
        var unmounted = await Task.Run(() => mount.Mount.UnmountAsync(force));
        mount.IsUnmounting = false;
        if (unmounted)
            Forget(mount);
        return unmounted;
    }

    /// <summary>Unmounts everything (closing the app): what is still in use is detached anyway.</summary>
    public async Task UnmountAllAsync()
    {
        foreach (var mount in Mounts.ToList())
            await UnmountAsync(mount, force: true);
    }

    [RelayCommand]
    private async Task UnmountAllMountsAsync()
    {
        var busy = new List<string>();
        foreach (var mount in Mounts.ToList())
            if (!await UnmountAsync(mount, force: false))
                busy.Add(mount.Title);
        if (busy.Count > 0)
            await _services.Dialogs.ShowMessageAsync(Strings.MountUnmount, BorgTexts.Format(Strings.MountBusy, string.Join(", ", busy)));
    }

    /// <summary>
    /// Before borg writes to a repository (backup, clean-up, freeing space) or it is removed: its mounts hold the lock, so
    /// they have to go – after asking. <c>false</c> if they stay (not confirmed, or still in use).
    /// </summary>
    private async Task<bool> ReleaseMountsAsync(Guid repositoryId, string repositoryName)
    {
        var mounts = Mounts.Where(mount => mount.RepositoryId == repositoryId).ToList();
        if (mounts.Count == 0)
            return true;
        if (!await _services.Dialogs.ConfirmAsync(Strings.MountedBlocksTitle, BorgTexts.Format(Strings.MountedBlocksMessage, repositoryName),
                Strings.MountedBlocksConfirm))
            return false;

        foreach (var mount in mounts)
        {
            if (!await UnmountAsync(mount, force: false))
            {
                await _services.Dialogs.ShowMessageAsync(Strings.MountUnmount, BorgTexts.Format(Strings.MountBusy, mount.Title));
                return false;
            }
        }
        return true;
    }

    /// <summary>borg stopped on its own (e.g. the connection broke): the mount is gone.</summary>
    private async Task WatchMountAsync(MountItemViewModel mount)
    {
        var error = await mount.Mount.Ended;
        OnViewModelThread(() =>
        {
            if (!Mounts.Contains(mount) || mount.IsUnmounting)
                return;
            Notice = BorgTexts.Format(Strings.MountEnded, mount.Title,
                error is null ? Strings.MountEndedOutside : BorgTexts.Describe(error));
            Forget(mount);
        });
    }

    private void Forget(MountItemViewModel mount)
    {
        if (Mounts.Remove(mount))
            OnMountsChanged();
    }

    private void OnMountsChanged()
    {
        OnPropertyChanged(nameof(HasMounts));
        OnPropertyChanged(nameof(MountsText));
    }

    /// <summary>Mounts left behind by a crash (borg gone, folder broken) are detached when borg is found.</summary>
    private void CleanUpMounts(BorgInstallation borg)
    {
        if (Mounts.Count == 0 && borg.Support == BorgSupport.Supported)
            _ = Task.Run(() => _services.BorgClient.CleanUpMountsAsync(borg));
    }
}

/// <summary>A mounted archive (or repository) in the status bar.</summary>
public sealed partial class MountItemViewModel : ObservableObject
{
    private readonly Func<MountItemViewModel, Task> _open;
    private readonly Func<MountItemViewModel, Task> _unmount;

    public MountItemViewModel(Guid repositoryId, string repositoryName, string? archiveName, BorgMount mount,
        Func<MountItemViewModel, Task> open, Func<MountItemViewModel, Task> unmount)
    {
        RepositoryId = repositoryId;
        RepositoryName = repositoryName;
        ArchiveName = archiveName;
        Mount = mount;
        _open = open;
        _unmount = unmount;
    }

    public Guid RepositoryId { get; }

    public string RepositoryName { get; }

    /// <summary><c>null</c>: the whole repository.</summary>
    public string? ArchiveName { get; }

    public BorgMount Mount { get; }

    /// <summary>"dev_Test · Documents-host-2026-11-06" or "dev_Test · all archives".</summary>
    public string Title => ArchiveName is null
        ? BorgTexts.Format(Strings.MountAllTitle, RepositoryName)
        : $"{RepositoryName} · {ArchiveName}";

    /// <summary>Where the file manager opens it (\\wsl.localhost\… in WSL).</summary>
    public string OpenPath => Mount.OpenPath;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UnmountCommand))]
    public partial bool IsUnmounting { get; set; }

    [RelayCommand]
    private Task OpenAsync() => _open(this);

    [RelayCommand(CanExecute = nameof(CanUnmount))]
    private Task UnmountAsync() => _unmount(this);

    private bool CanUnmount() => !IsUnmounting;
}
