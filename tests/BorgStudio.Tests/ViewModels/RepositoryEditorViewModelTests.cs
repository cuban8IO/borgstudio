using BorgStudio.App.Resources;
using BorgStudio.App.ViewModels;
using BorgStudio.Core.Borg;
using BorgStudio.Core.Plugins;
using BorgStudio.Core.Repositories;
using BorgStudio.Providers.Local;
using BorgStudio.Tests.TestSupport;

namespace BorgStudio.Tests.ViewModels;

public sealed class RepositoryEditorViewModelTests : IDisposable
{
    private readonly TemporaryDirectory _directory = new();
    private readonly FakeBorg _borg = new();
    private readonly FakeSecretStore _secrets = new();
    private readonly PluginCatalog _plugins = PluginCatalog.Load([new LocalProviderPlugin()], "no-plugins-here");

    public void Dispose() => _directory.Dispose();

    private RepositoryEditorViewModel NewEditor(BorgInstallation? borg = null) =>
        new(_plugins, new BorgClient(_borg), _secrets, borg ?? FakeBorg.Installation);

    private void Fill(RepositoryEditorViewModel editor, string name = "External disk", string? path = null)
    {
        editor.Name = name;
        editor.Fields.Single().Value = path ?? _directory.Combine("repo");
    }

    [Fact]
    public void With_only_one_provider_it_starts_with_the_details()
    {
        var editor = NewEditor();

        Assert.True(editor.IsDetailsPage);
        Assert.False(editor.CanGoBack);
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
        Assert.Empty(_borg.Calls);
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
        Assert.Equal(["init", "info"], _borg.Commands);
        Assert.Contains("--encryption=repokey-blake2", _borg.Calls[0].Arguments);
        Assert.All(_borg.Calls, call => Assert.Equal("secret", call.Passphrase));

        var result = editor.Result!;
        Assert.True(result.Created);
        Assert.Equal("External disk", result.Repository.Name);
        Assert.Equal(PassphraseMode.Stored, result.Repository.PassphraseMode);
        Assert.Equal("repokey-blake2", result.Repository.EncryptionMode);
        Assert.Equal("repo-id-1", result.Repository.BorgRepositoryId);
        Assert.Equal("secret", _secrets.Secrets[result.Repository.SecretKey]);
    }

    [Fact]
    public async Task Unencrypted_repositories_have_no_passphrase()
    {
        _borg.EncryptionMode = "none";
        var editor = NewEditor();
        Fill(editor);
        editor.CreateNew = true;
        editor.SelectedEncryption = editor.EncryptionOptions.Single(option => option.Value == BorgEncryption.None);

        Assert.False(editor.UsesPassphrase);
        await editor.ConfirmCommand.ExecuteAsync(null);

        Assert.Empty(editor.Errors);
        Assert.Contains("--encryption=none", _borg.Calls[0].Arguments);
        Assert.Equal(PassphraseMode.None, editor.Result!.Repository.PassphraseMode);
        Assert.Empty(_secrets.Secrets);
    }

    [Fact]
    public async Task Connecting_with_a_wrong_passphrase_keeps_the_dialog_open()
    {
        _borg.CorrectPassphrase = "right";
        var editor = NewEditor();
        Fill(editor);
        editor.Passphrase = "wrong";

        await editor.ConfirmCommand.ExecuteAsync(null);

        Assert.Equal(Strings.BorgErrorPassphraseWrong, Assert.Single(editor.Errors));
        Assert.Null(editor.Result);
        Assert.Equal(["info"], _borg.Commands);
    }

    [Fact]
    public async Task Without_a_keychain_the_passphrase_is_asked_for_every_time()
    {
        var editor = new RepositoryEditorViewModel(_plugins, new BorgClient(_borg), new FakeSecretStore(available: false),
            FakeBorg.Installation);
        Fill(editor);
        editor.Passphrase = "secret";

        Assert.False(editor.StorePassphrase);
        Assert.True(editor.KeychainUnavailable);
        await editor.ConfirmCommand.ExecuteAsync(null);

        Assert.Equal(PassphraseMode.Ask, editor.Result!.Repository.PassphraseMode);
    }

    [Fact]
    public async Task Needs_a_supported_borg_and_refuses_borg2()
    {
        var withoutBorg = new RepositoryEditorViewModel(_plugins, new BorgClient(_borg), _secrets, borg: null);
        Fill(withoutBorg);
        await withoutBorg.ConfirmCommand.ExecuteAsync(null);
        Assert.Equal(Strings.BorgNotReady, Assert.Single(withoutBorg.Errors));

        var borg2 = NewEditor();
        Fill(borg2);
        borg2.SelectedBorgVersion = borg2.BorgVersionOptions.Single(option => option.Value == BorgVersionPreference.Borg2);
        await borg2.ConfirmCommand.ExecuteAsync(null);
        Assert.Equal(Strings.Borg2NotSupported, Assert.Single(borg2.Errors));

        Assert.Empty(_borg.Calls);
    }

    [Fact]
    public async Task Editing_switches_between_stored_and_asked_passphrases()
    {
        var existing = new RepositoryConfig
        {
            Id = Guid.NewGuid(),
            Name = "Old name",
            ProviderId = "local",
            ProviderValues = new Dictionary<string, string> { ["path"] = _directory.Combine("repo") },
            PassphraseMode = PassphraseMode.Stored,
            EncryptionMode = "repokey-blake2",
        };
        _secrets.Secrets[existing.SecretKey] = "secret";

        var toAsk = new RepositoryEditorViewModel(_plugins, new BorgClient(_borg), _secrets, FakeBorg.Installation, existing);
        Assert.Equal("Old name", toAsk.Name);
        Assert.True(toAsk.StorePassphrase);
        toAsk.Name = "New name";
        toAsk.StorePassphrase = false;
        await toAsk.ConfirmCommand.ExecuteAsync(null);

        var asked = toAsk.Result!.Repository;
        Assert.Equal("New name", asked.Name);
        Assert.Equal(PassphraseMode.Ask, asked.PassphraseMode);
        Assert.Empty(_secrets.Secrets);
        Assert.Empty(_borg.Calls);

        var toStored = new RepositoryEditorViewModel(_plugins, new BorgClient(_borg), _secrets, FakeBorg.Installation, asked);
        toStored.StorePassphrase = true;
        await toStored.ConfirmCommand.ExecuteAsync(null);
        Assert.Equal(Strings.EditorPassphraseRequiredToStore, Assert.Single(toStored.Errors));

        toStored.Passphrase = "new secret";
        await toStored.ConfirmCommand.ExecuteAsync(null);
        Assert.Equal(PassphraseMode.Stored, toStored.Result!.Repository.PassphraseMode);
        Assert.Equal("new secret", _secrets.Secrets[existing.SecretKey]);
    }
}
