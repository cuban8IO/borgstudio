using BorgStudio.App.Resources;
using BorgStudio.App.ViewModels;
using BorgStudio.Core.Repositories;
using BorgStudio.Providers.BorgBase;
using BorgStudio.Tests.TestSupport;

namespace BorgStudio.Tests.ViewModels;

/// <summary>Adding a BorgBase repository: with an API token (fake API) or with the key added by hand.</summary>
public sealed class BorgBaseRepositoryFlowTests : IDisposable
{
    private readonly TestServices _test = new();

    // SSH keys (and the token) live in the vault.
    public BorgBaseRepositoryFlowTests() => _test.Vault.SetUp("123456");

    public void Dispose() => _test.Dispose();

    private RepositoryEditorViewModel NewEditor(string repo, string token = FakeBorgBase.Token)
    {
        var editor = new RepositoryEditorViewModel(_test.Services, FakeBorg.Installation);
        editor.SelectedProvider = editor.Providers.Single(option => option.Provider.Id == "borgbase");
        editor.NextCommand.Execute(null);
        editor.Name = "Laptop";
        Field(editor, "token").Value = token;
        Field(editor, "repo").Value = repo;
        editor.CreateNew = true;
        editor.Passphrase = editor.PassphraseConfirmation = "secret";
        editor.StorageAsk = true;
        // The fake server's key is not BorgBase's: confirmed after the warning.
        _test.Dialogs.ConfirmAnswers.Enqueue(true);
        return editor;
    }

    private static FieldViewModel Field(RepositoryEditorViewModel editor, string key) => editor.Fields.Single(field => field.Key == key);

    [Fact]
    public async Task With_a_token_the_repository_and_key_are_set_up_at_borgbase_without_a_password()
    {
        var editor = NewEditor("Laptop");
        Assert.True(editor.IsProvisioned);
        Assert.False(editor.ShowInstallOptions);
        Assert.True(Field(editor, "token").IsSecret);

        await editor.ConfirmCommand.ExecuteAsync(null);

        Assert.Empty(editor.Errors);
        Assert.Empty(_test.Ssh.Logins);
        Assert.Empty(_test.Dialogs.PublicKeys);
        var repo = Assert.Single(_test.BorgBaseApi.Repos);
        Assert.Equal("eu", repo.Region);
        Assert.Equal(["init", "info"], _test.Borg.Commands);
        Assert.Contains("ssh://abcd1@abcd1.repo.borgbase.com/./repo", _test.Borg.Calls[0].Arguments);

        var saved = editor.Result!.Repository;
        Assert.Equal("ssh://abcd1@abcd1.repo.borgbase.com/./repo", saved.ProviderValues[BorgBaseProvider.AddressKey]);
        Assert.Equal("r1", saved.ProviderValues[BorgBaseProvider.RepoIdKey]);
        Assert.DoesNotContain(BorgBaseProvider.TokenKey, saved.ProviderValues.Keys);
        Assert.Equal(FakeBorgBase.Token, _test.Vault.GetSecret(RepositorySecrets.Name(saved.Id, BorgBaseProvider.TokenKey)));
        Assert.False(saved.SshKeyRestricted);
        // The key BorgBase got is the one saved for the repository.
        Assert.Equal(_test.Vault.GetKey(saved.SshKeyId!.Value)!.PublicKey, _test.BorgBaseApi.Keys.Single().KeyData);

        // The next BorgBase repository suggests the token, and editing keeps BorgBase's values.
        _test.Store.Save([saved]);
        var next = new RepositoryEditorViewModel(_test.Services, FakeBorg.Installation);
        next.SelectedProvider = next.Providers.Single(option => option.Provider.Id == "borgbase");
        next.NextCommand.Execute(null);
        Assert.Equal(FakeBorgBase.Token, Field(next, "token").Value);

        var edit = new RepositoryEditorViewModel(_test.Services, FakeBorg.Installation, saved);
        Assert.Equal(FakeBorgBase.Token, Field(edit, "token").Value);
        edit.Name = "Laptop (BorgBase)";
        await edit.ConfirmCommand.ExecuteAsync(null);
        Assert.Equal("r1", edit.Result!.Repository.ProviderValues[BorgBaseProvider.RepoIdKey]);
    }

    [Fact]
    public async Task Without_a_token_the_key_is_shown_to_add_by_hand()
    {
        var editor = NewEditor("ssh://mmvz9gp4@mmvz9gp4.repo.borgbase.com/./repo", token: "");
        editor.CreateNew = false;
        _test.Borg.EncryptionMode = "repokey-blake2";

        await editor.ConfirmCommand.ExecuteAsync(null);

        Assert.Empty(editor.Errors);
        var (instructions, publicKey) = Assert.Single(_test.Dialogs.PublicKeys);
        Assert.Contains("BorgBase", instructions);
        Assert.StartsWith("ssh-ed25519 ", publicKey);
        Assert.Empty(_test.BorgBaseApi.Requests);
        Assert.Equal(["info"], _test.Borg.Commands);
        Assert.Equal(publicKey, _test.Vault.GetKey(editor.Result!.Repository.SshKeyId!.Value)!.PublicKey);
    }

    [Fact]
    public async Task Not_adding_the_key_saves_nothing()
    {
        _test.Dialogs.PublicKeyAdded = false;
        var editor = NewEditor("ssh://mmvz9gp4@mmvz9gp4.repo.borgbase.com/./repo", token: "");

        await editor.ConfirmCommand.ExecuteAsync(null);

        Assert.Equal(Strings.EditorAddKeyCancelled, Assert.Single(editor.Errors));
        Assert.Null(editor.Result);
        Assert.Empty(_test.Borg.Calls);
    }

    [Fact]
    public async Task A_name_without_a_token_asks_for_the_address()
    {
        var editor = NewEditor("Laptop", token: "");

        await editor.ConfirmCommand.ExecuteAsync(null);

        Assert.Contains("ssh://", Assert.Single(editor.Errors));
        Assert.Empty(_test.Dialogs.PublicKeys);
    }
}
