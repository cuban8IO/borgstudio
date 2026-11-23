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
        if (await ConnectAsync(item) is not { } location)
            return;
        if (await GetArchiveAccessAsync(item, location) is not { } access)
            return;
        var ssh = access.Ssh.Access;
        var passphrase = access.Passphrase.Value;
        var borg = Borg!;
        var repositoryId = item.Config.Id;
        if (NewExport(location, ssh, passphrase, archive, [],
                cancellationToken => WholeArchiveDepthAsync(borg, repositoryId, location, archive, passphrase, ssh, cancellationToken),
                BorgTexts.Format(Strings.ArchiveExportWhatArchive, archive.Name), await PickExportTargetAsync(_services.Dialogs, archive.Name)) is { } export)
            await _services.Dialogs.ShowArchiveExportAsync(export);
    }

    /// <summary>
    /// How many leading folders a whole archive's export leaves out: the ones above what was backed up, so a backup of
    /// C:\Users\me\Documents starts with "Documents", not "mnt/c/Users/me/Documents". From the archive's contents when they
    /// are cached, else from a <c>borg list</c> first (only the names, quick next to the export itself).
    /// </summary>
    private async Task<BorgResult<int>> WholeArchiveDepthAsync(BorgInstallation borg, Guid repositoryId, RepositoryLocation location,
        BorgArchive archive, string? passphrase, SshAccess? ssh, CancellationToken cancellationToken)
    {
        if (_services.Archives.LoadContents(repositoryId, archive.Id) is { } tree)
            return BorgResult<int>.Success(SharedFolderDepth(tree));

        var shared = new SharedFolders();
        var listed = await _services.BorgClient.ListItemsAsync(borg, location, archive.Name, passphrase, entry => shared.Add(entry.Path), ssh,
            cancellationToken);
        return listed.Succeeded ? BorgResult<int>.Success(shared.Depth) : BorgResult<int>.Failure(listed.Error!);
    }

    /// <summary>The folders above everything in the archive that were not backed up themselves, as a number of borg path segments.</summary>
    internal static int SharedFolderDepth(ArchiveTree tree)
    {
        var node = tree.Root;
        while (node.Children is [{ IsDirectory: true, IsImplicit: true } only])
            node = only;
        return node == tree.Root ? 0 : node.BorgPath.Split('/').Length;
    }

    /// <summary><see cref="SharedFolderDepth(IReadOnlyList{string})"/> for paths coming one by one (millions of them).</summary>
    internal sealed class SharedFolders
    {
        private string[]? _shared;

        public int Depth { get; private set; }

        public void Add(string path)
        {
            var parent = path.Trim('/').Split('/')[..^1];
            if (_shared is null)
            {
                (_shared, Depth) = (parent, parent.Length);
                return;
            }
            Depth = Math.Min(Depth, parent.Length);
            for (var index = 0; index < Depth; index++)
                if (parent[index] != _shared[index])
                {
                    Depth = index;
                    break;
                }
        }
    }

    /// <summary>Asks where to export to; the file type chosen tells the format (zip if none fits).</summary>
    internal static async Task<string?> PickExportTargetAsync(Services.IDialogService dialogs, string suggestedName)
    {
        var file = await dialogs.PickArchiveExportFileAsync(Strings.ArchiveExportPickTitle, suggestedName);
        return file is null ? null : ExportFormats.FromFileName(file) is null ? file + ExportFormat.Zip.Extension() : file;
    }

    /// <summary>
    /// The export of <paramref name="paths"/> of the archive (all if none) into <paramref name="targetFile"/>, leaving out
    /// as many leading folders as <paramref name="stripComponents"/> tells once it starts.
    /// </summary>
    private ArchiveExportViewModel? NewExport(RepositoryLocation location, SshAccess? ssh, string? passphrase, BorgArchive archive,
        IReadOnlyList<string> paths, Func<CancellationToken, Task<BorgResult<int>>> stripComponents, string what, string? targetFile)
    {
        if (targetFile is null)
            return null;
        var borg = Borg!;
        var format = ExportFormats.FromFileName(targetFile) ?? ExportFormat.Zip;
        return new ArchiveExportViewModel(what, targetFile, format, async (progress, cancellationToken) =>
        {
            var strip = await stripComponents(cancellationToken);
            if (!strip.Succeeded)
                return BorgResult<BorgExportResult>.Failure(strip.Error!);
            return await _services.BorgClient.ExportAsync(borg, location, archive.Name, paths, strip.Value, targetFile, format, passphrase, ssh,
                progress, cancellationToken);
        });
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
        if (NewExport(location, ssh, passphrase, archive, paths, _ => Task.FromResult(BorgResult<int>.Success(shared)), what,
                await PickExportTargetAsync(dialogs, suggested)) is { } export)
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
