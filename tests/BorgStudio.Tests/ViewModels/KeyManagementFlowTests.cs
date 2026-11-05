using System.Diagnostics;
using BorgStudio.App.Resources;
using BorgStudio.App.ViewModels;
using BorgStudio.Core.Repositories;
using BorgStudio.Core.Security;
using BorgStudio.Core.Ssh;
using BorgStudio.Tests.TestSupport;

namespace BorgStudio.Tests.ViewModels;

/// <summary>The SSH keys window, importing keys and moving key files of older versions into the vault.</summary>
public sealed class KeyManagementFlowTests : IDisposable
{
    private const string Pin = "123456";

    private readonly TestServices _test = new();

    public void Dispose() => _test.Dispose();

    private FakeDialogs Dialogs => _test.Dialogs;
    private Vault Vault => _test.Vault;

    private RepositoryConfig SshRepository(string name, Guid? keyId = null, string? legacyFile = null, bool managed = false) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        ProviderId = "ssh",
        ProviderValues = new Dictionary<string, string>
        {
            ["host"] = "nas.local", ["port"] = "22", ["user"] = "backup", ["path"] = name, ["remoteBorg"] = "",
        },
        PassphraseMode = PassphraseMode.None,
        EncryptionMode = "none",
        SshKeyId = keyId,
        LegacySshKeyFile = legacyFile,
        LegacySshKeyManaged = managed,
    };

    private MainViewModel Start(params RepositoryConfig[] repositories)
    {
        _test.Store.Save(repositories);
        return new MainViewModel(_test.Services);
    }

    /// <summary>A key file the way older BorgStudio versions created it.</summary>
    private string OldKeyFile(out SshKeyPair pair)
    {
        pair = SshKeyPair.GenerateEd25519("borgstudio-old");
        Directory.CreateDirectory(_test.SshKeys.Directory);
        var file = Path.Combine(_test.SshKeys.Directory, "ab604488-20261104");
        File.WriteAllText(file, pair.PrivateKeyPem);
        File.WriteAllText(file + ".pub", pair.PublicKeyLine + "\n");
        return file;
    }

    [Fact]
    public void Without_a_pin_the_keys_window_asks_to_set_one_up()
    {
        var keys = new KeysViewModel(Start(), Dialogs);

        Assert.True(keys.NeedsPin);
        Assert.False(keys.GenerateCommand.CanExecute(null));
    }

    [Fact]
    public async Task Keys_are_created_renamed_copied_saved_and_deleted_when_unused()
    {
        Vault.SetUp(Pin);
        var main = Start();
        var keys = new KeysViewModel(main, Dialogs);
        Assert.True(keys.HasNoKeys);

        keys.NewKeyName = "Laptop";
        keys.GenerateCommand.Execute(null);
        var key = Assert.Single(Vault.Keys);
        Assert.Equal("Laptop", key.Name);
        Assert.Same(keys.SelectedKey, Assert.Single(keys.Keys));
        Assert.Equal(Strings.KeysUnused, keys.SelectedKey!.UsedByText);

        keys.EditName = "Work laptop";
        keys.RenameCommand.Execute(null);
        Assert.Equal("Work laptop", Vault.GetKey(key.Id)!.Name);

        await keys.CopyPublicKeyCommand.ExecuteAsync(null);
        Assert.Equal(key.PublicKey, Dialogs.Clipboard);

        Dialogs.ConfirmAnswers.Enqueue(true);
        Dialogs.SaveFile = _test.Directory.Combine("exported_key");
        await keys.SavePrivateKeyCommand.ExecuteAsync(null);
        Assert.Contains(Strings.KeysSavePrivateTitle, Dialogs.Shown);
        Assert.Equal(key.PrivateKey, File.ReadAllText(Dialogs.SaveFile));

        Dialogs.ConfirmAnswers.Enqueue(true);
        await keys.DeleteCommand.ExecuteAsync(null);
        Assert.Empty(Vault.Keys);
        Assert.True(keys.HasNoKeys);
    }

    [Fact]
    public void A_key_in_use_shows_its_repositories_and_cannot_be_deleted()
    {
        Vault.SetUp(Pin);
        var key = SshKeyEntry.Generate("NAS", "c", DateTimeOffset.Now);
        Vault.SetKey(key);
        var keys = new KeysViewModel(Start(SshRepository("one", key.Id), SshRepository("two", key.Id)), Dialogs);

        Assert.Equal("one, two", keys.SelectedKey!.UsedByText);
        Assert.False(keys.DeleteCommand.CanExecute(null));
    }

    [SshKeygenFact]
    public async Task Importing_asks_for_the_password_of_a_protected_key_and_refuses_duplicates()
    {
        Vault.SetUp(Pin);
        var file = _test.Directory.Combine("id_work");
        using (var process = Process.Start(new ProcessStartInfo(SshKeygen.Find()!)
               {
                   ArgumentList = { "-q", "-t", "ed25519", "-N", "pw", "-C", "work", "-f", file },
                   RedirectStandardOutput = true,
                   RedirectStandardError = true,
               })!)
            process.WaitForExit();
        var keys = new KeysViewModel(Start(), Dialogs);
        Dialogs.OpenFile = file;
        Dialogs.Secrets.Enqueue("wrong");
        Dialogs.Secrets.Enqueue("pw");

        await keys.ImportCommand.ExecuteAsync(null);

        Assert.Equal([null, Strings.KeysPasswordWrong], Dialogs.SecretErrors);
        var key = Assert.Single(Vault.Keys);
        Assert.Equal("id_work", key.Name);
        Assert.False(key.Generated);
        Assert.Equal(string.Format(System.Globalization.CultureInfo.CurrentCulture, Strings.KeysImported, "id_work"), keys.Message);

        Dialogs.Secrets.Enqueue("pw");
        await keys.ImportCommand.ExecuteAsync(null);
        Assert.Single(Vault.Keys);
        Assert.Equal(string.Format(System.Globalization.CultureInfo.CurrentCulture, Strings.KeysAlreadyImported, "id_work"), keys.Message);
    }

    [SshKeygenFact]
    public async Task The_editor_imports_a_key_file_and_selects_it()
    {
        Vault.SetUp(Pin);
        var pair = SshKeyPair.GenerateEd25519("me@laptop");
        var file = _test.Directory.Combine("id_ed25519");
        File.WriteAllText(file, pair.PrivateKeyPem);
        var editor = new RepositoryEditorViewModel(_test.Services, FakeBorg.Installation);
        editor.SelectedProvider = editor.Providers.Single(option => option.Provider.Id == "ssh");
        editor.NextCommand.Execute(null);
        Dialogs.OpenFile = file;

        await editor.ImportKeyCommand.ExecuteAsync(null);

        Assert.Empty(editor.Errors);
        Assert.True(editor.KeyChoiceExisting);
        Assert.Equal("id_ed25519", editor.SelectedKey!.Key.Name);
        Assert.Equal(editor.SelectedKey.Key.Id, Assert.Single(Vault.Keys).Id);
    }

    [Fact]
    public async Task Unlocking_moves_old_key_files_into_the_vault()
    {
        var file = OldKeyFile(out var pair);
        Vault.SetUp(Pin);
        Vault.Lock();
        var main = Start(SshRepository("Box", legacyFile: file, managed: true));
        Assert.Equal(Strings.SshKeyLegacy, main.SelectedRepository!.SshKeyText);

        await main.TestRepositoryCommand.ExecuteAsync(null);
        Assert.Equal(Strings.SshKeyLegacyNeedsPin, main.SelectedRepository.ResultText);

        main.LockScreenPin = Pin;
        await main.UnlockCommand.ExecuteAsync(null);
        await main.MigrateLegacyKeysAsync();

        var repository = Assert.Single(_test.Store.Load().Repositories);
        Assert.False(repository.HasLegacySshKey);
        var key = Vault.GetKey(repository.SshKeyId!.Value)!;
        Assert.Equal(pair.PrivateKeyPem, key.PrivateKey);
        Assert.False(File.Exists(file));
        Assert.False(File.Exists(file + ".pub"));
        Assert.Equal("Box", main.SelectedRepository.SshKeyText);
        Assert.Equal(string.Format(System.Globalization.CultureInfo.CurrentCulture, Strings.KeysMigrated, 1), main.Notice);
    }

    [Fact]
    public async Task Without_a_pin_old_key_files_lead_to_setting_one_up()
    {
        var file = OldKeyFile(out _);
        var main = Start(SshRepository("Box", legacyFile: file, managed: true));
        Dialogs.NewPins.Enqueue(Pin);

        await main.OfferVaultForLegacyKeysAsync();
        await main.MigrateLegacyKeysAsync();

        Assert.Equal([Strings.KeysMigrationTitle], Dialogs.Shown);
        Assert.Equal(VaultState.Unlocked, Vault.State);
        Assert.Single(Vault.Keys);
        Assert.False(Assert.Single(_test.Store.Load().Repositories).HasLegacySshKey);
        Assert.False(File.Exists(file));
    }

    [Fact]
    public async Task Nothing_is_offered_without_old_key_files()
    {
        var main = Start(SshRepository("New"));

        await main.OfferVaultForLegacyKeysAsync();

        Assert.Empty(Dialogs.Shown);
        Assert.Equal(VaultState.NotSetUp, Vault.State);
    }
}
