using BorgStudio.App.Resources;
using BorgStudio.App.ViewModels;
using BorgStudio.Core.Backups;
using BorgStudio.Core.Repositories;
using BorgStudio.Tests.TestSupport;

namespace BorgStudio.Tests.ViewModels;

/// <summary>The "Archives" tab: listing, details for the newest archives, loading more – with fake borg.</summary>
public sealed class ArchiveListFlowTests : IDisposable
{
    private readonly TestServices _test = new();
    private readonly RepositoryConfig _repository;
    private readonly RepositoryConfig _other;

    public ArchiveListFlowTests()
    {
        _repository = Repository("Disk");
        _other = Repository("Stick");
        _test.Store.Save([_repository, _other]);
    }

    public void Dispose() => _test.Dispose();

    private RepositoryConfig Repository(string name) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        ProviderId = "local",
        ProviderValues = new Dictionary<string, string> { ["path"] = _test.Directory.Combine(name) },
        PassphraseMode = PassphraseMode.Ask,
        EncryptionMode = "repokey-blake2",
    };

    private void AddArchives(int count, string prefix = "Documents-host-")
    {
        for (var index = 0; index < count; index++)
            _test.Borg.Archives.Add(($"{prefix}{index:D2}", new DateTime(2026, 10, 1, 8, 0, 0).AddDays(index)));
    }

    private async Task<MainViewModel> StartAsync()
    {
        var main = new MainViewModel(_test.Services);
        await main.CheckBorgCommand.ExecuteAsync(null);
        main.SelectedRepository = main.Repositories.Single(item => item.Name == "Disk");
        _test.Borg.Calls.Clear();
        return main;
    }

    /// <summary>How many archives the call asked details for.</summary>
    private string Last(Index call)
    {
        var arguments = _test.Borg.Calls[call].Arguments.ToList();
        return arguments[arguments.IndexOf("--last") + 1];
    }

    private static async Task OpenTabAsync(MainViewModel main)
    {
        main.SelectedDetailsTab = MainViewModel.ArchivesTab;
        await main.ArchivesLoad;
    }

    [Fact]
    public async Task Nothing_is_loaded_until_the_tab_is_opened()
    {
        AddArchives(3);
        var main = await StartAsync();
        main.SelectedDetailsTab = MainViewModel.JobsTab;

        Assert.Empty(_test.Borg.Calls);
        Assert.Empty(main.Archives);
    }

    [Fact]
    public async Task The_list_comes_first_then_details_for_the_newest_archives()
    {
        AddArchives(25);
        _test.Dialogs.Passphrases.Enqueue("secret");
        var main = await StartAsync();

        await OpenTabAsync(main);

        Assert.Equal(["list", "info"], _test.Borg.Commands);
        Assert.All(_test.Borg.Calls, call => Assert.Equal("secret", call.Passphrase));
        Assert.Equal("20", Last(1));
        Assert.Equal(25, main.Archives.Count);
        Assert.Equal("Documents-host-24", main.Archives[0].Name); // newest first
        Assert.Equal(BorgTexts.Format(Strings.ArchivesCountWithDetails, 25, 20), main.ArchivesStatus);

        var newest = main.Archives[0];
        Assert.Equal(BorgTexts.Size(25_000_000), newest.OriginalText);
        Assert.Equal(BorgTexts.Size(25_000), newest.NewText);
        Assert.Equal(BorgTexts.Duration(TimeSpan.FromSeconds(250.5)), newest.DurationText);
        Assert.Equal(2500.ToString("N0", System.Globalization.CultureInfo.CurrentCulture), newest.FilesText);
        Assert.Equal("…", main.Archives[20].OriginalText); // not loaded yet
        Assert.True(main.LoadMoreArchiveDetailsCommand.CanExecute(null));

        await main.LoadMoreArchiveDetailsCommand.ExecuteAsync(null);

        Assert.Equal("25", Last(2));
        Assert.Equal("secret", _test.Borg.Calls[2].Passphrase); // not asked again
        Assert.Empty(_test.Dialogs.Passphrases);
        Assert.Equal(BorgTexts.Size(1_000_000), main.Archives[^1].OriginalText);
        Assert.Equal(BorgTexts.Format(Strings.ArchivesCount, 25), main.ArchivesStatus);
        Assert.False(main.LoadMoreArchiveDetailsCommand.CanExecute(null));

        // Opening the tab again does not reload; refreshing does, with the same number of details.
        main.SelectedDetailsTab = MainViewModel.OverviewTab;
        await OpenTabAsync(main);
        Assert.Equal(3, _test.Borg.Calls.Count);
        await main.RefreshArchivesCommand.ExecuteAsync(null);
        Assert.Equal(["list", "info"], _test.Borg.Commands.Skip(3));
        Assert.Equal("25", Last(^1));
    }

    [Fact]
    public async Task Archives_show_the_job_they_come_from()
    {
        _test.Jobs.Save([
            new BackupJob { Id = Guid.NewGuid(), Name = "Documents", RepositoryId = _repository.Id, Sources = ["x"] },
            new BackupJob { Id = Guid.NewGuid(), Name = "Documents & Photos", RepositoryId = _repository.Id, Sources = ["x"] },
            new BackupJob { Id = Guid.NewGuid(), Name = "Elsewhere", RepositoryId = _other.Id, Sources = ["x"] },
        ]);
        _test.Borg.Archives.Add(($"{MainViewModel.ArchivePrefix("Documents")}-host-1", new DateTime(2026, 11, 1)));
        _test.Borg.Archives.Add(($"{MainViewModel.ArchivePrefix("Documents & Photos")}-host-2", new DateTime(2026, 11, 2)));
        _test.Borg.Archives.Add(($"{MainViewModel.ArchivePrefix("Elsewhere")}-host-3", new DateTime(2026, 11, 3)));
        _test.Borg.Archives.Add(("made-by-hand", new DateTime(2026, 11, 4)));
        _test.Dialogs.Passphrases.Enqueue("secret");
        var main = await StartAsync();

        await OpenTabAsync(main);

        // Newest first; "Elsewhere" belongs to another repository, and the longer job name wins over "Documents".
        Assert.Equal(["–", "–", "Documents & Photos", "Documents"], main.Archives.Select(archive => archive.JobText));
    }

    [Fact]
    public async Task A_finished_backup_appears_in_the_open_tab()
    {
        var source = _test.Directory.Combine("Documents");
        Directory.CreateDirectory(source);
        _test.Jobs.Save([new BackupJob
        {
            Id = Guid.NewGuid(), Name = "Documents", RepositoryId = _repository.Id, Sources = [source],
            Exclusions = new BackupExclusions { CachesAndTemp = false },
        }]);
        AddArchives(2);
        _test.Dialogs.Passphrases.Enqueue("secret");
        _test.Dialogs.Passphrases.Enqueue("secret");
        var main = await StartAsync();
        await OpenTabAsync(main);
        Assert.Equal(2, main.Archives.Count);

        await main.RunJobCommand.ExecuteAsync(null);
        await main.ArchivesLoad;

        Assert.Equal(3, main.Archives.Count);
        Assert.Equal("Documents-host-2026-11-06T10:00:00", main.Archives[0].Name);
        Assert.Equal("Documents", main.Archives[0].JobText);
        Assert.NotEqual("…", main.Archives[0].OriginalText);
        Assert.Empty(_test.Dialogs.Passphrases); // the backup asked once, the reload reused the tab's passphrase
    }

    [Fact]
    public async Task Another_repository_shows_its_own_archives()
    {
        AddArchives(2);
        _test.Dialogs.Passphrases.Enqueue("disk");
        _test.Dialogs.Passphrases.Enqueue("stick");
        var main = await StartAsync();
        await OpenTabAsync(main);

        main.SelectedRepository = main.Repositories.Single(item => item.Name == "Stick");
        await main.ArchivesLoad;

        Assert.Equal("stick", _test.Borg.Calls[^1].Passphrase);
        Assert.EndsWith("Stick", _test.Borg.Calls[^1].Arguments[^1]);
        Assert.Equal(2, main.Archives.Count);
    }

    [Fact]
    public async Task Problems_are_shown_and_the_list_stays()
    {
        AddArchives(2);
        _test.Borg.ArchiveDetailsError = "Repository.DoesNotExist";
        _test.Dialogs.Passphrases.Enqueue("secret");
        var main = await StartAsync();

        await OpenTabAsync(main);

        Assert.Equal(2, main.Archives.Count);
        Assert.True(main.HasArchivesError);
        Assert.Equal("…", main.Archives[0].OriginalText);
    }

    [Fact]
    public async Task Cancelling_the_passphrase_loads_nothing()
    {
        AddArchives(2);
        var main = await StartAsync();

        await OpenTabAsync(main);

        Assert.Empty(_test.Borg.Calls);
        Assert.Empty(main.Archives);
        Assert.False(main.IsLoadingArchives);
        Assert.True(main.RefreshArchivesCommand.CanExecute(null));
    }

    [Fact]
    public async Task An_empty_repository_says_so()
    {
        _test.Dialogs.Passphrases.Enqueue("secret");
        var main = await StartAsync();

        await OpenTabAsync(main);

        Assert.Equal(["list"], _test.Borg.Commands);
        Assert.Equal(Strings.ArchivesNone, main.ArchivesStatus);
    }
}
