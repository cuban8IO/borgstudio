using BorgStudio.App.Resources;
using BorgStudio.App.ViewModels;
using BorgStudio.Core.Settings;
using BorgStudio.Tests.TestSupport;

namespace BorgStudio.Tests.ViewModels;

/// <summary>The "no warranty" notice: once when the app is first used, and every time a repository is created.</summary>
public sealed class WarrantyNoticeTests : IDisposable
{
    private const string Pin = "123456";

    private readonly TestServices _test = new();

    public void Dispose() => _test.Dispose();

    private FakeDialogs Dialogs => _test.Dialogs;

    private async Task<MainViewModel> StartAsync()
    {
        var main = new MainViewModel(_test.Services);
        await main.CheckBorgCommand.ExecuteAsync(null);
        return main;
    }

    [Fact]
    public async Task The_first_start_shows_the_notice_once()
    {
        var main = await StartAsync();

        await main.ShowWarrantyNoticeIfDueAsync();
        await main.ShowWarrantyNoticeIfDueAsync();

        Assert.Equal([Strings.WarrantyTitle], Dialogs.Shown);
        Assert.Equal([Strings.WarrantyMessage], Dialogs.Messages);
        Assert.True(_test.Settings.Load().WarrantyNoticeShown);

        // Next start: remembered.
        await (await StartAsync()).ShowWarrantyNoticeIfDueAsync();
        Assert.Single(Dialogs.Shown);
    }

    [Fact]
    public async Task Settings_of_an_older_version_get_the_notice_too()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_test.Settings.FilePath)!);
        File.WriteAllText(_test.Settings.FilePath, """{ "vaultAutoLockMinutes": 15, "language": "de", "checkForUpdates": true }""");
        Assert.False(_test.Settings.Load().WarrantyNoticeShown);

        var main = await StartAsync();
        await main.ShowWarrantyNoticeIfDueAsync();

        Assert.Equal([Strings.WarrantyTitle], Dialogs.Shown);
        // The other settings stay as they were.
        Assert.Equal("de", _test.Settings.Load().Language);
    }

    [Fact]
    public async Task With_a_pin_the_notice_waits_for_unlocking()
    {
        _test.Vault.SetUp(Pin);
        _test.Vault.Lock();
        var main = await StartAsync();
        Assert.True(main.IsLocked);

        await main.ShowWarrantyNoticeIfDueAsync();
        Assert.Empty(Dialogs.Shown);

        Assert.True(_test.Vault.Unlock(Pin));
        Assert.Equal([Strings.WarrantyTitle], Dialogs.Shown);

        // Locking and unlocking again does not repeat it.
        _test.Vault.Lock();
        Assert.True(_test.Vault.Unlock(Pin));
        Assert.Single(Dialogs.Shown);
    }

    private RepositoryEditorViewModel NewEditor(bool createNew)
    {
        var editor = new RepositoryEditorViewModel(_test.Services, FakeBorg.Installation);
        editor.SelectedProvider = editor.Providers.Single(option => option.Provider.Id == "local");
        editor.NextCommand.Execute(null);
        editor.Name = "Disk";
        editor.Fields.Single().Value = _test.Directory.Combine("repo");
        editor.StorageAsk = true;
        editor.CreateNew = createNew;
        editor.Passphrase = editor.PassphraseConfirmation = "secret";
        return editor;
    }

    [Fact]
    public async Task Every_new_repository_comes_with_the_notice()
    {
        // Already shown at the first start: creating still shows its own note, every time.
        _test.Settings.Save(new AppSettings { WarrantyNoticeShown = true });

        var first = NewEditor(createNew: true);
        await first.ConfirmCommand.ExecuteAsync(null);
        Assert.Empty(first.Errors);
        Assert.Equal([Strings.WarrantyTitle], Dialogs.Shown);
        Assert.Equal([Strings.WarrantyCreateMessage], Dialogs.Messages);

        var second = NewEditor(createNew: true);
        second.Name = "Disk 2";
        second.Fields.Single().Value = _test.Directory.Combine("repo2");
        await second.ConfirmCommand.ExecuteAsync(null);
        Assert.Equal(2, Dialogs.Shown.Count(title => title == Strings.WarrantyTitle));
    }

    [Fact]
    public async Task Connecting_or_invalid_input_shows_no_notice()
    {
        var connect = NewEditor(createNew: false);
        await connect.ConfirmCommand.ExecuteAsync(null);
        Assert.Empty(connect.Errors);

        var invalid = NewEditor(createNew: true);
        invalid.PassphraseConfirmation = "other";
        await invalid.ConfirmCommand.ExecuteAsync(null);
        Assert.NotEmpty(invalid.Errors);

        Assert.DoesNotContain(Strings.WarrantyTitle, Dialogs.Shown);
    }
}
