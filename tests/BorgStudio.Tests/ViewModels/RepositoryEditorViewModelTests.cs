using BorgStudio.App.Resources;
using BorgStudio.App.ViewModels;
using BorgStudio.Core.Borg;
using BorgStudio.Core.Repositories;
using BorgStudio.Tests.TestSupport;

namespace BorgStudio.Tests.ViewModels;

public sealed class RepositoryEditorViewModelTests : IDisposable
{
    private readonly TestServices _test = new();

    public void Dispose() => _test.Dispose();

    private FakeBorg Borg => _test.Borg;

    /// <summary>An add dialog with the local provider picked.</summary>
    private RepositoryEditorViewModel NewEditor(TestServices? services = null, bool withBorg = true)
    {
        var test = services ?? _test;
        var editor = new RepositoryEditorViewModel(test.Services, withBorg ? FakeBorg.Installation : null);
        editor.SelectedProvider = editor.Providers.Single(option => option.Provider.Id == "local");
        editor.NextCommand.Execute(null);
        return editor;
    }

    private void Fill(RepositoryEditorViewModel editor, string name = "External disk")
    {
        editor.Name = name;
        editor.Fields.Single().Value = _test.Directory.Combine("repo");
    }

    [Fact]
    public void Starts_with_the_provider_choice_and_goes_on_to_the_details()
    {
        var editor = new RepositoryEditorViewModel(_test.Services, FakeBorg.Installation);
        Assert.True(editor.IsProviderPage);
        Assert.Equal(["local", "ssh", "hetzner-storage-box"], editor.Providers.Select(option => option.Provider.Id));

        editor.SelectedProvider = editor.Providers[0];
        editor.NextCommand.Execute(null);

        Assert.True(editor.IsDetailsPage);
        Assert.True(editor.CanGoBack);
        Assert.False(editor.IsSsh);
        Assert.Equal("path", Assert.Single(editor.Fields).Key);
        Assert.Equal(Strings.EditorConnect, editor.ConfirmText);
    }

    [Fact]
    public async Task Invalid_input_is_reported_without_running_borg()
    {
        var editor = NewEditor();
        editor.CreateNew = true;
        editor.Fields.Single().Value = "relative/path";
        editor.Passphrase = "one";
        editor.PassphraseConfirmation = "two";

        await editor.ConfirmCommand.ExecuteAsync(null);

        Assert.Equal(3, editor.Errors.Count);
        Assert.Contains(Strings.EditorNameRequired, editor.Errors);
        Assert.Contains(Strings.EditorPassphraseMismatch, editor.Errors);
        Assert.Empty(Borg.Calls);
        Assert.Null(editor.Result);
    }

    [Fact]
    public async Task Creates_an_encrypted_repository_and_stores_the_passphrase()
    {
        var editor = NewEditor();
        Fill(editor);
        editor.CreateNew = true;
        editor.Passphrase = editor.PassphraseConfirmation = "secret";
        editor.StorePassphrase = true;
        var closed = false;
        editor.CloseRequested += (_, _) => closed = true;

        await editor.ConfirmCommand.ExecuteAsync(null);

        Assert.Empty(editor.Errors);
        Assert.True(closed);
        Assert.Equal(["init", "info"], Borg.Commands);
        Assert.Contains("--encryption=repokey-blake2", Borg.Calls[0].Arguments);
        Assert.All(Borg.Calls, call => Assert.Equal("secret", call.Passphrase));
        Assert.All(Borg.Environments, environment => Assert.False(environment.ContainsKey("BORG_RSH")));

        var result = editor.Result!;
        Assert.True(result.Created);
        Assert.Equal("External disk", result.Repository.Name);
        Assert.Equal(PassphraseMode.Stored, result.Repository.PassphraseMode);
        Assert.Equal("repokey-blake2", result.Repository.EncryptionMode);
        Assert.Equal("repo-id-1", result.Repository.BorgRepositoryId);
        Assert.Null(result.Repository.SshKeyFile);
        Assert.Equal("secret", _test.Secrets.Secrets[result.Repository.SecretKey]);
    }

    [Fact]
    public async Task Unencrypted_repositories_have_no_passphrase()
    {
        Borg.EncryptionMode = "none";
        var editor = NewEditor();
        Fill(editor);
        editor.CreateNew = true;
        editor.SelectedEncryption = editor.EncryptionOptions.Single(option => option.Value == BorgEncryption.None);

        Assert.False(editor.UsesPassphrase);
        await editor.ConfirmCommand.ExecuteAsync(null);

        Assert.Empty(editor.Errors);
        Assert.Contains("--encryption=none", Borg.Calls[0].Arguments);
        Assert.Equal(PassphraseMode.None, editor.Result!.Repository.PassphraseMode);
        Assert.Empty(_test.Secrets.Secrets);
    }

    [Fact]
    public async Task Connecting_with_a_wrong_passphrase_keeps_the_dialog_open()
    {
        Borg.CorrectPassphrase = "right";
        var editor = NewEditor();
        Fill(editor);
        editor.Passphrase = "wrong";

        await editor.ConfirmCommand.ExecuteAsync(null);

        Assert.Equal(Strings.BorgErrorPassphraseWrong, Assert.Single(editor.Errors));
        Assert.Null(editor.Result);
        Assert.Equal(["info"], Borg.Commands);
    }

    [Fact]
    public async Task Without_a_keychain_the_passphrase_is_asked_for_every_time()
    {
        using var noKeychain = new TestServices(keychainAvailable: false);
        var editor = NewEditor(noKeychain);
        editor.Name = "Disk";
        editor.Fields.Single().Value = noKeychain.Directory.Combine("repo");
        editor.Passphrase = "secret";

        Assert.False(editor.StorePassphrase);
        Assert.True(editor.KeychainUnavailable);
        await editor.ConfirmCommand.ExecuteAsync(null);

        Assert.Equal(PassphraseMode.Ask, editor.Result!.Repository.PassphraseMode);
    }

    [Fact]
    public async Task Needs_a_supported_borg_and_refuses_borg2()
    {
        var withoutBorg = NewEditor(withBorg: false);
        Fill(withoutBorg);
        await withoutBorg.ConfirmCommand.ExecuteAsync(null);
        Assert.Equal(Strings.BorgNotReady, Assert.Single(withoutBorg.Errors));

        var borg2 = NewEditor();
        Fill(borg2);
        borg2.SelectedBorgVersion = borg2.BorgVersionOptions.Single(option => option.Value == BorgVersionPreference.Borg2);
        await borg2.ConfirmCommand.ExecuteAsync(null);
        Assert.Equal(Strings.Borg2NotSupported, Assert.Single(borg2.Errors));

        Assert.Empty(Borg.Calls);
    }

    [Fact]
    public async Task Editing_switches_between_stored_and_asked_passphrases()
    {
        var existing = new RepositoryConfig
        {
            Id = Guid.NewGuid(),
            Name = "Old name",
            ProviderId = "local",
            ProviderValues = new Dictionary<string, string> { ["path"] = _test.Directory.Combine("repo") },
            PassphraseMode = PassphraseMode.Stored,
            EncryptionMode = "repokey-blake2",
        };
        _test.Secrets.Secrets[existing.SecretKey] = "secret";

        var toAsk = new RepositoryEditorViewModel(_test.Services, FakeBorg.Installation, existing);
        Assert.Equal("Old name", toAsk.Name);
        Assert.True(toAsk.StorePassphrase);
        Assert.False(toAsk.IsSsh);
        toAsk.Name = "New name";
        toAsk.StorePassphrase = false;
        await toAsk.ConfirmCommand.ExecuteAsync(null);

        var asked = toAsk.Result!.Repository;
        Assert.Equal("New name", asked.Name);
        Assert.Equal(PassphraseMode.Ask, asked.PassphraseMode);
        Assert.Empty(_test.Secrets.Secrets);
        Assert.Empty(Borg.Calls);

        var toStored = new RepositoryEditorViewModel(_test.Services, FakeBorg.Installation, asked);
        toStored.StorePassphrase = true;
        await toStored.ConfirmCommand.ExecuteAsync(null);
        Assert.Equal(Strings.EditorPassphraseRequiredToStore, Assert.Single(toStored.Errors));

        toStored.Passphrase = "new secret";
        await toStored.ConfirmCommand.ExecuteAsync(null);
        Assert.Equal(PassphraseMode.Stored, toStored.Result!.Repository.PassphraseMode);
        Assert.Equal("new secret", _test.Secrets.Secrets[existing.SecretKey]);
    }
}
