using BorgStudio.App.Resources;
using BorgStudio.App.ViewModels;
using BorgStudio.Core.Repositories;
using BorgStudio.Tests.TestSupport;

namespace BorgStudio.Tests.ViewModels;

/// <summary>Opening an archive: reading its contents (or taking them from the cache), browsing, searching, selecting.</summary>
public sealed class ArchiveBrowserFlowTests : IDisposable
{
    private const string Passphrase = "pw";

    private readonly TestServices _test = new();
    private readonly RepositoryConfig _repository;

    public ArchiveBrowserFlowTests()
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
        Borg.Archives.Add(("Documents-host-2026-11-05T10:00:00", new DateTime(2026, 11, 5, 10, 0, 0)));
        Borg.Archives.Add(("Documents-host-2026-11-06T10:00:00", new DateTime(2026, 11, 6, 10, 0, 0)));
    }

    public void Dispose() => _test.Dispose();

    private FakeBorg Borg => _test.Borg;

    private FakeDialogs Dialogs => _test.Dialogs;

    private async Task<MainViewModel> StartAsync()
    {
        var main = new MainViewModel(_test.Services);
        await main.CheckBorgCommand.ExecuteAsync(null);
        Dialogs.Passphrases.Enqueue(Passphrase);
        main.SelectedDetailsTab = MainViewModel.ArchivesTab;
        await main.ArchivesLoad;
        Borg.Calls.Clear();
        return main;
    }

    private IEnumerable<string> ListCalls => Borg.Calls.Where(call => call.Arguments.Contains("--json-lines")).Select(call => call.Arguments[^1]);

    [Fact]
    public async Task An_archive_is_read_once_then_opened_from_the_cache()
    {
        var main = await StartAsync();
        Assert.False(main.OpenArchiveCommand.CanExecute(null)); // nothing selected
        main.SelectedArchive = main.Archives[0];
        Assert.True(main.OpenArchiveCommand.CanExecute(null));

        ArchiveBrowserViewModel? seen = null;
        Dialogs.Browser = async browser =>
        {
            await main.BrowserLoad;
            seen = browser;
        };
        await main.OpenArchiveCommand.ExecuteAsync(null);

        // The passphrase was known from loading the tab: borg read the list without asking again.
        Assert.EndsWith("::Documents-host-2026-11-06T10:00:00", Assert.Single(ListCalls));
        Assert.Equal(Passphrase, Borg.Calls.Single().Passphrase);
        Assert.NotNull(seen);
        Assert.True(seen!.IsReady);
        Assert.False(seen.IsLoading);
        Assert.False(seen.Progress.IsActive);
        Assert.Equal(BorgTexts.Format(Strings.BrowserTitle, "Documents-host-2026-11-06T10:00:00"), seen.Title);
        Assert.Equal(BorgTexts.Format(Strings.BrowserSummary, 3, 4, BorgTexts.Size(51_200)), seen.SummaryText);
        // Opens where the data is: home › me › Documents.
        Assert.Equal("/home/me/Documents", seen.CurrentPathText);
        Assert.Equal(["Invoices", "latest", "letter.txt"], seen.Entries.Select(entry => entry.Name));
        Assert.Equal("Documents", seen.SelectedFolder!.Name);
        Assert.NotNull(_test.Durations.Get(Core.Progress.DurationHistory.Key(_repository.Id, "contents")));

        // Second time: straight from the cache, no borg, no passphrase.
        Borg.Calls.Clear();
        Dialogs.Browser = null;
        await main.OpenArchiveCommand.ExecuteAsync(null);
        Assert.Empty(Borg.Calls);
        Assert.True(Dialogs.Browsers[^1].IsReady);
        Assert.Equal("/home/me/Documents", Dialogs.Browsers[^1].CurrentPathText);
    }

    [Fact]
    public async Task Browsing_searching_and_selecting()
    {
        var main = await StartAsync();
        main.SelectedArchive = main.Archives[1];
        Dialogs.Browser = _ => main.BrowserLoad; // open until borg has read the list
        await main.OpenArchiveCommand.ExecuteAsync(null);
        var browser = Dialogs.Browsers.Single();

        // Into a folder and back up.
        browser.OpenCommand.Execute(browser.Entries.Single(entry => entry.Name == "Invoices"));
        Assert.Equal("/home/me/Documents/Invoices", browser.CurrentPathText);
        Assert.Equal(["invoice-2026.pdf"], browser.Entries.Select(entry => entry.Name));
        Assert.Equal("Invoices", browser.SelectedFolder!.Name);
        Assert.True(browser.Folders.Single().IsExpanded); // home
        browser.UpCommand.Execute(null);
        Assert.Equal("/home/me/Documents", browser.CurrentPathText);

        // The tree navigates too.
        browser.SelectedFolder = browser.Folders.Single();
        Assert.Equal("/home", browser.CurrentPathText);

        // Search the whole archive: by part of the name or a pattern, ignoring case.
        browser.SearchText = "INVOICE";
        await browser.SearchCommand.ExecuteAsync(null);
        Assert.True(browser.IsSearchResult);
        Assert.Equal(["Invoices", "invoice-2026.pdf"], browser.Entries.Select(entry => entry.Name));
        Assert.Equal(BorgTexts.Format(Strings.BrowserSearchResults, 2, "INVOICE"), browser.SearchStatus);
        Assert.Equal("/home/me/Documents", browser.Entries[0].LocationText);
        Assert.Equal("/home/me/Documents/Invoices", browser.Entries[1].LocationText);
        browser.SearchText = "*.pdf";
        await browser.SearchCommand.ExecuteAsync(null);
        Assert.Equal(["invoice-2026.pdf"], browser.Entries.Select(entry => entry.Name));
        browser.SearchText = "nothing-like-this";
        await browser.SearchCommand.ExecuteAsync(null);
        Assert.Equal(BorgTexts.Format(Strings.BrowserSearchNone, "nothing-like-this"), browser.SearchStatus);
        browser.ClearSearchCommand.Execute(null);
        Assert.False(browser.IsSearchResult);
        Assert.Equal("/home", browser.CurrentPathText);

        // A file found opens its folder.
        browser.SearchText = "letter";
        await browser.SearchCommand.ExecuteAsync(null);
        browser.OpenCommand.Execute(browser.Entries.Single());
        Assert.Equal("/home/me/Documents", browser.CurrentPathText);

        // Selecting: a file inside a selected folder counts once.
        Assert.Equal(Strings.BrowserSelectionNone, browser.SelectionText);
        browser.Entries.Single(entry => entry.Name == "Invoices").IsSelected = true;
        browser.Entries.Single(entry => entry.Name == "letter.txt").IsSelected = true;
        browser.OpenCommand.Execute(browser.Entries.Single(entry => entry.Name == "Invoices"));
        browser.Entries.Single().IsSelected = true; // the pdf inside
        Assert.Equal(["home/me/Documents/Invoices", "home/me/Documents/letter.txt"], browser.Selection.Select(node => node.BorgPath));
        Assert.Equal(BorgTexts.Format(Strings.BrowserSelection, 2, BorgTexts.Size(51_200)), browser.SelectionText);
        browser.UpCommand.Execute(null);
        Assert.True(browser.Entries.Single(entry => entry.Name == "Invoices").IsSelected); // kept while navigating

        browser.ClearSelectionCommand.Execute(null);
        Assert.False(browser.HasSelection);
        Assert.False(browser.Entries.Single(entry => entry.Name == "Invoices").IsSelected);
    }

    [Fact]
    public async Task A_failing_list_is_shown_and_nothing_is_cached()
    {
        var main = await StartAsync();
        main.SelectedArchive = main.Archives[0];
        Borg.ContentsError = "Archive.DoesNotExist";
        Dialogs.Browser = async browser => await main.BrowserLoad;

        await main.OpenArchiveCommand.ExecuteAsync(null);

        var browser = Dialogs.Browsers.Single();
        Assert.False(browser.IsReady);
        Assert.False(browser.IsLoading);
        Assert.StartsWith(Strings.BrowserLoadFailed[..Strings.BrowserLoadFailed.IndexOf('{')], browser.Error);
        Assert.Null(_test.Archives.LoadContents(_repository.Id, main.Archives[0].Archive.Id));

        // An empty archive is still an archive.
        Borg.ContentsError = null;
        Borg.Contents["Documents-host-2026-11-06T10:00:00"] = [];
        await main.OpenArchiveCommand.ExecuteAsync(null);
        Assert.True(Dialogs.Browsers[^1].IsReady);
        Assert.True(Dialogs.Browsers[^1].HasNoEntries);
    }

    [Fact]
    public async Task Contents_of_deleted_archives_are_dropped()
    {
        var main = await StartAsync();
        main.SelectedArchive = main.Archives[1];
        Dialogs.Browser = _ => main.BrowserLoad; // open until borg has read the list
        await main.OpenArchiveCommand.ExecuteAsync(null);
        var archiveId = main.Archives[1].Archive.Id;
        Assert.NotNull(_test.Archives.LoadContents(_repository.Id, archiveId));

        Borg.Archives.RemoveAt(0); // deleted elsewhere (e.g. prune)
        await main.RefreshArchivesCommand.ExecuteAsync(null);

        Assert.Single(main.Archives);
        Assert.Null(_test.Archives.LoadContents(_repository.Id, archiveId));
    }

    [Fact]
    public async Task Closing_the_browser_while_borg_reads_stops_it()
    {
        var main = await StartAsync();
        main.SelectedArchive = main.Archives[0];
        Borg.BeforeContents = cancellationToken => Task.Delay(Timeout.Infinite, cancellationToken); // borg still reading …
        Dialogs.Browser = null; // … when the browser is closed

        await main.OpenArchiveCommand.ExecuteAsync(null);

        Assert.False(Dialogs.Browsers.Single().IsReady);
        Assert.Equal(Strings.BrowserLoadCancelled, Dialogs.Browsers.Single().Error);
        Assert.Null(_test.Archives.LoadContents(_repository.Id, main.Archives[0].Archive.Id));
    }

    [Fact]
    public void Search_patterns()
    {
        var contains = ArchiveBrowserViewModel.Matcher("rech");
        Assert.True(contains("Rechnung.pdf"));
        Assert.False(contains("Brief.txt"));
        var pattern = ArchiveBrowserViewModel.Matcher("*.PDF");
        Assert.True(pattern("rechnung.pdf"));
        Assert.False(pattern("rechnung.pdf.bak"));
        Assert.True(ArchiveBrowserViewModel.Matcher("a?c")("ABC"));
        Assert.True(ArchiveBrowserViewModel.Matcher("(1).txt")("Kopie (1).txt")); // no regex surprises
    }
}
