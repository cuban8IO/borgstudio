using System.Collections.ObjectModel;
using BorgStudio.Core.Borg;
using BorgStudio.Plugins;
using CommunityToolkit.Mvvm.Input;

namespace BorgStudio.App.ViewModels;

/// <summary>
/// Comparing archives from the "Archives" tab: two marked ones, or one with the archive of the same job before it.
/// borg compares them (<c>borg diff</c>) while the comparison window shows how far it is.
/// </summary>
public partial class MainViewModel
{
    /// <summary>The archives marked in the list (Ctrl/Shift); <see cref="SelectedArchive"/> is the one clicked last.</summary>
    public ObservableCollection<ArchiveItemViewModel> SelectedArchives { get; } = [];

    /// <summary>The comparison running for the open window (tests wait for it).</summary>
    internal Task DiffLoad { get; private set; } = Task.CompletedTask;

    private void OnSelectedArchivesChanged()
    {
        CompareArchivesCommand.NotifyCanExecuteChanged();
        CompareWithPreviousCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanCompareArchives))]
    private Task CompareArchivesAsync()
    {
        var pair = SelectedArchives.OrderBy(archive => archive.Archive.Start).ToList();
        return CompareAsync(pair[0].Archive, pair[1].Archive);
    }

    private bool CanCompareArchives() => SelectedArchives.Count == 2 && CanUseArchives();

    /// <summary>The marked archive and the one of its job made before it.</summary>
    [RelayCommand(CanExecute = nameof(CanCompareWithPrevious))]
    private Task CompareWithPreviousAsync()
    {
        var newer = SelectedArchive!;
        return CompareAsync(Previous(newer)!.Archive, newer.Archive);
    }

    private bool CanCompareWithPrevious() => SelectedArchive is { } archive && SelectedArchives.Count <= 1 && Previous(archive) is not null && CanUseArchives();

    /// <summary>The newest archive of the same job before <paramref name="archive"/>; <c>null</c> if there is none (or no job).</summary>
    internal ArchiveItemViewModel? Previous(ArchiveItemViewModel archive) => archive.JobName is not { } job ? null
        : Archives.Where(other => other.JobName == job && other.Archive.Start < archive.Archive.Start).MaxBy(other => other.Archive.Start);

    private bool CanUseArchives() =>
        !IsBackupRunning && !IsWorking && SelectedRepository?.Location is not null && Borg is { Support: BorgSupport.Supported };

    private async Task CompareAsync(BorgArchive older, BorgArchive newer)
    {
        var item = SelectedRepository!;
        if (!TryGetLocation(item, out var location))
            return;
        if (await GetArchiveAccessAsync(item, location) is not { } access)
            return;

        var diff = new ArchiveDiffViewModel(older, newer);
        using var cancellation = new CancellationTokenSource();
        diff.Cancel = cancellation.Cancel;
        DiffLoad = LoadDiffAsync(location, access.Ssh.Access, access.Passphrase.Value, diff, cancellation.Token);
        await _services.Dialogs.ShowArchiveDiffAsync(diff);
        // Closed while borg was still comparing: stop it.
        await cancellation.CancelAsync();
        await DiffLoad;
    }

    private async Task LoadDiffAsync(RepositoryLocation location, Core.Borg.SshAccess? ssh, string? passphrase, ArchiveDiffViewModel diff,
        CancellationToken cancellationToken)
    {
        var borg = Borg!;
        var progress = new ViewModelProgress<int>(this, diff.ReportRead);
        var result = await Task.Run(() => _services.BorgClient.DiffAsync(borg, location, diff.Older.Name, diff.Newer.Name, passphrase, ssh, progress,
            cancellationToken));
        if (result.Error?.Kind == BorgErrorKind.Cancelled)
            return;
        OnViewModelThread(() =>
        {
            if (result.Value is { } entries)
                diff.Show(entries);
            else
                diff.Fail(BorgTexts.Describe(result.Error!));
        });
    }
}
