using System.Collections.ObjectModel;
using System.Globalization;
using BorgStudio.App.Resources;
using BorgStudio.Core.Borg;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BorgStudio.App.ViewModels;

/// <summary>
/// The "Archives" tab: all archives of the selected repository (fast, <c>borg list</c>), and duration and sizes of the
/// newest ones (<c>borg info --last N</c>, which takes longer), more on request.
/// </summary>
public partial class MainViewModel
{
    public const int OverviewTab = 0;
    public const int JobsTab = 1;
    public const int ArchivesTab = 2;

    /// <summary>How many archives get details at a time.</summary>
    public const int ArchiveDetailsPage = 20;

    /// <summary>The repository whose archives are shown (<c>null</c>: none loaded).</summary>
    private Guid? _archivesOf;

    private int _archiveDetailsLoaded;

    /// <summary>Login and passphrase used for the shown archives, so loading more details doesn't ask again.</summary>
    private (SshAccessHolder Ssh, PassphraseHolder Passphrase)? _archiveAccess;

    private Guid? _archiveAccessOf;

    public ObservableCollection<ArchiveItemViewModel> Archives { get; } = [];

    /// <summary>The selected tab of the repository details.</summary>
    [ObservableProperty]
    public partial int SelectedDetailsTab { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshArchivesCommand), nameof(LoadMoreArchiveDetailsCommand))]
    public partial bool IsLoadingArchives { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasArchivesStatus))]
    public partial string? ArchivesStatus { get; private set; }

    public bool HasArchivesStatus => ArchivesStatus is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasArchivesError))]
    public partial string? ArchivesError { get; private set; }

    public bool HasArchivesError => ArchivesError is not null;

    partial void OnSelectedDetailsTabChanged(int value) => _ = EnsureArchivesAsync();

    partial void OnIsBackupRunningChanged(bool value)
    {
        NotifyArchiveCommands();
        // borg waits for the repository lock, so the tab may have been opened during the backup without loading.
        if (!value)
            _ = EnsureArchivesAsync();
    }

    // Locking forgets the passphrase used for the shown archives; the next load asks the vault again.
    partial void OnIsVaultUnlockedChanged(bool value)
    {
        if (!value)
            _archiveAccess = null;
    }

    partial void OnIsWorkingChanged(bool value) => NotifyArchiveCommands();

    private void NotifyArchiveCommands()
    {
        RefreshArchivesCommand.NotifyCanExecuteChanged();
        LoadMoreArchiveDetailsCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Another repository was selected: its archives are loaded when the tab is (or gets) shown.</summary>
    private void ResetArchives()
    {
        Archives.Clear();
        _archivesOf = null;
        _archiveAccess = null;
        _archiveDetailsLoaded = 0;
        ArchivesStatus = null;
        ArchivesError = null;
        NotifyArchiveCommands();
        _ = EnsureArchivesAsync();
    }

    /// <summary>A backup added an archive: show it if that repository's archives are on screen.</summary>
    private void ArchivesChanged(Guid repositoryId)
    {
        if (_archivesOf != repositoryId)
            return;
        _archivesOf = null;
        _ = EnsureArchivesAsync();
    }

    /// <summary>The load started by opening the tab or selecting a repository (tests wait for it).</summary>
    internal Task ArchivesLoad { get; private set; } = Task.CompletedTask;

    private Task EnsureArchivesAsync()
    {
        if (SelectedDetailsTab == ArchivesTab && SelectedRepository is { } item && _archivesOf != item.Config.Id && CanLoadArchives())
            ArchivesLoad = LoadArchivesAsync(item, Math.Max(ArchiveDetailsPage, _archiveDetailsLoaded));
        return ArchivesLoad;
    }

    [RelayCommand(CanExecute = nameof(CanLoadArchives))]
    private Task RefreshArchivesAsync() => LoadArchivesAsync(SelectedRepository!, Math.Max(ArchiveDetailsPage, _archiveDetailsLoaded));

    // borg locks the repository during a backup; other borg calls wait.
    private bool CanLoadArchives() =>
        !IsLoadingArchives && !IsBackupRunning && !IsWorking && SelectedRepository?.Location is not null
        && Borg is { Support: BorgSupport.Supported };

    [RelayCommand(CanExecute = nameof(CanLoadMoreDetails))]
    private async Task LoadMoreArchiveDetailsAsync()
    {
        var item = SelectedRepository!;
        IsLoadingArchives = true;
        try
        {
            await LoadDetailsAsync(item, _archiveDetailsLoaded + ArchiveDetailsPage);
        }
        finally
        {
            IsLoadingArchives = false;
            if (SelectedRepository != item)
                _ = EnsureArchivesAsync();
        }
    }

    private bool CanLoadMoreDetails() => CanLoadArchives() && _archivesOf is not null && _archiveDetailsLoaded < Archives.Count;

    private async Task LoadArchivesAsync(RepositoryItemViewModel item, int details)
    {
        var repositoryId = item.Config.Id;
        ArchivesError = null;
        if (!TryGetLocation(item, out var location))
        {
            ArchivesError = item.ResultText;
            return;
        }

        IsLoadingArchives = true;
        ArchivesStatus = Strings.ArchivesLoading;
        try
        {
            if (_archiveAccess is null || _archiveAccessOf != repositoryId)
            {
                if (await GetSshAccessAsync(item, location) is not { } ssh)
                {
                    ArchivesError = item.ResultText;
                    ArchivesStatus = null;
                    return;
                }
                if (await GetPassphraseAsync(item) is not { } passphrase)
                {
                    ArchivesStatus = null;
                    return;
                }
                _archiveAccess = (ssh, passphrase);
                _archiveAccessOf = repositoryId;
            }

            var access = _archiveAccess.Value;
            var borg = Borg!;
            var listed = await Task.Run(() => _services.BorgClient.ListArchivesAsync(borg, location, access.Passphrase.Value, access.Ssh.Access));
            if (SelectedRepository?.Config.Id != repositoryId)
                return; // the user moved on
            if (!listed.Succeeded)
            {
                ArchivesError = BorgTexts.Describe(listed.Error!);
                ArchivesStatus = null;
                _archiveAccess = null;
                return;
            }

            Archives.Clear();
            var jobs = _jobs.Where(job => job.RepositoryId == repositoryId).Select(job => (job.Name, Prefix: ArchivePrefix(job.Name) + "-")).ToList();
            foreach (var archive in listed.Value!)
                Archives.Add(new ArchiveItemViewModel(archive,
                    jobs.Where(job => archive.Name.StartsWith(job.Prefix, StringComparison.Ordinal))
                        .OrderByDescending(job => job.Prefix.Length).Select(job => job.Name).FirstOrDefault()));
            _archivesOf = repositoryId;
            _archiveDetailsLoaded = 0;
            UpdateArchivesStatus();

            await LoadDetailsAsync(item, details);
        }
        finally
        {
            IsLoadingArchives = false;
            NotifyArchiveCommands();
            // Another repository was selected meanwhile: load its archives now. (Not after a failure: no retry loop.)
            if (SelectedRepository?.Config.Id != repositoryId)
                _ = EnsureArchivesAsync();
        }
    }

    /// <summary>Duration and sizes for the newest <paramref name="count"/> archives.</summary>
    private async Task LoadDetailsAsync(RepositoryItemViewModel item, int count)
    {
        count = Math.Min(count, Archives.Count);
        if (count == 0 || _archiveAccess is not { } access || !TryGetLocation(item, out var location))
        {
            UpdateArchivesStatus();
            return;
        }

        ArchivesStatus = BorgTexts.Format(Strings.ArchivesLoadingDetails, count);
        var borg = Borg!;
        var details = await Task.Run(() => _services.BorgClient.ArchiveDetailsAsync(borg, location, count, access.Passphrase.Value, access.Ssh.Access));
        if (SelectedRepository?.Config.Id != item.Config.Id)
            return;
        if (!details.Succeeded)
        {
            ArchivesError = BorgTexts.Describe(details.Error!);
            UpdateArchivesStatus();
            return;
        }

        var byId = details.Value!.ToDictionary(archive => archive.Id);
        foreach (var archive in Archives)
            if (byId.TryGetValue(archive.Archive.Id, out var detailed))
                archive.SetDetails(detailed);
        _archiveDetailsLoaded = Math.Max(_archiveDetailsLoaded, count);
        UpdateArchivesStatus();
    }

    private void UpdateArchivesStatus()
    {
        ArchivesStatus = Archives.Count == 0 ? Strings.ArchivesNone
            : _archiveDetailsLoaded >= Archives.Count ? BorgTexts.Format(Strings.ArchivesCount, Archives.Count)
            : BorgTexts.Format(Strings.ArchivesCountWithDetails, Archives.Count, _archiveDetailsLoaded);
        NotifyArchiveCommands();
    }
}

/// <summary>An archive in the "Archives" tab.</summary>
public sealed partial class ArchiveItemViewModel(BorgArchive archive, string? jobName) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DurationText), nameof(OriginalText), nameof(NewText), nameof(FilesText))]
    public partial BorgArchive Archive { get; private set; } = archive;

    public string Name => Archive.Name;

    /// <summary>The job the archive comes from (by its name), if it is one of this repository's jobs.</summary>
    public string JobText => jobName ?? "–";

    public string StartText => Archive.Start == DateTimeOffset.MinValue
        ? "–"
        : Archive.Start.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);

    public string DurationText => Archive.Duration is { } duration ? BorgTexts.Duration(duration) : "…";

    public string OriginalText => Archive.Stats is { } stats ? BorgTexts.Size(stats.OriginalSize) : "…";

    /// <summary>What only this archive takes up in the repository.</summary>
    public string NewText => Archive.Stats is { } stats ? BorgTexts.Size(stats.DeduplicatedSize) : "…";

    public string FilesText => Archive.Stats is { } stats ? stats.Files.ToString("N0", CultureInfo.CurrentCulture) : "…";

    public void SetDetails(BorgArchive detailed) => Archive = detailed;
}
