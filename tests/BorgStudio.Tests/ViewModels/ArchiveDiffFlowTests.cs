using BorgStudio.App.Resources;
using BorgStudio.App.ViewModels;
using BorgStudio.Core.Backups;
using BorgStudio.Core.Borg;
using BorgStudio.Core.Repositories;
using BorgStudio.Tests.TestSupport;

namespace BorgStudio.Tests.ViewModels;

/// <summary>Comparing archives: borg's lines, the commands in the "Archives" tab and the comparison window – with fake borg.</summary>
public sealed class ArchiveDiffFlowTests : IDisposable
{
    // Lines as borg 1.4.4 prints them.
    private const string Modified = """{"changes": [{"added": 16, "removed": 2, "type": "modified"}, {"new_mtime": "2026-11-22T17:56:28.566350", "old_mtime": "2026-11-22T17:56:25.982350", "type": "mtime"}], "path": "home/me/a.txt"}""";
    private const string Added = """{"changes": [{"size": 4, "type": "added"}], "path": "home/me/sub/new.txt"}""";
    private const string AddedFolder = """{"changes": [{"type": "added directory"}], "path": "home/me/newdir"}""";
    private const string Removed = """{"changes": [{"size": 5, "type": "removed"}], "path": "home/me/gone.txt"}""";
    private const string ModeOnly = """{"changes": [{"new_mode": "-rw-------", "old_mode": "-rw-r--r--", "type": "mode"}, {"new_ctime": "2026-11-22T17:56:28.570350", "old_ctime": "2026-11-22T17:56:25.982350", "type": "ctime"}], "path": "home/me/sub/b.txt"}""";

    private readonly TestServices _test = new();
    private readonly RepositoryConfig _repository;

    public ArchiveDiffFlowTests()
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
        _test.Store.Save([_repository]);
        _test.Jobs.Save([Job("Documents"), Job("Photos")]);
        _test.Borg.Archives.Add(("Documents-host-2026-11-01", new DateTime(2026, 11, 1, 20, 0, 0)));
        _test.Borg.Archives.Add(("Photos-host-2026-11-01", new DateTime(2026, 11, 1, 21, 0, 0)));
        _test.Borg.Archives.Add(("Documents-host-2026-11-02", new DateTime(2026, 11, 2, 20, 0, 0)));
        _test.Borg.DiffLines.AddRange([Modified, Added, AddedFolder, Removed, ModeOnly]);
    }

    public void Dispose() => _test.Dispose();

    private BackupJob Job(string name) => new() { Id = Guid.NewGuid(), Name = name, RepositoryId = _repository.Id, Sources = ["/home/me"] };

    private IReadOnlyList<string>? DiffCall => _test.Borg.Calls.LastOrDefault(call => call.Arguments[0] == "diff").Arguments;

    private async Task<MainViewModel> StartAsync()
    {
        var main = new MainViewModel(_test.Services);
        await main.CheckBorgCommand.ExecuteAsync(null);
        main.ListedRepository = main.Repositories[0];
        main.SelectedDetailsTab = MainViewModel.ArchivesTab;
        await main.ArchivesLoad;
        return main;
    }

    private static ArchiveItemViewModel Archive(MainViewModel main, string name) => main.Archives.Single(archive => archive.Name == name);

    [Fact]
    public void Reads_borgs_lines()
    {
        var modified = ArchiveDiffEntry.Parse(Modified)!;
        Assert.Equal((DiffKind.Modified, 16L, 2L), (modified.Kind, modified.Added, modified.Removed));
        Assert.Equal(["mtime: 2026-11-22T17:56:25.982350 → 2026-11-22T17:56:28.566350"], modified.Details);
        Assert.Equal((DiffKind.Added, 4L), (ArchiveDiffEntry.Parse(Added)!.Kind, ArchiveDiffEntry.Parse(Added)!.Size!.Value));
        Assert.True(ArchiveDiffEntry.Parse(AddedFolder)!.IsDirectory);
        Assert.Equal(DiffKind.Removed, ArchiveDiffEntry.Parse(Removed)!.Kind);
        var mode = ArchiveDiffEntry.Parse(ModeOnly)!;
        Assert.Equal(DiffKind.Metadata, mode.Kind);
        Assert.Contains("mode: -rw-r--r-- → -rw-------", mode.Details);
        Assert.Null(ArchiveDiffEntry.Parse("not json"));
        Assert.Null(ArchiveDiffEntry.Parse("""{"type": "log_message"}"""));
    }

    [Fact]
    public async Task Any_two_archives_can_be_chosen_in_either_order_and_the_same_one_twice_is_refused()
    {
        var main = await StartAsync();
        _test.Dialogs.PickCompare = picker =>
        {
            Assert.Equal(main.Archives.Count, picker.Options.Count);
            picker.Older = picker.Options.First(option => option.Archive.Name == "Documents-host-2026-11-02");
            picker.Newer = picker.Older;
            Assert.False(picker.CanCompare);
            Assert.NotNull(picker.Problem);
            // Chosen the wrong way round: compared older first anyway.
            picker.Newer = picker.Options.First(option => option.Archive.Name == "Photos-host-2026-11-01");
            picker.SwapCommand.Execute(null);
            Assert.Equal("Photos-host-2026-11-01", picker.Older!.Archive.Name);
            picker.SwapCommand.Execute(null);
            return true;
        };

        await main.CompareArchivesCommand.ExecuteAsync(null);

        Assert.Equal($"{_test.Directory.Combine("Disk")}::Photos-host-2026-11-01", DiffCall[^2]);
        Assert.Equal("Documents-host-2026-11-02", DiffCall[^1]);

        // Cancelled: nothing is compared.
        _test.Dialogs.PickCompare = _ => false;
        var calls = _test.Borg.Calls.Count;
        await main.CompareArchivesCommand.ExecuteAsync(null);
        Assert.Equal(calls, _test.Borg.Calls.Count);
    }

    [Fact]
    public async Task Two_marked_archives_are_compared_older_first()
    {
        var main = await StartAsync();
        Assert.True(main.CompareArchivesCommand.CanExecute(null)); // any two, chosen in the window
        main.SelectedArchives.Add(Archive(main, "Documents-host-2026-11-02"));
        main.SelectedArchives.Add(Archive(main, "Photos-host-2026-11-01"));

        await main.CompareArchivesCommand.ExecuteAsync(null);

        // The window was preset with the two marked ones, older first.
        var picker = Assert.Single(_test.Dialogs.ComparePickers);
        Assert.Equal(("Photos-host-2026-11-01", "Documents-host-2026-11-02"), (picker.Older!.Archive.Name, picker.Newer!.Archive.Name));

        Assert.Equal(["diff", "--log-json", "--json-lines", $"{_test.Directory.Combine("Disk")}::Photos-host-2026-11-01", "Documents-host-2026-11-02"],
            DiffCall);
        var diff = Assert.Single(_test.Dialogs.Diffs);
        Assert.False(diff.IsLoading);
        Assert.Equal(BorgTexts.Format(Strings.DiffSummary, 2, 1, 1, 1), diff.SummaryText);
        // Metadata-only changes are hidden until asked for.
        Assert.Equal(["/home/me/a.txt", "/home/me/gone.txt", "/home/me/newdir", "/home/me/sub/new.txt"], diff.Items.Select(item => item.Path));
        diff.ShowMetadata = true;
        Assert.Equal(5, diff.Items.Count);
        diff.SelectedKind = diff.KindOptions.Single(option => option.Value == DiffKind.Added);
        Assert.Equal(["/home/me/newdir", "/home/me/sub/new.txt"], diff.Items.Select(item => item.Path));
        diff.SelectedKind = diff.KindOptions[0];
        diff.Search = "SUB";
        Assert.Equal(["/home/me/sub/b.txt", "/home/me/sub/new.txt"], diff.Items.Select(item => item.Path));
        Assert.Equal("+16 B / −2 B", new ArchiveDiffItemViewModel(ArchiveDiffEntry.Parse(Modified)!).SizeText);
    }

    [Fact]
    public async Task One_archive_is_compared_with_the_one_of_its_job_before_it()
    {
        var main = await StartAsync();
        var newest = Archive(main, "Documents-host-2026-11-02");
        main.SelectedArchive = newest;
        main.SelectedArchives.Add(newest);
        Assert.Equal("Documents-host-2026-11-01", main.Previous(newest)!.Name);

        await main.CompareWithPreviousCommand.ExecuteAsync(null);

        Assert.Equal($"{_test.Directory.Combine("Disk")}::Documents-host-2026-11-01", DiffCall![3]);
        Assert.Equal("Documents-host-2026-11-02", DiffCall[4]);

        // The first archive of a job has nothing before it.
        main.SelectedArchive = Archive(main, "Photos-host-2026-11-01");
        main.SelectedArchives.Clear();
        Assert.False(main.CompareWithPreviousCommand.CanExecute(null));
    }

    [Fact]
    public async Task Closing_the_window_stops_borg_and_errors_are_shown()
    {
        var main = await StartAsync();
        main.SelectedArchives.Add(Archive(main, "Documents-host-2026-11-01"));
        main.SelectedArchives.Add(Archive(main, "Documents-host-2026-11-02"));
        var waiting = new TaskCompletionSource();
        _test.Borg.BeforeDiff = async token =>
        {
            waiting.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
        };
        _test.Dialogs.Diff = async diff =>
        {
            await waiting.Task;
            diff.CloseCommand.Execute(null);
        };

        await main.CompareArchivesCommand.ExecuteAsync(null);
        Assert.True(_test.Dialogs.Diffs[0].IsLoading);

        _test.Borg.BeforeDiff = null;
        _test.Dialogs.Diff = null;
        _test.Borg.DiffError = "Archive.DoesNotExist";
        await main.CompareArchivesCommand.ExecuteAsync(null);
        Assert.True(_test.Dialogs.Diffs[1].HasError);
    }

    [Fact]
    public void Many_changes_are_cut_for_the_list_but_counted()
    {
        var diff = new ArchiveDiffViewModel(new BorgArchive("a", "1", DateTimeOffset.Now), new BorgArchive("b", "2", DateTimeOffset.Now));
        diff.Show([.. Enumerable.Range(0, ArchiveDiffViewModel.MaximumShown + 10)
            .Select(number => new ArchiveDiffEntry($"f{number:D5}", DiffKind.Added, false, 1, 0, 0, []))]);

        Assert.Equal(ArchiveDiffViewModel.MaximumShown, diff.Items.Count);
        Assert.True(diff.HasMoreText);
        Assert.False(diff.IsEmpty);
        var empty = new ArchiveDiffViewModel(new BorgArchive("a", "1", DateTimeOffset.Now), new BorgArchive("b", "2", DateTimeOffset.Now));
        empty.Show([]);
        Assert.True(empty.IsEmpty);
    }
}
