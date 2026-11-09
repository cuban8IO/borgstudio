using System.Globalization;
using BorgStudio.App.Services;
using BorgStudio.App.ViewModels;
using BorgStudio.Core.Settings;
using BorgStudio.Tests.TestSupport;

namespace BorgStudio.Tests.ViewModels;

/// <summary>Choosing the UI language: which one applies, saving the choice, restarting.</summary>
public sealed class LanguageTests : IDisposable
{
    private readonly TestServices _test = new();

    public void Dispose() => _test.Dispose();

    private static CultureInfo Culture(string name) => CultureInfo.GetCultureInfo(name);

    [Theory]
    [InlineData(null, "de-DE", "de-DE")]
    [InlineData(null, "de-AT", "de-AT")]
    [InlineData(null, "en-US", "en-US")]
    [InlineData(null, "fr-FR", "en")]
    [InlineData(null, "", "en")]
    [InlineData("en", "de-DE", "en")]
    [InlineData("de", "fr-FR", "de")]
    [InlineData("DE", "en-US", "de")]
    [InlineData("xx", "de-DE", "de-DE")]
    public void The_chosen_language_wins_then_the_system_language_then_English(string? setting, string system, string expected) =>
        Assert.Equal(expected, AppLanguages.Resolve(setting, Culture(system)).Name);

    [Fact]
    public void Neutral_drops_the_region() =>
        Assert.Equal("de", AppLanguages.Neutral(Culture("de-CH")));

    [Fact]
    public void An_old_settings_file_has_no_language()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_test.Settings.FilePath)!);
        File.WriteAllText(_test.Settings.FilePath, """{ "vaultAutoLockMinutes": 5 }""");
        Assert.Null(_test.Settings.Load().Language);

        _test.Settings.Save(new AppSettings { Language = "de" });
        Assert.Equal("de", _test.Settings.Load().Language);
    }

    [Fact]
    public async Task The_settings_save_the_language_and_offer_a_restart()
    {
        var viewModel = new MainViewModel(_test.Services);
        await viewModel.CheckBorgCommand.ExecuteAsync(null);
        var settings = new SettingsViewModel(viewModel, _test.Dialogs);

        Assert.Null(settings.SelectedLanguage.Value);
        Assert.False(settings.RestartNeeded);
        Assert.Equal(["en", "de"], settings.LanguageOptions.Skip(1).Select(option => option.Value));
        Assert.Equal(["English", "Deutsch"], settings.LanguageOptions.Skip(1).Select(option => option.Label));

        // The language the app does not show right now needs a restart; the one it shows does not.
        var shown = AppLanguages.Neutral(AppLanguages.Current);
        var other = settings.LanguageOptions.Skip(1).First(option => option.Value != shown);
        settings.SelectedLanguage = other;
        Assert.Equal(other.Value, _test.Settings.Load().Language);
        Assert.True(settings.RestartNeeded);

        settings.RestartCommand.Execute(null);
        Assert.Equal(1, _test.Dialogs.Restarts);

        settings.SelectedLanguage = settings.LanguageOptions[0];
        Assert.Null(_test.Settings.Load().Language);
        Assert.Equal(AppLanguages.NeedsRestart(null), settings.RestartNeeded);

        // Reopened, the window shows the saved choice.
        settings.SelectedLanguage = other;
        Assert.Equal(other, new SettingsViewModel(viewModel, _test.Dialogs).SelectedLanguage);
    }
}
