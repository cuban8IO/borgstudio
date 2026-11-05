using BorgStudio.App.Resources;
using BorgStudio.App.ViewModels;
using BorgStudio.Core.Repositories;
using BorgStudio.Core.Security;
using BorgStudio.Tests.TestSupport;

namespace BorgStudio.Tests.ViewModels;

/// <summary>Passphrases in the PIN-protected vault: adding, using, moving, removing, locking.</summary>
public sealed class VaultFlowTests : IDisposable
{
    private const string Pin = "123456";

    private readonly TestServices _test = new();

    public void Dispose() => _test.Dispose();

    private FakeBorg Borg => _test.Borg;
    private FakeDialogs Dialogs => _test.Dialogs;
    private Vault Vault => _test.Vault;

    private RepositoryEditorViewModel NewEditor()
    {
        var editor = new RepositoryEditorViewModel(_test.Services, FakeBorg.Installation);
        editor.SelectedProvider = editor.Providers.Single(option => option.Provider.Id == "local");
        editor.NextCommand.Execute(null);
        editor.Name = "Disk";
        editor.Fields.Single().Value = _test.Directory.Combine("repo");
        editor.CreateNew = true;
        editor.Passphrase = editor.PassphraseConfirmation = "secret";
        return editor;
    }

    private RepositoryConfig Saved(PassphraseMode mode) => new()
    {
        Id = Guid.NewGuid(),
        Name = "Disk",
        ProviderId = "local",
        ProviderValues = new Dictionary<string, string> { ["path"] = _test.Directory.Combine("repo") },
        PassphraseMode = mode,
        EncryptionMode = "repokey-blake2",
        BorgRepositoryId = "repo-id-1",
    };

    private async Task<MainViewModel> StartAsync(params RepositoryConfig[] repositories)
    {
        _test.Store.Save(repositories);
        var viewModel = new MainViewModel(_test.Services);
        await viewModel.CheckBorgCommand.ExecuteAsync(null);
        Borg.Calls.Clear();
        return viewModel;
    }

    [Fact]
    public async Task New_repositories_keep_the_passphrase_in_the_vault_after_setting_up_a_pin()
    {
        var editor = NewEditor();
        Assert.True(editor.StorageVault);
        Dialogs.NewPins.Enqueue(Pin);

        await editor.ConfirmCommand.ExecuteAsync(null);

        Assert.Empty(editor.Errors);
        var repository = editor.Result!.Repository;
        Assert.Equal(PassphraseMode.Vault, repository.PassphraseMode);
        Assert.Equal(VaultState.Unlocked, Vault.State);
        Assert.Equal("secret", Vault.GetPassphrase(repository.Id));
        Assert.Empty(_test.Secrets.Secrets);
    }

    [Fact]
    public async Task Not_setting_up_a_pin_changes_nothing()
    {
        var editor = NewEditor();

        await editor.ConfirmCommand.ExecuteAsync(null);

        Assert.Equal(Strings.VaultNotUnlocked, Assert.Single(editor.Errors));
        Assert.Null(editor.Result);
        Assert.Empty(Borg.Calls);
        Assert.Equal(VaultState.NotSetUp, Vault.State);
    }

    [Fact]
    public async Task A_locked_vault_asks_for_the_pin_until_it_is_right()
    {
        Vault.SetUp(Pin);
        var editor = NewEditor();
        Vault.Lock();
        Dialogs.Pins.Enqueue("000000");
        Dialogs.Pins.Enqueue(Pin);

        await editor.ConfirmCommand.ExecuteAsync(null);

        Assert.Empty(editor.Errors);
        Assert.Equal([null, Strings.PinWrong], Dialogs.PinErrors);
        Assert.Equal("secret", Vault.GetPassphrase(editor.Result!.Repository.Id));
    }

    [Fact]
    public async Task Testing_takes_the_passphrase_from_the_vault()
    {
        var repository = Saved(PassphraseMode.Vault);
        Vault.SetUp(Pin);
        Vault.SetPassphrase(repository.Id, "from vault");
        Vault.Lock();
        var viewModel = await StartAsync(repository);
        Assert.Equal(Strings.VaultStatusLocked, viewModel.VaultStatus);

        Dialogs.Pins.Enqueue(Pin);
        await viewModel.TestRepositoryCommand.ExecuteAsync(null);

        Assert.Equal("from vault", Assert.Single(Borg.Calls).Passphrase);
        Assert.True(viewModel.IsVaultUnlocked);
        Assert.Equal(Strings.VaultStatusUnlocked, viewModel.VaultStatus);

        // Unlocked: no second PIN prompt.
        await viewModel.TestRepositoryCommand.ExecuteAsync(null);
        Assert.Single(Dialogs.PinErrors);
    }

    [Fact]
    public async Task Cancelling_the_pin_runs_nothing()
    {
        var repository = Saved(PassphraseMode.Vault);
        Vault.SetUp(Pin);
        Vault.Lock();
        var viewModel = await StartAsync(repository);

        await viewModel.TestRepositoryCommand.ExecuteAsync(null);

        Assert.Empty(Borg.Calls);
        Assert.Empty(Dialogs.Passphrases);
    }

    [Fact]
    public async Task A_passphrase_missing_from_the_vault_is_asked_for()
    {
        var repository = Saved(PassphraseMode.Vault);
        Vault.SetUp(Pin);
        var viewModel = await StartAsync(repository);
        Dialogs.Passphrases.Enqueue("typed");

        await viewModel.TestRepositoryCommand.ExecuteAsync(null);

        Assert.Equal("typed", Assert.Single(Borg.Calls).Passphrase);
    }

    [Fact]
    public async Task Editing_moves_the_passphrase_between_keychain_and_vault()
    {
        var keychain = Saved(PassphraseMode.Stored);
        _test.Secrets.Secrets[keychain.SecretKey] = "secret";
        Vault.SetUp(Pin);

        var toVault = new RepositoryEditorViewModel(_test.Services, FakeBorg.Installation, keychain);
        Assert.True(toVault.StorageKeychain);
        toVault.StorageVault = true;
        await toVault.ConfirmCommand.ExecuteAsync(null);

        var inVault = toVault.Result!.Repository;
        Assert.Equal(PassphraseMode.Vault, inVault.PassphraseMode);
        Assert.Equal("secret", Vault.GetPassphrase(inVault.Id));
        Assert.Empty(_test.Secrets.Secrets);

        var toAsk = new RepositoryEditorViewModel(_test.Services, FakeBorg.Installation, inVault);
        toAsk.StorageAsk = true;
        await toAsk.ConfirmCommand.ExecuteAsync(null);

        Assert.Equal(PassphraseMode.Ask, toAsk.Result!.Repository.PassphraseMode);
        Assert.Null(Vault.GetPassphrase(inVault.Id));
        Assert.Empty(Borg.Calls);
    }

    [Fact]
    public async Task Removing_while_locked_drops_the_passphrase_at_the_next_unlock()
    {
        var repository = Saved(PassphraseMode.Vault);
        Vault.SetUp(Pin);
        Vault.SetPassphrase(repository.Id, "secret");
        Vault.Lock();
        var viewModel = await StartAsync(repository);
        Dialogs.ConfirmAnswers.Enqueue(true);

        await viewModel.RemoveRepositoryCommand.ExecuteAsync(null);
        Assert.Empty(viewModel.Repositories);
        Assert.Empty(Dialogs.PinErrors);

        Assert.True(Vault.Unlock(Pin));
        Assert.Null(Vault.GetPassphrase(repository.Id));
    }

    [Fact]
    public async Task The_vault_locks_itself_after_the_configured_idle_time()
    {
        Vault.SetUp(Pin);
        var viewModel = await StartAsync();
        Assert.Equal(15, viewModel.Settings.VaultAutoLockMinutes);

        _test.Time.Advance(TimeSpan.FromMinutes(14));
        viewModel.CheckVaultIdle();
        Assert.True(viewModel.IsVaultUnlocked);

        _test.Time.Advance(TimeSpan.FromMinutes(2));
        viewModel.CheckVaultIdle();
        Assert.False(viewModel.IsVaultUnlocked);
        Assert.False(viewModel.LockVaultCommand.CanExecute(null));
    }

    [Fact]
    public async Task Auto_lock_can_be_switched_off()
    {
        Vault.SetUp(Pin);
        var viewModel = await StartAsync();
        var settings = new SettingsViewModel(viewModel, Dialogs);

        settings.AutoLock = false;

        Assert.Equal(0, _test.Settings.Load().VaultAutoLockMinutes);
        _test.Time.Advance(TimeSpan.FromHours(5));
        viewModel.CheckVaultIdle();
        Assert.True(viewModel.IsVaultUnlocked);

        settings.AutoLock = true;
        settings.SelectedAutoLockMinutes = settings.AutoLockOptions.Single(option => option.Value == 5);
        Assert.Equal(5, _test.Settings.Load().VaultAutoLockMinutes);
    }

    [Fact]
    public async Task The_settings_set_up_change_and_reset_the_pin()
    {
        var viewModel = await StartAsync();
        var settings = new SettingsViewModel(viewModel, Dialogs);
        Assert.True(settings.IsNotSetUp);
        Assert.False(viewModel.IsVaultSetUp);

        Dialogs.NewPins.Enqueue(Pin);
        await settings.SetUpPinCommand.ExecuteAsync(null);
        Assert.True(settings.IsUnlocked);
        Assert.True(viewModel.IsVaultSetUp);
        Assert.Equal(Strings.VaultSetUpDone, settings.Message);

        Dialogs.Pins.Enqueue("000000");
        Dialogs.NewPins.Enqueue("abcdefgh");
        await settings.ChangePinCommand.ExecuteAsync(null);
        Assert.Equal(Strings.PinWrong, settings.Message);

        Dialogs.Pins.Enqueue(Pin);
        Dialogs.NewPins.Enqueue("abcdefgh");
        await settings.ChangePinCommand.ExecuteAsync(null);
        Assert.Equal(Strings.PinChanged, settings.Message);

        settings.LockVaultCommand.Execute(null);
        Assert.False(settings.IsUnlocked);
        Assert.True(Vault.Unlock("abcdefgh"));

        await settings.ResetVaultCommand.ExecuteAsync(null);
        Assert.True(settings.IsSetUp);

        Dialogs.ConfirmAnswers.Enqueue(true);
        await settings.ResetVaultCommand.ExecuteAsync(null);
        Assert.Equal(VaultState.NotSetUp, Vault.State);
        Assert.True(settings.IsNotSetUp);
        Assert.Equal(Strings.VaultResetDone, settings.Message);
        settings.Detach();
    }
}
