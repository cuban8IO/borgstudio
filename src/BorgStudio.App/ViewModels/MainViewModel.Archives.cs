using System.Collections.ObjectModel;
using System.Globalization;
using BorgStudio.App.Resources;
using BorgStudio.Core.Borg;
using BorgStudio.Plugins;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BorgStudio.App.ViewModels;

/// <summary>
/// The "Archives" tab. Opening it shows the archives cached from last time at once, then checks with borg in the
/// background: <c>borg list</c> for the archives themselves (quick), <c>borg info --last N</c> for duration and sizes
/// (slow: borg reads all metadata of an archive for its own share) – but only for archives not cached yet, which after a
/// backup is just the new one. Deleted archives change the own share of the others, so then the details are fetched again.
/// </summary>
public partial class MainViewModel
{
    public const int OverviewTab = 0;
    public const int JobsTab = 1;
    public const int ArchivesTab = 2;

    /// <summary>How many of the newest archives get details at a time.</summary>
    public const int ArchiveDetailsPage = 20;

    /// <summary>The repository whose archives are in <see cref="Archives"/> (from the cache or from borg).</summary>
    private Guid? _shownOf;

    /// <summary>The repository whose archives were checked with borg since it was selected.</summary>
    private Guid? _archivesOf;

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

    /// <summary>The load started by opening the tab, selecting a repository or a finished backup (tests wait for it).</summary>
    internal Task ArchivesLoad { get; private set; } = Task.CompletedTask;

    /// <summary>How many of the newest archives in a row have details.</summary>
    private int LeadingDetails => Archives.TakeWhile(archive => archive.HasDetails).Count();

    partial void OnSelectedDetailsTabChanged(int value) => _ = EnsureArchivesAsync();

    partial void OnIsBackupRunningChanged(bool value)
    {
        NotifyArchiveCommands();
        // borg waits for the repository lock, so the tab may have been opened during the backup without checking.
        if (!value)
            _ = EnsureArchivesAsync();
    }

    partial void OnIsWorkingChanged(bool value) => NotifyArchiveCommands();

    // Locking forgets the passphrase used for the shown archives; the next load asks the vault again.
    partial void OnIsVaultUnlockedChanged(bool value)
    {
        if (!value)
            _archiveAccess = null;
    }

    private void NotifyArchiveCommands()
    {
        RefreshArchivesCommand.NotifyCanExecuteChanged();
        LoadMoreArchiveDetailsCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Another repository was selected: its archives are shown when the tab is (or gets) opened.</summary>
    private void ResetArchives()
    {
        Archives.Clear();
        _shownOf = null;
        _archivesOf = null;
        _archiveAccess = null;
        ArchivesStatus = null;
        ArchivesError = null;
        NotifyArchiveCommands();
        _ = EnsureArchivesAsync();
    }

    /// <summary>A backup added an archive: check again (only the new archive needs details).</summary>
    private void ArchivesChanged(Guid repositoryId)
    {
        if (_archivesOf == repositoryId)
            _archivesOf = null;
        _ = EnsureArchivesAsync();
    }

    private Task EnsureArchivesAsync()
    {
        if (SelectedDetailsTab != ArchivesTab || SelectedRepository is not { } item)
            return ArchivesLoad;

        if (_shownOf != item.Config.Id)
            ShowCachedArchives(item);
        if (_archivesOf != item.Config.Id && CanLoadArchives())
            ArchivesLoad = SyncArchivesAsync(item, refresh: false);
        return ArchivesLoad;
    }

    private void ShowCachedArchives(RepositoryItemViewModel item)
    {
        Archives.Clear();
        foreach (var archive in _services.Archives.Load(item.Config.Id) ?? [])
            Archives.Add(NewArchiveItem(item, archive));
        _shownOf = item.Config.Id;
        ArchivesError = null;
        RestoreArchivesStatus();
    }

    /// <summary>Checks the archives with borg again and fetches the details of the newest ones anew.</summary>
    [RelayCommand(CanExecute = nameof(CanLoadArchives))]
    private Task RefreshArchivesAsync() => ArchivesLoad = SyncArchivesAsync(SelectedRepository!, refresh: true);

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
            await LoadDetailsAsync(item, LeadingDetails + ArchiveDetailsPage);
        }
        finally
        {
            IsLoadingArchives = false;
            if (SelectedRepository != item)
                _ = EnsureArchivesAsync();
        }
    }

    private bool CanLoadMoreDetails() =>
        CanLoadArchives() && _archivesOf is { } id && id == SelectedRepository!.Config.Id && LeadingDetails < Archives.Count;

    /// <param name="refresh">Fetch the details of the newest archives again, even if they are cached.</param>
    private async Task SyncArchivesAsync(RepositoryItemViewModel item, bool refresh)
    {
        var repositoryId = item.Config.Id;
        ArchivesError = null;
        if (!TryGetLocation(item, out var location))
        {
            ArchivesError = item.ResultText;
            return;
        }

        IsLoadingArchives = true;
        ArchivesStatus = Archives.Count > 0 ? BorgTexts.Format(Strings.ArchivesChecking, Archives.Count) : Strings.ArchivesLoading;
        try
        {
            if (await GetArchiveAccessAsync(item, location) is not { } access)
            {
                RestoreArchivesStatus();
                return;
            }

            var borg = Borg!;
            var listed = await Task.Run(() => _services.BorgClient.ListArchivesAsync(borg, location, access.Passphrase.Value, access.Ssh.Access));
            if (SelectedRepository?.Config.Id != repositoryId)
                return; // the user moved on
            if (!listed.Succeeded)
            {
                ArchivesError = BorgTexts.Describe(listed.Error!);
                _archiveAccess = null;
                RestoreArchivesStatus();
                return;
            }

            // Keep the details borg worked out before – unless archives were deleted: that changes the others' own share.
            var newest = Math.Max(ArchiveDetailsPage, LeadingDetails);
            var known = Archives.Where(archive => archive.HasDetails).ToDictionary(archive => archive.Archive.Id, archive => archive.Archive);
            var listedIds = listed.Value!.Select(archive => archive.Id).ToHashSet();
            if (Archives.Any(archive => !listedIds.Contains(archive.Archive.Id)))
                known.Clear();

            Archives.Clear();
            foreach (var (archive, index) in listed.Value!.Select((archive, index) => (archive, index)))
            {
                var details = known.GetValueOrDefault(archive.Id);
                // Refreshing fetches the newest again (their values stay visible meanwhile) and forgets the older ones.
                if (refresh && index >= newest)
                    details = null;
                Archives.Add(NewArchiveItem(item, details ?? archive));
            }
            _shownOf = _archivesOf = repositoryId;
            SaveArchiveCache(repositoryId);

            var top = Math.Min(newest, Archives.Count);
            var missing = refresh ? top : Archives.Take(top).Select((archive, index) => archive.HasDetails ? 0 : index + 1).DefaultIfEmpty().Max();
            if (missing > 0)
                await LoadDetailsAsync(item, missing);
            else
                UpdateArchivesStatus();
        }
        finally
        {
            IsLoadingArchives = false;
            NotifyArchiveCommands();
            // Another repository was selected meanwhile: show its archives now. (Not after a failure: no retry loop.)
            if (SelectedRepository?.Config.Id != repositoryId)
                _ = EnsureArchivesAsync();
        }
    }

    /// <summary>Login and passphrase for the archives of <paramref name="item"/>, asked once per selection.</summary>
    private async Task<(SshAccessHolder Ssh, PassphraseHolder Passphrase)?> GetArchiveAccessAsync(RepositoryItemViewModel item, RepositoryLocation location)
    {
        if (_archiveAccess is { } cached && _archiveAccessOf == item.Config.Id)
            return cached;

        if (await GetSshAccessAsync(item, location) is not { } ssh)
        {
            ArchivesError = item.ResultText;
            return null;
        }
        if (await GetPassphraseAsync(item) is not { } passphrase)
            return null;

        _archiveAccess = (ssh, passphrase);
        _archiveAccessOf = item.Config.Id;
        return _archiveAccess;
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
        SaveArchiveCache(item.Config.Id);
        UpdateArchivesStatus();
    }

    private void SaveArchiveCache(Guid repositoryId) =>
        _services.Archives.Save(repositoryId, [.. Archives.Select(archive => archive.Archive)]);

    private ArchiveItemViewModel NewArchiveItem(RepositoryItemViewModel item, BorgArchive archive)
    {
        // The job whose prefix fits best (the longest: "Docs-Photos-…" belongs to "Docs Photos", not "Docs").
        var jobName = _jobs.Where(job => job.RepositoryId == item.Config.Id)
            .Select(job => (job.Name, Prefix: ArchivePrefix(job.Name) + "-"))
            .Where(job => archive.Name.StartsWith(job.Prefix, StringComparison.Ordinal))
            .OrderByDescending(job => job.Prefix.Length)
            .Select(job => job.Name)
            .FirstOrDefault();
        return new ArchiveItemViewModel(archive, jobName);
    }

    /// <summary>After a failed or cancelled check: the archives shown (from the cache) stay, with their status.</summary>
    private void RestoreArchivesStatus()
    {
        if (Archives.Count > 0)
            UpdateArchivesStatus();
        else
            ArchivesStatus = null;
    }

    private void UpdateArchivesStatus()
    {
        var detailed = Archives.Count(archive => archive.HasDetails);
        ArchivesStatus = Archives.Count == 0 ? Strings.ArchivesNone
            : detailed >= Archives.Count ? BorgTexts.Format(Strings.ArchivesCount, Archives.Count)
            : BorgTexts.Format(Strings.ArchivesCountWithDetails, Archives.Count, detailed);
        NotifyArchiveCommands();
    }
}

/// <summary>An archive in the "Archives" tab.</summary>
public sealed partial class ArchiveItemViewModel(BorgArchive archive, string? jobName) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDetails), nameof(DurationText), nameof(OriginalText), nameof(NewText), nameof(FilesText))]
    public partial BorgArchive Archive { get; private set; } = archive;

    public string Name => Archive.Name;

    public bool HasDetails => Archive.Stats is not null;

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
