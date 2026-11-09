using BorgStudio.App.Resources;
using BorgStudio.App.ViewModels;
using BorgStudio.Core.Updates;
using BorgStudio.Tests.TestSupport;

namespace BorgStudio.Tests.ViewModels;

/// <summary>The update banner, the update window and the notice after an update.</summary>
public sealed class UpdateFlowTests : IDisposable
{
    private readonly TestServices _test = new();

    public void Dispose() => _test.Dispose();

    private FakeUpdateSource Source => _test.UpdateSource;

    private async Task<MainViewModel> StartAsync()
    {
        var viewModel = new MainViewModel(_test.Services);
        await viewModel.CheckBorgCommand.ExecuteAsync(null);
        return viewModel;
    }

    [Fact]
    public async Task A_newer_release_shows_the_banner_until_later()
    {
        Source.Publish("0.3.0");
        var viewModel = await StartAsync();

        await viewModel.CheckForUpdatesInBackgroundAsync();

        Assert.True(viewModel.HasUpdate);
        Assert.Equal(BorgTexts.Format(Strings.UpdateAvailable, "0.3.0"), viewModel.UpdateBannerText);
        viewModel.DismissUpdateCommand.Execute(null);
        Assert.False(viewModel.HasUpdate);
    }

    [Fact]
    public async Task No_banner_when_up_to_date_offline_or_switched_off()
    {
        var viewModel = await StartAsync();
        Source.Publish("0.2.0");
        await viewModel.CheckForUpdatesInBackgroundAsync();
        Assert.False(viewModel.HasUpdate);

        Source.Error = new HttpRequestException("offline");
        await viewModel.CheckForUpdatesInBackgroundAsync();
        Assert.False(viewModel.HasUpdate);

        Source.Error = null;
        Source.Publish("0.3.0");
        var settings = new SettingsViewModel(viewModel, _test.Dialogs);
        settings.CheckForUpdates = false;
        Assert.False(_test.Settings.Load().CheckForUpdates);
        var checks = Source.Checks;
        await viewModel.CheckForUpdatesInBackgroundAsync();
        Assert.Equal(checks, Source.Checks);
        Assert.False(viewModel.HasUpdate);
    }

    [Fact]
    public async Task Details_shows_the_known_release_and_installs_it()
    {
        Source.Publish("0.3.0");
        var viewModel = await StartAsync();
        await viewModel.CheckForUpdatesInBackgroundAsync();
        var checks = Source.Checks;

        await viewModel.ShowUpdateCommand.ExecuteAsync(null);
        var update = _test.Dialogs.Updates.Single();
        await update.BeginAsync();

        // Not asked again.
        Assert.Equal(checks, Source.Checks);
        Assert.True(update.IsResult);
        Assert.Equal(BorgTexts.Format(Strings.UpdateAvailable, "0.3.0"), update.Headline);
        Assert.Equal("What's new\n• Things", update.Notes);
        Assert.Equal(BorgTexts.Format(Strings.UpdateInstalledVersion, "0.2.0"), update.InstalledText);
        Assert.True(update.CanInstall);
        Assert.False(update.HasBlocker);

        await update.InstallCommand.ExecuteAsync(null);

        Assert.True(update.IsReady);
        Assert.False(update.Progress.IsActive);
        var start = _test.Dialogs.RestartsWith.Single();
        Assert.Equal(Path.Combine(_test.Directory.Combine("updates", "0.3.0", "app"), FakeUpdateSource.Executable), start.FileName);
        Assert.Contains(UpdateApplier.Argument, start.ArgumentList);
        Assert.Contains(_test.AppFolder, start.ArgumentList);

        // Closing was refused (e.g. a backup runs): the restart can be asked for again.
        update.RestartCommand.Execute(null);
        Assert.Equal(2, _test.Dialogs.RestartsWith.Count);
    }

    [Fact]
    public async Task The_window_looks_for_itself_when_opened_from_the_menu()
    {
        Source.Publish("0.2.0");
        var viewModel = await StartAsync();
        await viewModel.ShowUpdateCommand.ExecuteAsync(null);
        var update = _test.Dialogs.Updates.Single();
        Assert.True(update.IsChecking);

        await update.BeginAsync();

        Assert.True(update.IsResult);
        Assert.Equal(BorgTexts.Format(Strings.UpdateUpToDate, "0.2.0"), update.Headline);
        Assert.False(update.CanInstall);
        Assert.False(update.HasNotes);
        Assert.False(update.InstallCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_failed_check_is_shown()
    {
        Source.Error = new HttpRequestException("no network");
        var update = new UpdateViewModel(await StartAsync(), _test.Dialogs, known: null);

        await update.BeginAsync();

        Assert.True(update.IsResult);
        Assert.Equal(BorgTexts.Format(Strings.UpdateCheckFailed, "no network"), update.Error);
    }

    [Fact]
    public async Task A_damaged_download_is_not_installed()
    {
        Source.Publish("0.3.0", corrupt: true);
        var update = new UpdateViewModel(await StartAsync(), _test.Dialogs, known: null);
        await update.BeginAsync();

        await update.InstallCommand.ExecuteAsync(null);

        Assert.True(update.IsResult);
        Assert.Equal(BorgTexts.Format(Strings.UpdateFailed, Strings.UpdateChecksumMismatch), update.Error);
        Assert.Empty(_test.Dialogs.RestartsWith);
        Assert.True(update.InstallCommand.CanExecute(null));
    }

    [Fact]
    public async Task Cancelling_the_download_goes_back()
    {
        Source.Publish("0.3.0");
        var update = new UpdateViewModel(await StartAsync(), _test.Dialogs, known: null);
        await update.BeginAsync();
        Source.BeforeDownload = () =>
        {
            Assert.True(update.IsDownloading);
            Assert.True(update.Progress.CanCancel);
            update.Abandon();
            return Task.CompletedTask;
        };

        await update.InstallCommand.ExecuteAsync(null);

        Assert.True(update.IsResult);
        Assert.Null(update.Error);
        Assert.Empty(_test.Dialogs.RestartsWith);
    }

    [Fact]
    public async Task A_folder_the_app_may_not_write_is_explained()
    {
        Source.Publish("0.3.0");
        var executable = Path.Combine(_test.AppFolder, FakeUpdateSource.Executable);
        File.SetAttributes(executable, FileAttributes.ReadOnly);
        try
        {
            var update = new UpdateViewModel(await StartAsync(), _test.Dialogs, known: null);
            await update.BeginAsync();

            Assert.False(update.CanInstall);
            Assert.Equal(BorgTexts.Format(Strings.UpdateBlockedNotWritable, _test.AppFolder), update.Blocker);
            Assert.Equal(new Uri("https://example.test/releases/v0.3.0"), update.PageUrl);
        }
        finally
        {
            File.SetAttributes(executable, FileAttributes.Normal);
        }
    }

    [Fact]
    public async Task The_next_start_tells_how_the_update_went()
    {
        var work = _test.Updates.WorkDirectory;
        Directory.CreateDirectory(work);
        File.WriteAllText(Path.Combine(work, UpdateApplier.OutcomeFile), """{ "Version": "0.3.0", "Succeeded": true, "Error": null }""");
        var viewModel = await StartAsync();

        viewModel.ShowUpdateOutcome();
        Assert.Equal(BorgTexts.Format(Strings.UpdateInstalledNotice, "0.3.0"), viewModel.Notice);

        File.WriteAllText(Path.Combine(work, UpdateApplier.OutcomeFile), """{ "Version": "0.3.0", "Succeeded": false, "Error": "disk full" }""");
        viewModel.ShowUpdateOutcome();
        Assert.Equal(BorgTexts.Format(Strings.UpdateFailedNotice, "0.3.0", "disk full"), viewModel.Notice);
    }

    [Theory]
    [InlineData("## Added\r\n- A\r\n* B\r\n\r\n### Downloads\n\n| a | b |", "Added\n• A\n• B")]
    [InlineData("Just text", "Just text")]
    public void Release_notes_drop_the_download_table(string notes, string expected) =>
        Assert.Equal(expected, UpdateViewModel.ReleaseNotes(notes));
}
