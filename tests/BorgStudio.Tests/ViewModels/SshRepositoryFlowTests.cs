using BorgStudio.App.Resources;
using BorgStudio.App.ViewModels;
using BorgStudio.Core.Repositories;
using BorgStudio.Core.Ssh;
using BorgStudio.Tests.TestSupport;

namespace BorgStudio.Tests.ViewModels;

/// <summary>Adding, editing and using SSH repositories, with fake borg and a fake SSH server.</summary>
public sealed class SshRepositoryFlowTests : IDisposable
{
    private readonly TestServices _test = new();

    // SSH keys live in the vault.
    public SshRepositoryFlowTests() => _test.Vault.SetUp("123456");

    public void Dispose() => _test.Dispose();

    private RepositoryEditorViewModel NewSshEditor(IReadOnlyList<RepositoryConfig>? others = null)
    {
        var editor = new RepositoryEditorViewModel(_test.Services, FakeBorg.Installation, otherRepositories: others);
        editor.SelectedProvider = editor.Providers.Single(option => option.Provider.Id == "ssh");
        editor.NextCommand.Execute(null);
        editor.Name = "NAS";
        Field(editor, "host").Value = "nas.local";
        Field(editor, "port").Value = "2222";
        Field(editor, "user").Value = "backup";
        Field(editor, "path").Value = "borg/laptop";
        editor.CreateNew = true;
        editor.Passphrase = editor.PassphraseConfirmation = "secret";
        editor.StorageAsk = true;
        editor.ServerPassword = "server-password";
        return editor;
    }

    private static FieldViewModel Field(RepositoryEditorViewModel editor, string key) => editor.Fields.Single(field => field.Key == key);

    private RepositoryConfig SavedSshRepository(Guid? keyId, string path = "borg/laptop", bool restricted = true) => new()
    {
        Id = Guid.NewGuid(),
        Name = "NAS " + path,
        ProviderId = "ssh",
        ProviderValues = new Dictionary<string, string>
        {
            ["host"] = "nas.local", ["port"] = "2222", ["user"] = "backup", ["path"] = path, ["remoteBorg"] = "",
        },
        PassphraseMode = PassphraseMode.None,
        EncryptionMode = "none",
        SshKeyId = keyId,
        SshKeyRestricted = restricted,
    };

    [Fact]
    public async Task Creating_confirms_the_host_installs_a_restricted_key_and_keeps_it_in_the_vault()
    {
        var editor = NewSshEditor();
        Assert.True(editor.IsSsh);
        Assert.True(editor.KeyChoiceGenerate);
        Assert.True(editor.RestrictKey);
        _test.Dialogs.ConfirmAnswers.Enqueue(true);

        await editor.ConfirmCommand.ExecuteAsync(null);

        Assert.Empty(editor.Errors);
        Assert.Equal([Strings.HostKeyTitle], _test.Dialogs.Shown);
        Assert.True(FakeSsh.ServerKey.SameAs(Assert.Single(_test.KnownHosts.Find("nas.local", 2222))));

        var repository = editor.Result!.Repository;
        var key = _test.Vault.GetKey(repository.SshKeyId!.Value)!;
        Assert.Equal("NAS", key.Name);
        Assert.True(key.Generated);
        Assert.True(repository.SshKeyRestricted);

        var login = Assert.Single(_test.Ssh.Logins);
        Assert.Equal("server-password", login.Password);
        Assert.Contains($"command=\"borg serve --restrict-to-repository borg/laptop\",restrict {key.PublicKey}'", login.Command);

        // borg got the key as a temporary file that is gone afterwards; no key file lies around.
        Assert.Equal(["init", "info"], _test.Borg.Commands);
        Assert.All(_test.Borg.KeysDuringCalls, content => Assert.Equal(key.PrivateKey, content));
        Assert.All(_test.Borg.KeyFiles, file => Assert.False(File.Exists(file)));
        Assert.All(_test.Borg.Environments, environment =>
            Assert.Contains($"UserKnownHostsFile='{_test.SshKeys.KnownHostsFile}'", environment["BORG_RSH"]));
        Assert.Empty(Directory.GetFiles(_test.SshKeys.Directory, "key-*"));
    }

    [Fact]
    public async Task Unrestricted_keys_are_installed_as_plain_public_keys()
    {
        var editor = NewSshEditor();
        editor.RestrictKey = false;
        _test.Dialogs.ConfirmAnswers.Enqueue(true);

        await editor.ConfirmCommand.ExecuteAsync(null);

        Assert.DoesNotContain("command=", Assert.Single(_test.Ssh.Logins).Command);
        Assert.False(editor.Result!.Repository.SshKeyRestricted);
    }

    [Fact]
    public async Task Without_confirming_the_server_nothing_happens()
    {
        var editor = NewSshEditor();
        _test.Dialogs.ConfirmAnswers.Enqueue(false);

        await editor.ConfirmCommand.ExecuteAsync(null);

        Assert.Equal(Strings.HostKeyRejected, Assert.Single(editor.Errors));
        Assert.Empty(_test.Ssh.Logins);
        Assert.Empty(_test.Borg.Calls);
        Assert.False(_test.KnownHosts.Find("nas.local", 2222).Any());
        Assert.Empty(_test.Vault.Keys);
    }

    [Fact]
    public async Task A_changed_host_key_is_refused()
    {
        _test.KnownHosts.Add("nas.local", 2222, new SshHostKey(FakeSsh.HostKeyBlob("ssh-ed25519", 42)));
        var editor = NewSshEditor();

        await editor.ConfirmCommand.ExecuteAsync(null);

        Assert.Contains("nas.local:2222", Assert.Single(editor.Errors));
        Assert.Empty(_test.Dialogs.Shown);
        Assert.Empty(_test.Ssh.Logins);
    }

    [Fact]
    public async Task A_failed_login_keeps_no_key()
    {
        _test.Ssh.LoginFailure = new SshOperationException(SshFailureKind.AuthenticationFailed, "denied");
        var editor = NewSshEditor();
        _test.Dialogs.ConfirmAnswers.Enqueue(true);

        await editor.ConfirmCommand.ExecuteAsync(null);

        Assert.Equal(Strings.SshLoginFailed, Assert.Single(editor.Errors));
        Assert.Empty(_test.Vault.Keys);
        Assert.Empty(_test.Borg.Calls);
    }

    [Fact]
    public async Task Retrying_reuses_the_installed_key_and_only_success_keeps_it()
    {
        _test.Borg.EncryptionMode = null; // "info" fails after a successful "init"
        var editor = NewSshEditor();
        _test.Dialogs.ConfirmAnswers.Enqueue(true);

        await editor.ConfirmCommand.ExecuteAsync(null);
        Assert.NotEmpty(editor.Errors);
        editor.ServerPassword = "";
        await editor.ConfirmCommand.ExecuteAsync(null);

        Assert.Single(_test.Ssh.Logins);
        Assert.Single(_test.Borg.KeysDuringCalls.Distinct());
        Assert.Empty(_test.Vault.Keys);

        _test.Borg.EncryptionMode = "none";
        await editor.ConfirmCommand.ExecuteAsync(null);
        Assert.Empty(editor.Errors);
        Assert.Equal(editor.Result!.Repository.SshKeyId, Assert.Single(_test.Vault.Keys).Id);
    }

    [Fact]
    public async Task A_key_from_the_list_is_used_as_it_is_or_installed_on_request()
    {
        var key = SshKeyEntry.Generate("Laptop", "me@laptop", DateTimeOffset.Now);
        _test.Vault.SetKey(key);
        var editor = NewSshEditor();
        Assert.Equal(key.Id, Assert.Single(editor.AvailableKeys).Key.Id);
        editor.KeyChoiceExisting = true;
        Assert.False(editor.ShowInstallOptions);
        _test.Dialogs.ConfirmAnswers.Enqueue(true);

        await editor.ConfirmCommand.ExecuteAsync(null);

        Assert.Empty(editor.Errors);
        Assert.Empty(_test.Ssh.Logins);
        var repository = editor.Result!.Repository;
        Assert.Equal(key.Id, repository.SshKeyId);
        Assert.False(repository.SshKeyRestricted);
        Assert.Equal(key.PrivateKey, _test.Borg.KeysDuringCalls[0]);

        var installing = NewSshEditor();
        installing.KeyChoiceExisting = true;
        installing.InstallExistingKey = true;
        Assert.True(installing.ShowInstallOptions);
        await installing.ConfirmCommand.ExecuteAsync(null);

        Assert.Contains(key.PublicKey, Assert.Single(_test.Ssh.Logins).Command);
        Assert.True(installing.Result!.Repository.SshKeyRestricted);
        Assert.Single(_test.Vault.Keys);
    }

    [Fact]
    public void Sharing_a_restricted_key_on_one_account_is_warned_about()
    {
        var key = SshKeyEntry.Generate("NAS", "me", DateTimeOffset.Now);
        _test.Vault.SetKey(key);
        var editor = NewSshEditor([SavedSshRepository(key.Id, "borg/other", restricted: true)]);

        editor.KeyChoiceExisting = true;

        Assert.True(editor.HasSharedKeyWarning);
        Assert.Contains("NAS borg/other", editor.SharedKeyWarning);

        Field(editor, "user").Value = "someone-else";
        Assert.False(editor.HasSharedKeyWarning);
    }

    [Fact]
    public async Task Missing_password_or_key_is_reported_before_contacting_the_server()
    {
        var editor = NewSshEditor();
        editor.ServerPassword = "";
        await editor.ConfirmCommand.ExecuteAsync(null);
        Assert.Contains(Strings.EditorServerPasswordRequired, editor.Errors);

        editor.KeyChoiceExisting = true;
        Assert.True(editor.HasNoKeys);
        await editor.ConfirmCommand.ExecuteAsync(null);
        Assert.Contains(Strings.EditorKeyRequired, editor.Errors);

        Assert.Equal(0, _test.Ssh.HostKeyRequests);
    }

    [Fact]
    public async Task Testing_uses_the_vault_key_and_removing_offers_to_delete_an_unused_key()
    {
        var key = SshKeyEntry.Generate("NAS key", "test", DateTimeOffset.Now);
        _test.Vault.SetKey(key);
        _test.Store.Save([SavedSshRepository(key.Id)]);
        var viewModel = new MainViewModel(_test.Services);
        await viewModel.CheckBorgCommand.ExecuteAsync(null);
        _test.Borg.Calls.Clear();
        _test.Borg.KeysDuringCalls.Clear();

        Assert.True(viewModel.SelectedRepository!.IsSsh);
        Assert.Equal($"NAS key {Strings.SshKeyRestrictedSuffix}", viewModel.SelectedRepository.SshKeyText);

        _test.Dialogs.ConfirmAnswers.Enqueue(true);
        await viewModel.TestRepositoryCommand.ExecuteAsync(null);
        await viewModel.TestRepositoryCommand.ExecuteAsync(null);

        Assert.Equal([Strings.HostKeyTitle], _test.Dialogs.Shown);
        Assert.Equal(1, _test.Ssh.HostKeyRequests);
        Assert.Equal(["info", "info"], _test.Borg.Commands);
        Assert.All(_test.Borg.KeysDuringCalls, content => Assert.Equal(key.PrivateKey, content));
        Assert.True(viewModel.SelectedRepository.HasSuccess);

        _test.Dialogs.ConfirmAnswers.Enqueue(true); // remove
        _test.Dialogs.ConfirmAnswers.Enqueue(true); // delete the key, too
        await viewModel.RemoveRepositoryCommand.ExecuteAsync(null);
        Assert.Contains(Strings.RemoveKeyTitle, _test.Dialogs.Shown);
        Assert.Empty(_test.Vault.Keys);
    }

    [Fact]
    public async Task A_key_still_used_elsewhere_is_not_offered_for_deletion()
    {
        var key = SshKeyEntry.Generate("shared", "test", DateTimeOffset.Now);
        _test.Vault.SetKey(key);
        _test.Store.Save([SavedSshRepository(key.Id, "a", restricted: false), SavedSshRepository(key.Id, "b", restricted: false)]);
        var viewModel = new MainViewModel(_test.Services);
        _test.Dialogs.ConfirmAnswers.Enqueue(true);

        await viewModel.RemoveRepositoryCommand.ExecuteAsync(null);

        Assert.DoesNotContain(Strings.RemoveKeyTitle, _test.Dialogs.Shown);
        Assert.Single(_test.Vault.Keys);
    }

    [Fact]
    public async Task Repositories_without_key_cannot_be_tested()
    {
        _test.Store.Save([SavedSshRepository(keyId: null)]);
        var viewModel = new MainViewModel(_test.Services);
        await viewModel.CheckBorgCommand.ExecuteAsync(null);
        _test.Borg.Calls.Clear();

        await viewModel.TestRepositoryCommand.ExecuteAsync(null);

        Assert.Equal(Strings.SshKeyMissing, viewModel.SelectedRepository!.ResultText);
        Assert.Empty(_test.Borg.Calls);
    }
}
