using BorgStudio.App.Resources;
using BorgStudio.Core.Borg;
using BorgStudio.Plugins;
using CommunityToolkit.Mvvm.Input;

namespace BorgStudio.App.ViewModels;

/// <summary>
/// Exporting an archive into one file (zip, tar, tar.gz, tar.zst) – a whole one from the "Archives" tab, or what is
/// selected in the archive browser.
/// </summary>
public partial class MainViewModel
{
    [RelayCommand(CanExecute = nameof(CanChangeOneArchive))]
    private async Task ExportArchiveAsync()
    {
        var item = SelectedRepository!;
        var archive = TargetArchives[0].Archive;
        if (!TryGetLocation(item, out var location))
            return;
        if (await GetArchiveAccessAsync(item, location) is not { } access)
            return;
        if (NewExport(location, access.Ssh.Access, access.Passphrase.Value, archive, [], 0,
                BorgTexts.Format(Strings.ArchiveExportWhatArchive, archive.Name), await PickExportTargetAsync(_services.Dialogs, archive.Name)) is { } export)
            await _services.Dialogs.ShowArchiveExportAsync(export);
    }

    /// <summary>Asks where to export to; the file type chosen tells the format (zip if none fits).</summary>
    internal static async Task<string?> PickExportTargetAsync(Services.IDialogService dialogs, string suggestedName)
    {
        var file = await dialogs.PickArchiveExportFileAsync(Strings.ArchiveExportPickTitle, suggestedName);
        return file is null ? null : ExportFormats.FromFileName(file) is null ? file + ExportFormat.Zip.Extension() : file;
    }

    /// <summary>The export of <paramref name="paths"/> of the archive (all if none) into <paramref name="targetFile"/>.</summary>
    private ArchiveExportViewModel? NewExport(RepositoryLocation location, SshAccess? ssh, string? passphrase, BorgArchive archive,
        IReadOnlyList<string> paths, int stripComponents, string what, string? targetFile)
    {
        if (targetFile is null)
            return null;
        var borg = Borg!;
        var format = ExportFormats.FromFileName(targetFile) ?? ExportFormat.Zip;
        return new ArchiveExportViewModel(what, targetFile, format, (progress, cancellationToken) =>
            _services.BorgClient.ExportAsync(borg, location, archive.Name, paths, stripComponents, targetFile, format, passphrase, ssh, progress,
                cancellationToken));
    }

    /// <summary>
    /// For the browser: exports the selected files and folders, starting at the folder they share (so "Documents" is the
    /// top of the file, not "/home/me/Documents").
    /// </summary>
    private Func<IReadOnlyList<ArchiveNode>, Task>? ExportFactory(RepositoryLocation location, SshAccess? ssh, string? passphrase, BorgArchive archive,
        Services.IDialogService dialogs) => async items =>
    {
        var paths = items.Select(node => node.BorgPath).ToList();
        var shared = SharedFolderDepth(paths);
        var suggested = items.Count == 1 ? items[0].Name : archive.Name;
        var what = items.Count == 1
            ? BorgTexts.Format(Strings.ArchiveExportWhatOne, ArchiveTree.DisplayPathOf(paths[0]), archive.Name)
            : BorgTexts.Format(Strings.ArchiveExportWhatSome, items.Count, archive.Name);
        if (NewExport(location, ssh, passphrase, archive, paths, shared, what, await PickExportTargetAsync(dialogs, suggested)) is { } export)
            await dialogs.ShowArchiveExportAsync(export);
    };

    /// <summary>How many leading path segments all paths share as folders: "home/me/a", "home/me/b/c" → 2.</summary>
    internal static int SharedFolderDepth(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
            return 0;
        var parents = paths.Select(path => path.Split('/')[..^1]).ToList();
        var depth = 0;
        while (parents.All(parent => parent.Length > depth) && parents.All(parent => parent[depth] == parents[0][depth]))
            depth++;
        return depth;
    }
}
