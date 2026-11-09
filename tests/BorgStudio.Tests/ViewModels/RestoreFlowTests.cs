using BorgStudio.App.Resources;
using BorgStudio.App.ViewModels;
using BorgStudio.Core.Repositories;
using BorgStudio.Tests.TestSupport;

namespace BorgStudio.Tests.ViewModels;

/// <summary>Restoring from the archive browser: target, structure, overwriting, progress, outcome – with fake borg.</summary>
public sealed class RestoreFlowTests : IDisposable
{
    private const string Passphrase = "pw";
    private const string ArchiveName = "Documents-host-2026-11-06T10:00:00";

    private readonly TestServices _test = new();

    public RestoreFlowTests()
    {
        var repository = new RepositoryConfig
        {
            Id = Guid.NewGuid(),
            Name = "Disk",
            ProviderId = "local",
            ProviderValues = new Dictionary<string, string> { ["path"] = _test.Directory.Combine("repo") },
            PassphraseMode = PassphraseMode.Ask,
            EncryptionMode = "repokey-blake2",
        };
        _test.Store.Save([repository]);
        Borg.Archives.Add((ArchiveName, new DateTime(2026, 11, 6, 10, 0, 0)));
    }

    public void Dispose() => _test.Dispose();

    private FakeBorg Borg => _test.Borg;

    private FakeDialogs Dialogs => _test.Dialogs;

    private string Target => _test.Directory.Combine("restore");

    /// <summary>Opens the archive in the browser and runs <paramref name="inBrowser"/> while it is open.</summary>
    private async Task BrowseAsync(Func<ArchiveBrowserViewModel, Task> inBrowser)
    {
        var main = new MainViewModel(_test.Services);
        await main.CheckBorgCommand.ExecuteAsync(null);
        Dialogs.Passphrases.Enqueue(Passphrase);
        main.SelectedDetailsTab = MainViewModel.ArchivesTab;
        await main.ArchivesLoad;
        main.SelectedArchive = main.Archives.Single();
        Dialogs.Browser = async browser =>
        {
            await main.BrowserLoad;
            await inBrowser(browser);
        };
        await main.OpenArchiveCommand.ExecuteAsync(null);
        Borg.Calls.RemoveAll(call => call.Arguments[0] != "extract");
    }

    /// <summary>Selects "Invoices" and "letter.txt" in Documents and opens the restore dialog with <paramref name="inDialog"/>.</summary>
    private Task RestoreSelectionAsync(Func<RestoreViewModel, Task> inDialog) => BrowseAsync(async browser =>
    {
        browser.Entries.Single(entry => entry.Name == "Invoices").IsSelected = true;
        browser.Entries.Single(entry => entry.Name == "letter.txt").IsSelected = true;
        Dialogs.Restore = inDialog;
        await browser.RestoreSelectionCommand.ExecuteAsync(null);
    });

    private static async Task StartAsync(RestoreViewModel restore)
    {
        await restore.StartCommand.ExecuteAsync(null);
        await restore.Running;
    }

    [Fact]
    public async Task The_selection_is_restored_directly_into_a_folder()
    {
        RestoreViewModel? seen = null;
        await RestoreSelectionAsync(async restore =>
        {
            seen = restore;
            Assert.Equal(BorgTexts.Format(Strings.RestoreWhatSelection, 2, ArchiveName, BorgTexts.Size(51_200)), restore.WhatText);
            Assert.Equal(!OperatingSystem.IsWindows(), restore.CanRestoreToOriginal); // borg runs natively here
            Assert.True(restore.ToFolder);
            Assert.True(restore.Direct);

            await StartAsync(restore);
            Assert.Equal(Strings.RestoreFolderRequired, restore.Error); // no folder yet
            Assert.True(restore.IsOptions);

            Dialogs.Folders.Enqueue(Target);
            await restore.PickFolderCommand.ExecuteAsync(null);
            Assert.Equal(BorgTexts.Format(Strings.RestoreExample, Path.Combine(Target, "Invoices", "invoice-2026.pdf")), restore.ExampleText);
            await StartAsync(restore);
        });

        var extract = Assert.Single(Borg.Calls).Arguments.ToList();
        Assert.Equal("3", extract[extract.IndexOf("--strip-components") + 1]); // home, me, Documents
        Assert.Contains("pp:home/me/Documents/Invoices", extract);
        Assert.Contains("pp:home/me/Documents/letter.txt", extract);
        Assert.Equal(Passphrase, Borg.Calls[0].Passphrase);
        Assert.Equal(Target, Assert.Single(Borg.ExtractFolders));
        Assert.Equal("restored", File.ReadAllText(Path.Combine(Target, "Invoices", "invoice-2026.pdf")));
        Assert.True(File.Exists(Path.Combine(Target, "letter.txt")));

        Assert.True(seen!.IsDone);
        Assert.False(seen.ResultIsError);
        Assert.Equal(BorgTexts.Format(Strings.RestoreDone, BorgTexts.Size(51_200), Target), seen.ResultText);
        Assert.Equal(Target, seen.OpenFolderPath);
        Assert.True(seen.CanOpenFolder);
        Assert.Equal(1, seen.Progress.Fraction); // borg's bytes
    }

    [Fact]
    public async Task With_the_full_path_and_the_whole_archive()
    {
        await BrowseAsync(async browser =>
        {
            Dialogs.Restore = async restore =>
            {
                Assert.Equal(BorgTexts.Format(Strings.RestoreWhatAll, ArchiveName, BorgTexts.Size(51_200)), restore.WhatText);
                restore.TargetFolder = Target;
                restore.KeepFullPath = true;
                await StartAsync(restore);
            };
            await browser.RestoreAllCommand.ExecuteAsync(null);
        });

        var extract = Assert.Single(Borg.Calls).Arguments;
        Assert.DoesNotContain("--strip-components", extract);
        Assert.Contains("pp:home/me/Documents", extract); // the backed-up folder
        Assert.True(File.Exists(Path.Combine(Target, "home", "me", "Documents", "Invoices", "invoice-2026.pdf")));
    }

    [Fact]
    public async Task Existing_files_are_only_overwritten_when_confirmed()
    {
        Directory.CreateDirectory(Target);
        File.WriteAllText(Path.Combine(Target, "letter.txt"), "mine");
        await RestoreSelectionAsync(async restore =>
        {
            restore.TargetFolder = Target;
            Dialogs.ConfirmAnswers.Enqueue(false);
            await StartAsync(restore);
            Assert.True(restore.IsOptions); // back to the choices, nothing written
            Assert.DoesNotContain(Borg.Calls, call => call.Arguments[0] == "extract");
            Assert.Equal("mine", File.ReadAllText(Path.Combine(Target, "letter.txt")));
            Assert.Equal(Strings.RestoreOverwriteTitle, Dialogs.Shown[^1]);
            Assert.Contains(Path.Combine(Target, "letter.txt"), Dialogs.Messages[^1]);
            Assert.StartsWith(BorgTexts.Format(Strings.RestoreOverwriteMessage, 1, 1), Dialogs.Messages[^1]);

            Dialogs.ConfirmAnswers.Enqueue(true);
            await StartAsync(restore);
            Assert.True(restore.IsDone);
        });

        Assert.Equal("restored", File.ReadAllText(Path.Combine(Target, "letter.txt")));
    }

    [Fact]
    public async Task Failing_and_cancelling_are_reported()
    {
        Borg.ExtractError = "Repository.DoesNotExist";
        await RestoreSelectionAsync(async restore =>
        {
            restore.TargetFolder = Target;
            await StartAsync(restore);
            Assert.True(restore.ResultIsError);
            Assert.StartsWith(Strings.RestoreFailed[..Strings.RestoreFailed.IndexOf('{')], restore.ResultText);
            Assert.False(restore.CanOpenFolder);
        });

        Borg.ExtractError = null;
        RestoreViewModel? cancelled = null;
        Borg.BeforeExtract = async cancellationToken =>
        {
            cancelled!.CancelCommand.Execute(null); // the user clicks "Cancel" while borg runs
            await Task.Delay(Timeout.Infinite, cancellationToken);
        };
        await RestoreSelectionAsync(async restore =>
        {
            cancelled = restore;
            restore.TargetFolder = Target;
            await StartAsync(restore);
        });
        Assert.Equal(Strings.RestoreCancelled, cancelled!.ResultText);
        Assert.False(cancelled.ResultIsError);
    }

    [Fact]
    public void Warnings_name_Windows_files_as_the_user_knows_them()
    {
        Assert.Equal(@"C:\Users\me\a.txt: [Errno 13] Permission denied",
            RestoreViewModel.Readable("mnt/c/Users/me/a.txt: [Errno 13] Permission denied"));
        Assert.Equal("home/me/a.txt: odd", RestoreViewModel.Readable("home/me/a.txt: odd"));
        Assert.Equal("no path here", RestoreViewModel.Readable("no path here"));
    }

    [Fact]
    public void Progress_shows_the_pace_when_asked()
    {
        var time = new ManualTime();
        var progress = new ProgressViewModel(time);
        progress.Start(null, rate: BorgTexts.Rate);
        progress.Report(0, 10_000_000);
        time.Advance(TimeSpan.FromSeconds(2));
        progress.Report(4_000_000, 10_000_000);
        Assert.Contains(BorgTexts.Rate(2_000_000), progress.SummaryText);
    }
}
