using System.IO.Compression;
using BorgStudio.App.Resources;
using BorgStudio.App.ViewModels;
using BorgStudio.Core.Backups;
using BorgStudio.Core.Borg;
using BorgStudio.Core.Repositories;
using BorgStudio.Tests.TestSupport;

namespace BorgStudio.Tests.ViewModels;

/// <summary>Exporting archives into one file – zip, tar, tar.gz, tar.zst – with fake borg.</summary>
public sealed class ArchiveExportFlowTests : IDisposable
{
    private readonly TestServices _test = new();
    private readonly RepositoryConfig _repository;

    public ArchiveExportFlowTests()
    {
        _repository = new RepositoryConfig
        {
            Id = Guid.NewGuid(),
            Name = "Disk",
            ProviderId = "local",
            ProviderValues = new Dictionary<string, string> { ["path"] = _test.Directory.Combine("Disk") },
            PassphraseMode = PassphraseMode.None,
            EncryptionMode = "none",
        };
        Directory.CreateDirectory(_test.Directory.Combine("Disk"));
        Directory.CreateDirectory(Exports);
        _test.Store.Save([_repository]);
        _test.Jobs.Save([new BackupJob { Id = Guid.NewGuid(), Name = "Documents", RepositoryId = _repository.Id, Sources = ["/home/me"] }]);
        _test.Borg.Archives.Add(("Documents-host-2026-11-01", new DateTime(2026, 11, 1, 20, 0, 0)));
    }

    public void Dispose() => _test.Dispose();

    private string Exports => _test.Directory.Combine("exports");

    private FakeDialogs Dialogs => _test.Dialogs;

    private IReadOnlyList<string> ExportCall => _test.Borg.Calls.Last(call => call.Arguments[0] == "export-tar").Arguments;

    private async Task<MainViewModel> StartAsync()
    {
        var main = new MainViewModel(_test.Services);
        await main.CheckBorgCommand.ExecuteAsync(null);
        main.ListedRepository = main.Repositories[0];
        main.SelectedDetailsTab = MainViewModel.ArchivesTab;
        await main.ArchivesLoad;
        main.SelectedArchive = main.Archives[0];
        main.SelectedArchives.Add(main.Archives[0]);
        return main;
    }

    [Fact]
    public async Task A_whole_archive_becomes_a_zip_without_its_links()
    {
        var main = await StartAsync();
        Dialogs.ExportFile = Path.Combine(Exports, "backup"); // no extension: zip

        await main.ExportArchiveCommand.ExecuteAsync(null);

        var export = Assert.Single(Dialogs.Exports);
        Assert.True(export.Succeeded, export.ResultText);
        Assert.Equal(Path.Combine(Exports, "backup.zip"), export.TargetFile);
        // The folders above what was backed up ("home/me") are left out – found with a borg list first.
        Assert.Equal(["export-tar", "--log-json", "--progress", "--strip-components", "2",
            $"{_test.Directory.Combine("Disk")}::Documents-host-2026-11-01", "-"], ExportCall);
        Assert.Contains(_test.Borg.Calls, call => call.Arguments[0] == "list" && call.Arguments.Contains("--json-lines"));
        using var zip = ZipFile.OpenRead(export.TargetFile);
        Assert.Equal(["Documents/", "Documents/Invoices/", "Documents/Invoices/invoice-2026.pdf", "Documents/letter.txt"],
            zip.Entries.Select(entry => entry.FullName).Order(StringComparer.Ordinal));
        Assert.Equal(50_000, zip.GetEntry("Documents/Invoices/invoice-2026.pdf")!.Length);
        Assert.Contains(BorgTexts.Format(Strings.ArchiveExportSkippedLinks, "1"), export.ResultText);
        Assert.Equal("Documents-host-2026-11-01", Dialogs.TextInitials[^1]);
    }

    [Fact]
    public async Task Tar_and_tar_gz_are_written_from_borgs_stream()
    {
        var main = await StartAsync();
        Dialogs.ExportFile = Path.Combine(Exports, "backup.tar.gz");
        await main.ExportArchiveCommand.ExecuteAsync(null);
        await using (var gzip = new GZipStream(File.OpenRead(Dialogs.ExportFile), CompressionMode.Decompress))
        {
            var reader = new System.Formats.Tar.TarReader(gzip);
            var names = new List<string>();
            while (await reader.GetNextEntryAsync() is { } entry)
                names.Add(entry.Name);
            Assert.Contains("Documents/letter.txt", names);
            Assert.Contains("Documents/latest", names); // tar keeps links
        }

        Dialogs.ExportFile = Path.Combine(Exports, "backup.tar");
        await main.ExportArchiveCommand.ExecuteAsync(null);
        Assert.True(Dialogs.Exports[^1].Succeeded);
        Assert.True(new FileInfo(Dialogs.ExportFile).Length > 51_200);
    }

    [Fact]
    public async Task Tar_zst_is_written_by_borg_and_a_missing_zstd_is_told()
    {
        var main = await StartAsync();
        Dialogs.ExportFile = Path.Combine(Exports, "backup.tar.zst");

        await main.ExportArchiveCommand.ExecuteAsync(null);

        Assert.True(Dialogs.Exports[^1].Succeeded);
        Assert.Equal(Dialogs.ExportFile, ExportCall[^1]);
        Assert.True(File.Exists(Dialogs.ExportFile));

        _test.Borg.ExportError = "filter zstd: process creation failed";
        await main.ExportArchiveCommand.ExecuteAsync(null);
        Assert.True(Dialogs.Exports[^1].IsError);
        Assert.Equal(Strings.ArchiveExportZstdMissing, Dialogs.Exports[^1].ResultText);
        Assert.False(File.Exists(Dialogs.ExportFile));
    }

    [Fact]
    public async Task A_cancelled_export_leaves_no_file()
    {
        var main = await StartAsync();
        Dialogs.ExportFile = Path.Combine(Exports, "backup.zip");
        _test.Borg.DuringExport = () => Dialogs.Exports[^1].CancelCommand.Execute(null);

        await main.ExportArchiveCommand.ExecuteAsync(null);

        Assert.Equal(Strings.ArchiveExportCancelled, Dialogs.Exports[^1].ResultText);
        Assert.False(File.Exists(Dialogs.ExportFile));
    }

    [Fact]
    public async Task Cancelling_the_file_choice_exports_nothing()
    {
        var main = await StartAsync();
        Dialogs.ExportFile = null;

        await main.ExportArchiveCommand.ExecuteAsync(null);

        Assert.Empty(Dialogs.Exports);
        Assert.DoesNotContain(_test.Borg.Calls, call => call.Arguments[0] == "export-tar");
    }

    [Fact]
    public async Task The_browser_exports_its_selection_from_the_folder_they_share()
    {
        var main = await StartAsync();
        ArchiveBrowserViewModel? browser = null;
        Dialogs.Browser = async opened =>
        {
            browser = opened;
            await main.BrowserLoad;
            opened.SetSelected(opened.Tree!.Find("home/me/Documents/Invoices")!, true);
            opened.SetSelected(opened.Tree!.Find("home/me/Documents/letter.txt")!, true);
            Dialogs.ExportFile = Path.Combine(Exports, "part.zip");
            await opened.ExportSelectionCommand.ExecuteAsync(null);
        };

        await main.OpenArchiveCommand.ExecuteAsync(null);

        Assert.Equal(["--strip-components", "3"], ExportCall.SkipWhile(argument => argument != "--strip-components").Take(2));
        Assert.Contains("pp:home/me/Documents/Invoices", ExportCall);
        Assert.Contains("pp:home/me/Documents/letter.txt", ExportCall);
        using var zip = ZipFile.OpenRead(Path.Combine(Exports, "part.zip"));
        Assert.Equal(["Invoices/", "Invoices/invoice-2026.pdf", "letter.txt"], zip.Entries.Select(entry => entry.FullName).Order(StringComparer.Ordinal));
        Assert.Equal(BorgTexts.Format(Strings.ArchiveExportWhatSome, 2, "Documents-host-2026-11-01"), Dialogs.Exports[^1].What);
    }

    [Fact]
    public void Formats_and_shared_folders()
    {
        Assert.Equal(ExportFormat.TarGz, ExportFormats.FromFileName(@"C:\x\Backup.TGZ"));
        Assert.Equal(ExportFormat.TarZst, ExportFormats.FromFileName("a.tar.zst"));
        Assert.Equal(ExportFormat.Tar, ExportFormats.FromFileName("a.tar"));
        Assert.Equal(ExportFormat.Zip, ExportFormats.FromFileName("a.zip"));
        Assert.Null(ExportFormats.FromFileName("a.7z"));
        Assert.Equal(2, MainViewModel.SharedFolderDepth(["home/me/a", "home/me/b/c"]));
        Assert.Equal(0, MainViewModel.SharedFolderDepth(["home/a", "srv/b"]));
        Assert.Equal(3, MainViewModel.SharedFolderDepth(["home/me/Documents/letter.txt"]));
    }

    [Theory]
    [InlineData(new[] { "mnt/c/Users/me/Documents", "mnt/c/Users/me/Documents/a.txt", "mnt/c/Users/me/Documents/x/b.txt" }, 4)]
    [InlineData(new[] { "mnt/c/Users/me/Documents/a.txt", "mnt/c/Users/me/Pictures/b.jpg" }, 4)]
    [InlineData(new[] { "home/me/a.txt", "srv/data/b.txt" }, 0)]
    [InlineData(new[] { "a.txt" }, 0)]
    [InlineData(new[] { "home/me", "home/me/a.txt" }, 1)]
    public void A_whole_archive_leaves_out_the_folders_above_what_was_backed_up(string[] paths, int expected)
    {
        var shared = new MainViewModel.SharedFolders();
        var tree = new ArchiveTree();
        foreach (var path in paths)
        {
            shared.Add(path);
            tree.Add(new BorgItem(path, path.Contains('.') ? ArchiveEntryKind.File : ArchiveEntryKind.Directory, 0, DateTimeOffset.UnixEpoch));
        }

        Assert.Equal(expected, shared.Depth);
        Assert.Equal(expected, MainViewModel.SharedFolderDepth(tree.Complete()));
    }

    [Fact]
    public async Task A_whole_archive_whose_contents_are_cached_needs_no_extra_borg_list()
    {
        var main = await StartAsync();
        var archive = main.Archives[0].Archive;
        var tree = new ArchiveTree();
        tree.Add(new BorgItem("home/me/Documents", ArchiveEntryKind.Directory, 0, DateTimeOffset.UnixEpoch));
        tree.Add(new BorgItem("home/me/Documents/letter.txt", ArchiveEntryKind.File, 10, DateTimeOffset.UnixEpoch));
        _test.Archives.SaveContents(_repository.Id, archive.Id, tree.Complete());
        var lists = _test.Borg.Calls.Count(call => call.Arguments[0] == "list" && call.Arguments.Contains("--json-lines"));
        Dialogs.ExportFile = Path.Combine(Exports, "backup.tar");

        await main.ExportArchiveCommand.ExecuteAsync(null);

        Assert.True(Dialogs.Exports[^1].Succeeded);
        Assert.Equal(["--strip-components", "2"], ExportCall.SkipWhile(argument => argument != "--strip-components").Take(2));
        Assert.Equal(lists, _test.Borg.Calls.Count(call => call.Arguments[0] == "list" && call.Arguments.Contains("--json-lines")));
    }
}
