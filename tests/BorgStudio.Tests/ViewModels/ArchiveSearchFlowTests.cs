using BorgStudio.App.Resources;
using BorgStudio.App.ViewModels;
using BorgStudio.Core.Progress;
using BorgStudio.Core.Repositories;
using BorgStudio.Tests.TestSupport;

namespace BorgStudio.Tests.ViewModels;

/// <summary>Searching all archives of a repository and the versions of a file – with fake borg.</summary>
public sealed class ArchiveSearchFlowTests : IDisposable
{
    private const string Passphrase = "pw";
    private const string Oldest = "Documents-host-2026-11-04T10:00:00";
    private const string Middle = "Documents-host-2026-11-05T10:00:00";
    private const string Newest = "Documents-host-2026-11-06T10:00:00";

    private readonly TestServices _test = new();
    private readonly RepositoryConfig _repository;

    public ArchiveSearchFlowTests()
    {
        _repository = new RepositoryConfig
        {
            Id = Guid.NewGuid(),
            Name = "Disk",
            ProviderId = "local",
            ProviderValues = new Dictionary<string, string> { ["path"] = _test.Directory.Combine("repo") },
            PassphraseMode = PassphraseMode.Ask,
            EncryptionMode = "repokey-blake2",
        };
        _test.Store.Save([_repository]);
        Borg.Archives.Add((Oldest, new DateTime(2026, 11, 4, 10, 0, 0)));
        Borg.Archives.Add((Middle, new DateTime(2026, 11, 5, 10, 0, 0)));
        Borg.Archives.Add((Newest, new DateTime(2026, 11, 6, 10, 0, 0)));
        // letter.txt: the same in the two older archives, changed (bigger, newer) in the newest; notes.txt only there.
        Borg.Contents[Newest] = [.. FakeBorg.DefaultContents.Select(entry => entry.Path.EndsWith("letter.txt") ? entry with { Size = 2400 } : entry),
            ("-", "home/me/Documents/notes.txt", 300)];
        Borg.ContentsTime[Newest] = "2026-11-06T08:00:00.000000";
    }

    public void Dispose() => _test.Dispose();

    private FakeBorg Borg => _test.Borg;

    private FakeDialogs Dialogs => _test.Dialogs;

    private IEnumerable<string> ListCalls => Borg.Calls.Where(call => call.Arguments.Contains("--json-lines")).Select(call => call.Arguments[^1]);

    private async Task<MainViewModel> StartAsync()
    {
        var main = new MainViewModel(_test.Services);
        await main.CheckBorgCommand.ExecuteAsync(null);
        Assert.False(main.SearchArchivesCommand.CanExecute(null)); // no archives shown yet
        Dialogs.Passphrases.Enqueue(Passphrase);
        main.SelectedDetailsTab = MainViewModel.ArchivesTab;
        await main.ArchivesLoad;
        Borg.Calls.Clear();
        return main;
    }

    /// <summary>Opens the search window and runs <paramref name="inWindow"/> while it is open.</summary>
    private async Task SearchWindowAsync(MainViewModel main, Func<ArchiveSearchViewModel, Task> inWindow)
    {
        Dialogs.Search = inWindow;
        await main.SearchArchivesCommand.ExecuteAsync(null);
    }

    private static async Task SearchAsync(ArchiveSearchViewModel search, string text)
    {
        search.SearchText = text;
        await search.SearchCommand.ExecuteAsync(null);
        await search.Searching;
    }

    [Fact]
    public async Task Archives_never_read_are_read_after_asking_then_searched_from_the_cache()
    {
        var main = await StartAsync();
        Assert.True(main.SearchArchivesCommand.CanExecute(null));

        await SearchWindowAsync(main, async search =>
        {
            Assert.Equal(BorgTexts.Format(Strings.SearchTitle, "Disk"), search.Title);
            Assert.Equal(BorgTexts.Format(Strings.SearchScope, 3, 0), search.ScopeText);

            // Asked first: none was read yet, so the other answer is "Cancel" – nothing searched.
            Dialogs.ConfirmAnswers.Enqueue(false);
            await SearchAsync(search, "letter");
            Assert.Equal(Strings.ScanReadTitle, Dialogs.Shown[^1]);
            Assert.StartsWith(BorgTexts.Format(Strings.ScanReadMessage, 3, 3), Dialogs.Messages[^1]);
            Assert.EndsWith(Strings.ScanReadEstimateUnknown, Dialogs.Messages[^1]);
            Assert.Empty(ListCalls);
            Assert.Empty(search.Hits);
            Assert.Equal(BorgTexts.Format(Strings.ScanSkipped, 3), search.SkippedText);

            // "Read them now": borg reads one archive after the other, newest first, and tells which.
            var details = new List<string?>();
            Borg.BeforeContents = _ =>
            {
                details.Add(search.Progress.Detail);
                return Task.CompletedTask;
            };
            await search.LoadMissingCommand.ExecuteAsync(null);
            await search.Searching;
            Assert.Equal([$"::{Newest}", $"::{Middle}", $"::{Oldest}"], ListCalls.Select(call => call[call.IndexOf("::", StringComparison.Ordinal)..]));
            Assert.Equal(BorgTexts.Format(Strings.ScanArchiveOf, 2, 3, Middle), details[1]);
            Assert.Equal(Passphrase, Borg.Calls[0].Passphrase);
            Assert.False(search.HasSkipped);
            Assert.False(search.IsScanning);
            Assert.False(search.Progress.IsActive);

            var hit = Assert.Single(search.Hits);
            Assert.Equal("letter.txt", hit.Name);
            Assert.Equal("/home/me/Documents", hit.FolderText);
            Assert.Equal(2, hit.VersionCount);
            Assert.Equal(BorgTexts.Format(Strings.SearchInArchives, 3, 3), hit.ArchivesText);
            Assert.Equal(2400, hit.Size); // as in the newest archive
            Assert.Equal(BorgTexts.Format(Strings.SearchResultOne, 1, "letter", 3), search.StatusText);
            Assert.Equal(hit, search.SelectedHit);
            Assert.Equal(BorgTexts.Format(Strings.SearchScopeAll, 3), search.ScopeText);

            // Everything is cached now: no borg, no question.
            Borg.Calls.Clear();
            var asked = Dialogs.Shown.Count;
            await SearchAsync(search, "*.txt");
            Assert.Empty(Borg.Calls);
            Assert.Equal(asked, Dialogs.Shown.Count);
            Assert.Equal(["letter.txt", "notes.txt"], search.Hits.Select(found => found.Name).Order());
            Assert.Equal(BorgTexts.Format(Strings.SearchInArchives, 1, 3), search.Hits.Single(found => found.Name == "notes.txt").ArchivesText);

            await SearchAsync(search, "nothing-like-this");
            Assert.Equal(BorgTexts.Format(Strings.SearchNone, "nothing-like-this", 3), search.StatusText);
        });

        foreach (var archive in main.Archives)
            Assert.True(_test.Archives.HasContents(_repository.Id, archive.Archive.Id));
        Assert.NotNull(_test.Durations.Get(DurationHistory.Key(_repository.Id, "contents")));
    }

    [Fact]
    public async Task Declining_searches_only_the_archives_read_before()
    {
        var main = await StartAsync();
        main.SelectedArchive = main.Archives[0]; // the newest, opened once
        Dialogs.Browser = _ => main.BrowserLoad;
        await main.OpenArchiveCommand.ExecuteAsync(null);
        Borg.Calls.Clear();

        await SearchWindowAsync(main, async search =>
        {
            Dialogs.ConfirmAnswers.Enqueue(false); // "only the others"
            await SearchAsync(search, "notes");
            Assert.StartsWith(BorgTexts.Format(Strings.ScanReadMessage, 2, 3), Dialogs.Messages[^1]);
            Assert.Empty(ListCalls);
            Assert.Equal("notes.txt", Assert.Single(search.Hits).Name);
            Assert.Equal(BorgTexts.Format(Strings.SearchInArchives, 1, 1), search.Hits[0].ArchivesText);
            Assert.Equal(BorgTexts.Format(Strings.ScanSkipped, 2), search.SkippedText);

            // Not asked again in this window.
            var asked = Dialogs.Shown.Count;
            await SearchAsync(search, "letter");
            Assert.Equal(asked, Dialogs.Shown.Count);
            Assert.Equal(1, search.Hits.Single().VersionCount);

            await search.LoadMissingCommand.ExecuteAsync(null);
            await search.Searching;
            Assert.Equal(2, ListCalls.Count()); // only the two missing
            Assert.Equal(2, search.Hits.Single().VersionCount);
        });
    }

    [Fact]
    public async Task A_version_found_by_the_search_is_restored_from_its_newest_archive()
    {
        var main = await StartAsync();
        var target = _test.Directory.Combine("restore");
        await SearchWindowAsync(main, async search =>
        {
            Dialogs.ConfirmAnswers.Enqueue(true);
            await SearchAsync(search, "letter");
            Borg.Calls.Clear();

            Dialogs.Versions = async versions =>
            {
                Assert.Equal(BorgTexts.Format(Strings.VersionsTitle, "letter.txt"), versions.Title);
                Assert.Equal("/home/me/Documents/letter.txt", versions.PathText);
                Assert.Equal(BorgTexts.Format(Strings.VersionsSummary, 2, 3, 3), versions.SummaryText);
                Assert.Equal(2, versions.Versions.Count);
                var (changed, before) = (versions.Versions[0], versions.Versions[1]);
                Assert.True(changed.IsNewest);
                Assert.Equal(BorgTexts.Size(2400), changed.SizeText);
                Assert.Equal(Strings.VersionsArchiveOne, changed.ArchivesText);
                Assert.Equal(BorgTexts.Format(Strings.VersionsArchives, 2), before.ArchivesText);
                Assert.Equal(BorgTexts.Format(Strings.VersionsFrom, Middle), before.Tip);
                Assert.Equal(changed, versions.SelectedVersion);
                Assert.Empty(Borg.Calls); // known from the search

                versions.SelectedVersion = before;
                Dialogs.Restore = async restore =>
                {
                    restore.TargetFolder = target;
                    await restore.StartCommand.ExecuteAsync(null);
                    await restore.Running;
                };
                await versions.RestoreCommand.ExecuteAsync(null);
            };
            await search.ShowVersionsCommand.ExecuteAsync(null);
        });

        var extract = Assert.Single(Borg.Calls).Arguments;
        Assert.Equal("extract", extract[0]);
        Assert.Contains(extract, argument => argument.EndsWith("::" + Middle, StringComparison.Ordinal));
        Assert.Contains("pp:home/me/Documents/letter.txt", extract);
        Assert.True(File.Exists(Path.Combine(target, "letter.txt")));
    }

    [Fact]
    public async Task The_browser_shows_the_versions_of_an_entry()
    {
        var main = await StartAsync();
        main.SelectedArchive = main.Archives[0];
        Dialogs.Browser = async browser =>
        {
            await main.BrowserLoad;
            Assert.False(browser.ShowVersionsCommand.CanExecute(null)); // no row selected
            browser.SelectedEntry = browser.Entries.Single(entry => entry.Name == "notes.txt");
            Dialogs.ConfirmAnswers.Enqueue(true); // read the two others
            Dialogs.Versions = _ => Task.CompletedTask;
            await browser.ShowVersionsCommand.ExecuteAsync(null);
        };
        await main.OpenArchiveCommand.ExecuteAsync(null);

        var versions = Dialogs.VersionWindows.Single();
        await versions.Loading;
        Assert.StartsWith(BorgTexts.Format(Strings.ScanReadMessage, 2, 3), Dialogs.Messages[^1]);
        // Only in the newest archive: not there yet before.
        Assert.Equal([false, true], versions.Versions.Select(row => row.IsMissing));
        Assert.Equal(Strings.VersionsMissing, versions.Versions[1].ModifiedText);
        Assert.Equal(BorgTexts.Format(Strings.VersionsArchives, 2), versions.Versions[1].ArchivesText);
        Assert.Equal(BorgTexts.Format(Strings.VersionsSummaryOne, 1, 1, 3), versions.SummaryText);

        versions.SelectedVersion = versions.Versions[1];
        Assert.False(versions.RestoreCommand.CanExecute(null)); // nothing to restore from there
    }

    [Fact]
    public async Task Cancelling_or_failing_keeps_what_was_found_so_far()
    {
        var main = await StartAsync();
        await SearchWindowAsync(main, async search =>
        {
            Dialogs.ConfirmAnswers.Enqueue(true);
            var reads = 0;
            Borg.BeforeContents = async cancellationToken =>
            {
                if (++reads < 2)
                    return;
                search.Progress.CancelCommand.Execute(null); // the user clicks "Cancel" while borg reads the second
                await Task.Delay(Timeout.Infinite, cancellationToken);
            };
            await SearchAsync(search, "letter");
            Assert.StartsWith(BorgTexts.Format(Strings.ScanCancelled, 1, 3), search.StatusText);
            Assert.Equal(BorgTexts.Format(Strings.SearchInArchives, 1, 1), Assert.Single(search.Hits).ArchivesText);
            Assert.False(_test.Archives.HasContents(_repository.Id, main.Archives[1].Archive.Id));

            Borg.BeforeContents = null;
            Borg.ContentsError = "Repository.DoesNotExist";
            await SearchAsync(search, "letter");
            Assert.StartsWith(BorgTexts.Format(Strings.ScanReadFailed, Middle, "").TrimEnd(), search.Error);
            Assert.Single(search.Hits); // from the newest, cached
        });
    }
}
