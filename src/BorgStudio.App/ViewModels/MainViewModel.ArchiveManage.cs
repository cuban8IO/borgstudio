using BorgStudio.App.Resources;
using BorgStudio.Core.Backups;
using BorgStudio.Core.Borg;
using BorgStudio.Plugins;
using CommunityToolkit.Mvvm.Input;

namespace BorgStudio.App.ViewModels;

/// <summary>
/// Looking after archives in the "Archives" tab: renaming, keeping for good (out of the clean-up's reach, by the name's
/// "keep-" prefix), commenting and deleting – each followed by checking the list again.
/// </summary>
public partial class MainViewModel
{
    /// <summary>The archives a command works on: the marked ones, or the one clicked.</summary>
    private List<ArchiveItemViewModel> TargetArchives =>
        SelectedArchives.Count > 0 ? [.. SelectedArchives] : SelectedArchive is { } archive ? [archive] : [];

    private bool CanChangeArchives() => CanUseArchives() && !IsLoadingArchives && TargetArchives.Count > 0;

    private bool CanChangeOneArchive() => CanChangeArchives() && TargetArchives.Count == 1;

    private void NotifyArchiveChangeCommands()
    {
        RenameArchiveCommand.NotifyCanExecuteChanged();
        CommentArchiveCommand.NotifyCanExecuteChanged();
        KeepArchivesCommand.NotifyCanExecuteChanged();
        ReleaseArchivesCommand.NotifyCanExecuteChanged();
        DeleteArchivesCommand.NotifyCanExecuteChanged();
        ExportArchiveCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanChangeOneArchive))]
    private async Task RenameArchiveAsync()
    {
        var archive = TargetArchives[0];
        var name = await _services.Dialogs.AskTextAsync(Strings.ArchiveRenameTitle, BorgTexts.Format(Strings.ArchiveRenamePrompt, archive.Name), archive.Name);
        if (name is null || (name = name.Trim()) == archive.Name)
            return;
        if (ArchiveNameProblem(name) is { } problem)
        {
            ArchivesError = problem;
            return;
        }
        await ChangeArchivesAsync(Strings.ArchiveRenaming, (borg, location, passphrase, ssh) =>
            _services.BorgClient.RenameArchiveAsync(borg, location, archive.Name, name, passphrase, ssh));
    }

    /// <summary>Why <paramref name="name"/> cannot be an archive's name; <c>null</c> if it can.</summary>
    private string? ArchiveNameProblem(string name) =>
        name.Length == 0 ? Strings.ArchiveNameEmpty
        : name.Contains('/', StringComparison.Ordinal) || name.Contains("::", StringComparison.Ordinal) ? Strings.ArchiveNameInvalid
        : Archives.Any(other => other.Name == name) ? BorgTexts.Format(Strings.ArchiveNameExists, name)
        : null;

    [RelayCommand(CanExecute = nameof(CanChangeOneArchive))]
    private async Task CommentArchiveAsync()
    {
        var archive = TargetArchives[0];
        var comment = await _services.Dialogs.AskTextAsync(Strings.ArchiveCommentTitle, BorgTexts.Format(Strings.ArchiveCommentPrompt, archive.Name),
            archive.Archive.Comment ?? "");
        if (comment is null || (comment = comment.Trim()) == (archive.Archive.Comment ?? ""))
            return;
        await ChangeArchivesAsync(Strings.ArchiveCommenting, (borg, location, passphrase, ssh) =>
            _services.BorgClient.SetArchiveCommentAsync(borg, location, archive.Name, comment, passphrase, ssh));
    }

    /// <summary>Keeps the archives for good: "keep-" in front of their name, so their job's clean-up no longer sees them.</summary>
    [RelayCommand(CanExecute = nameof(CanKeepArchives))]
    private Task KeepArchivesAsync() => RenameAllAsync(TargetArchives.Where(archive => !archive.IsKept), BackupNames.Kept, Strings.ArchiveKeeping);

    private bool CanKeepArchives() => CanChangeArchives() && TargetArchives.Any(archive => !archive.IsKept);

    /// <summary>Gives kept archives their name back: their job's clean-up decides about them again.</summary>
    [RelayCommand(CanExecute = nameof(CanReleaseArchives))]
    private Task ReleaseArchivesAsync() => RenameAllAsync(TargetArchives.Where(archive => archive.IsKept), BackupNames.Unkept, Strings.ArchiveReleasing);

    private bool CanReleaseArchives() => CanChangeArchives() && TargetArchives.Any(archive => archive.IsKept);

    private Task RenameAllAsync(IEnumerable<ArchiveItemViewModel> archives, Func<string, string> rename, string workingText)
    {
        var names = archives.Select(archive => (Old: archive.Name, New: rename(archive.Name))).ToList();
        if (names.FirstOrDefault(pair => Archives.Any(other => other.Name == pair.New)) is { Old: not null } taken)
        {
            ArchivesError = BorgTexts.Format(Strings.ArchiveNameExists, taken.New);
            return Task.CompletedTask;
        }
        return ChangeArchivesAsync(workingText, async (borg, location, passphrase, ssh) =>
        {
            foreach (var (old, @new) in names)
            {
                var renamed = await _services.BorgClient.RenameArchiveAsync(borg, location, old, @new, passphrase, ssh);
                if (!renamed.Succeeded)
                    return renamed;
            }
            return BorgResult<bool>.Success(true);
        });
    }

    /// <summary>Deletes the archives for good (after asking) and offers to free their space right away.</summary>
    [RelayCommand(CanExecute = nameof(CanChangeArchives))]
    private async Task DeleteArchivesAsync()
    {
        var archives = TargetArchives;
        var names = archives.Select(archive => archive.Name).ToList();
        var shown = string.Join("\n", names.Take(10).Select(name => "• " + name))
            + (names.Count > 10 ? "\n" + BorgTexts.Format(Strings.ArchiveDeleteMore, names.Count - 10) : "");
        if (!await _services.Dialogs.ConfirmAsync(Strings.ArchiveDeleteTitle, BorgTexts.Format(Strings.ArchiveDeleteMessage, names.Count, shown),
                Strings.Delete))
            return;
        var deleted = await ChangeArchivesAsync(Strings.ArchiveDeleting, (borg, location, passphrase, ssh) =>
            _services.BorgClient.DeleteArchivesAsync(borg, location, names, passphrase, ssh));
        if (!deleted || !await _services.Dialogs.ConfirmAsync(Strings.ArchiveCompactTitle, Strings.ArchiveCompactMessage, Strings.FreeSpace, Strings.Later))
            return;
        await ChangeArchivesAsync(Strings.CleanupCompacting, async (borg, location, _, ssh) =>
        {
            var compacted = await _services.BorgClient.CompactAsync(borg, location, ssh, throttle: Throttle(SelectedRepository!.Config, location));
            if (compacted.Value is { } result)
                OnViewModelThread(() => ArchivesStatus = result.FreedBytes is { } freed
                    ? BorgTexts.Format(Strings.SpaceFreed, BorgTexts.Size(freed))
                    : Strings.SpaceFreedNoSize);
            return compacted.Succeeded ? BorgResult<bool>.Success(true) : BorgResult<bool>.Failure(compacted.Error!);
        }, reload: false);
    }

    /// <summary>
    /// Runs a change of the repository's archives: mounts let go first, nothing else of BorgStudio working on it, then the
    /// list is checked again. <c>true</c> if it worked.
    /// </summary>
    private async Task<bool> ChangeArchivesAsync(string workingText,
        Func<BorgInstallation, RepositoryLocation, string?, SshAccess?, Task<BorgResult<bool>>> change, bool reload = true)
    {
        var item = SelectedRepository!;
        if (await ConnectAsync(item) is not { } location)
            return false;
        if (!await ReleaseMountsAsync(item.Config.Id, item.Name))
            return false;
        if (await GetArchiveAccessAsync(item, location) is not { } access)
            return false;

        ArchivesError = null;
        BorgResult<bool> result;
        IsLoadingArchives = true;
        ArchivesStatus = workingText;
        try
        {
            using var repositoryLock = RepositoryLock.TryAcquire(Runner.LockDirectory, item.Config.Id);
            if (repositoryLock is null)
            {
                ArchivesError = Strings.CheckBusy;
                return false;
            }
            var borg = Borg!;
            result = await Task.Run(() => change(borg, location, access.Passphrase.Value, access.Ssh.Access));
        }
        finally
        {
            IsLoadingArchives = false;
        }

        NoteLock(item, result.Error);
        if (reload)
        {
            // The list as borg has it now (names, comments, the archives' own share after deleting).
            SelectedArchives.Clear();
            ArchivesLoad = SyncArchivesAsync(item, refresh: false);
            await ArchivesLoad;
            RefreshStatistics();
        }
        // After checking the list again (which starts without an error).
        if (!result.Succeeded)
            ArchivesError = BorgTexts.Describe(result.Error!);
        return result.Succeeded;
    }
}
