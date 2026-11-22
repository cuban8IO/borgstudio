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
        Assert.Equal(["export-tar", "--log-json", "--progress", $"{_test.Directory.Combine("Disk")}::Documents-host-2026-11-01", "-"], ExportCall);
        using var zip = ZipFile.OpenRead(export.TargetFile);
        Assert.Equal(["home/me/Documents/", "home/me/Documents/Invoices/", "home/me/Documents/Invoices/invoice-2026.pdf", "home/me/Documents/letter.txt"],
            zip.Entries.Select(entry => entry.FullName).Order(StringComparer.Ordinal));
        Assert.Equal(50_000, zip.GetEntry("home/me/Documents/Invoices/invoice-2026.pdf")!.Length);
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
            Assert.Contains("home/me/Documents/letter.txt", names);
            Assert.Contains("home/me/Documents/latest", names); // tar keeps links
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
}
