using BorgStudio.App.Resources;
using BorgStudio.App.ViewModels;
using BorgStudio.Core.Backups;
using BorgStudio.Core.Repositories;
using BorgStudio.Tests.TestSupport;

namespace BorgStudio.Tests.ViewModels;

/// <summary>Renaming, keeping, commenting and deleting archives in the "Archives" tab – with fake borg.</summary>
public sealed class ArchiveManageFlowTests : IDisposable
{
    private readonly TestServices _test = new();
    private readonly RepositoryConfig _repository;

    public ArchiveManageFlowTests()
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
        _test.Jobs.Save([new BackupJob { Id = Guid.NewGuid(), Name = "Documents", RepositoryId = _repository.Id, Sources = ["/home/me"] }]);
        Borg.Archives.Add(("Documents-host-2026-11-01", new DateTime(2026, 11, 1, 20, 0, 0)));
        Borg.Archives.Add(("Documents-host-2026-11-02", new DateTime(2026, 11, 2, 20, 0, 0)));
        Borg.Archives.Add(("Documents-host-2026-11-03", new DateTime(2026, 11, 3, 20, 0, 0)));
    }

    public void Dispose() => _test.Dispose();

    private FakeBorg Borg => _test.Borg;

    private FakeDialogs Dialogs => _test.Dialogs;

    private string Repo => _test.Directory.Combine("Disk");

    private List<IReadOnlyList<string>> Calls(string command) => [.. Borg.Calls.Where(call => call.Arguments[0] == command).Select(call => call.Arguments)];

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

    private static void Select(MainViewModel main, params string[] names)
    {
        main.SelectedArchives.Clear();
        foreach (var name in names)
            main.SelectedArchives.Add(Archive(main, name));
        main.SelectedArchive = main.SelectedArchives[^1];
    }

    [Fact]
    public async Task Renaming_checks_the_name_and_the_list_follows()
    {
        var main = await StartAsync();
        Select(main, "Documents-host-2026-11-01");

        Dialogs.TextAnswers.Enqueue("Documents-host-2026-11-02");
        await main.RenameArchiveCommand.ExecuteAsync(null);
        Assert.Empty(Calls("rename"));
        Assert.Equal(BorgTexts.Format(Strings.ArchiveNameExists, "Documents-host-2026-11-02"), main.ArchivesError);
        Assert.Equal("Documents-host-2026-11-01", Dialogs.TextInitials[^1]);

        Dialogs.TextAnswers.Enqueue("a/b");
        await main.RenameArchiveCommand.ExecuteAsync(null);
        Assert.Equal(Strings.ArchiveNameInvalid, main.ArchivesError);

        Dialogs.TextAnswers.Enqueue("  Steuer-2026  ");
        await main.RenameArchiveCommand.ExecuteAsync(null);

        Assert.Equal(["rename", "--log-json", $"{Repo}::Documents-host-2026-11-01", "Steuer-2026"], Calls("rename").Single());
        Assert.Contains(main.Archives, archive => archive.Name == "Steuer-2026");
        Assert.Null(main.ArchivesError);
        Assert.Empty(main.SelectedArchives);
    }

    [Fact]
    public async Task Keeping_takes_archives_out_of_the_clean_up_and_back()
    {
        var main = await StartAsync();
        Select(main, "Documents-host-2026-11-01", "Documents-host-2026-11-02");
        Assert.True(main.KeepArchivesCommand.CanExecute(null));
        Assert.False(main.ReleaseArchivesCommand.CanExecute(null));

        await main.KeepArchivesCommand.ExecuteAsync(null);

        Assert.Equal(2, Calls("rename").Count);
        var kept = Archive(main, "keep-Documents-host-2026-11-01");
        Assert.True(kept.IsKept);
        Assert.Equal("Documents", kept.JobText); // still the job's archive
        // The job's clean-up looks at "Documents-{hostname}-*" only.
        Assert.False(BackupNames.ArchiveGlob("Documents").StartsWith(BackupNames.KeepPrefix, StringComparison.Ordinal));

        Select(main, "keep-Documents-host-2026-11-01", "Documents-host-2026-11-03");
        Assert.True(main.ReleaseArchivesCommand.CanExecute(null));
        Borg.Calls.Clear();
        await main.ReleaseArchivesCommand.ExecuteAsync(null);

        Assert.Equal(["rename", "--log-json", $"{Repo}::keep-Documents-host-2026-11-01", "Documents-host-2026-11-01"], Calls("rename").Single());
        Assert.False(Archive(main, "Documents-host-2026-11-01").IsKept);
    }

    [Fact]
    public async Task A_comment_is_kept_in_the_archive_and_shown()
    {
        var main = await StartAsync();
        Select(main, "Documents-host-2026-11-03");
        Dialogs.TextAnswers.Enqueue("before the update");

        await main.CommentArchiveCommand.ExecuteAsync(null);

        Assert.Equal(["recreate", "--log-json", "--comment", "before the update", $"{Repo}::Documents-host-2026-11-03"], Calls("recreate").Single());
        Assert.Contains("--format", Calls("list")[^1]);
        var archive = Archive(main, "Documents-host-2026-11-03");
        Assert.True(archive.HasComment);
        Assert.Equal("before the update", archive.Comment);

        // Unchanged: nothing to do.
        Select(main, "Documents-host-2026-11-03");
        Dialogs.TextAnswers.Enqueue("before the update");
        await main.CommentArchiveCommand.ExecuteAsync(null);
        Assert.Single(Calls("recreate"));
        Assert.Equal("before the update", Dialogs.TextInitials[^1]);
    }

    [Fact]
    public async Task Deleting_asks_with_the_names_and_offers_to_free_the_space()
    {
        var main = await StartAsync();
        Select(main, "Documents-host-2026-11-01", "Documents-host-2026-11-02");

        await main.DeleteArchivesCommand.ExecuteAsync(null);
        Assert.Empty(Calls("delete"));
        Assert.Contains("• Documents-host-2026-11-01", Dialogs.Messages[^1]);
        Assert.Contains("• Documents-host-2026-11-02", Dialogs.Messages[^1]);

        Select(main, "Documents-host-2026-11-01", "Documents-host-2026-11-02");
        Dialogs.ConfirmAnswers.Enqueue(true);
        Dialogs.ConfirmAnswers.Enqueue(true);
        await main.DeleteArchivesCommand.ExecuteAsync(null);

        Assert.Equal(["delete", "--log-json", $"{Repo}::Documents-host-2026-11-01", "Documents-host-2026-11-02"], Calls("delete").Single());
        Assert.Equal([Strings.ArchiveDeleteTitle, Strings.ArchiveDeleteTitle, Strings.ArchiveCompactTitle], Dialogs.Shown);
        Assert.Single(Calls("compact"));
        Assert.Equal(["Documents-host-2026-11-03"], main.Archives.Select(archive => archive.Name));
    }

    [Fact]
    public async Task A_failure_is_shown_and_BorgStudios_own_run_keeps_changes_away()
    {
        var main = await StartAsync();
        Select(main, "Documents-host-2026-11-01");
        Borg.ChangeError = "Archive.DoesNotExist";
        Dialogs.TextAnswers.Enqueue("new-name");
        await main.RenameArchiveCommand.ExecuteAsync(null);
        Assert.NotNull(main.ArchivesError);

        Borg.ChangeError = null;
        Borg.Calls.Clear();
        Select(main, "Documents-host-2026-11-01");
        using (Core.Backups.RepositoryLock.TryAcquire(main.Runner.LockDirectory, _repository.Id))
            await main.KeepArchivesCommand.ExecuteAsync(null);
        Assert.Empty(Calls("rename"));
        Assert.Equal(Strings.CheckBusy, main.ArchivesError);
    }

    [Fact]
    public async Task Single_archive_commands_need_exactly_one()
    {
        var main = await StartAsync();
        Select(main, "Documents-host-2026-11-01", "Documents-host-2026-11-02");

        Assert.False(main.RenameArchiveCommand.CanExecute(null));
        Assert.False(main.CommentArchiveCommand.CanExecute(null));
        Assert.True(main.DeleteArchivesCommand.CanExecute(null));
    }
}
