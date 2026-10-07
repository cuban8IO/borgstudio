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

    public void Dispose() => _test.Dispose();

    private RepositoryEditorViewModel NewSshEditor()
    {
        var editor = new RepositoryEditorViewModel(_test.Services, FakeBorg.Installation);
        editor.SelectedProvider = editor.Providers.Single(option => option.Provider.Id == "ssh");
        editor.NextCommand.Execute(null);
        editor.Name = "NAS";
        Field(editor, "host").Value = "nas.local";
        Field(editor, "port").Value = "2222";
        Field(editor, "user").Value = "backup";
        Field(editor, "path").Value = "borg/laptop";
        editor.CreateNew = true;
        editor.Passphrase = editor.PassphraseConfirmation = "secret";
        editor.ServerPassword = "server-password";
        return editor;
    }

    private static FieldViewModel Field(RepositoryEditorViewModel editor, string key) => editor.Fields.Single(field => field.Key == key);

    [Fact]
    public async Task Creating_confirms_the_host_installs_a_restricted_key_and_uses_it()
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

        var login = Assert.Single(_test.Ssh.Logins);
        Assert.Equal("server-password", login.Password);
        Assert.Contains("command=\"borg serve --restrict-to-repository \\\"borg/laptop\\\"\",restrict ssh-ed25519 ", login.Command);

        var repository = editor.Result!.Repository;
        Assert.True(repository.SshKeyManaged);
        Assert.True(repository.SshKeyRestricted);
        Assert.True(File.Exists(repository.SshKeyFile));
        Assert.Equal(_test.SshKeys.Directory, Path.GetDirectoryName(repository.SshKeyFile));

        Assert.Equal(["init", "info"], _test.Borg.Commands);
        Assert.All(_test.Borg.Environments, environment =>
        {
            Assert.Contains($"-i '{repository.SshKeyFile}'", environment["BORG_RSH"]);
            Assert.Contains($"UserKnownHostsFile='{_test.SshKeys.KnownHostsFile}'", environment["BORG_RSH"]);
        });
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
    public async Task A_failed_login_deletes_the_new_key()
    {
        _test.Ssh.LoginFailure = new SshOperationException(SshFailureKind.AuthenticationFailed, "denied");
        var editor = NewSshEditor();
        _test.Dialogs.ConfirmAnswers.Enqueue(true);

        await editor.ConfirmCommand.ExecuteAsync(null);

        Assert.Equal(Strings.SshLoginFailed, Assert.Single(editor.Errors));
        Assert.Empty(Directory.GetFiles(_test.SshKeys.Directory, "*.pub"));
        Assert.Empty(_test.Borg.Calls);
    }

    [Fact]
    public async Task Retrying_reuses_the_installed_key_and_cancelling_deletes_it()
    {
        _test.Borg.EncryptionMode = null; // "info" fails after a successful "init"
        var editor = NewSshEditor();
        _test.Dialogs.ConfirmAnswers.Enqueue(true);

        await editor.ConfirmCommand.ExecuteAsync(null);
        Assert.NotEmpty(editor.Errors);
        editor.ServerPassword = "";
        await editor.ConfirmCommand.ExecuteAsync(null);

        Assert.Single(_test.Ssh.Logins);
        Assert.Single(Directory.GetFiles(_test.SshKeys.Directory, "*.pub"));

        editor.OnClosed();
        Assert.Empty(Directory.GetFiles(_test.SshKeys.Directory, "*.pub"));
    }

    [Fact]
    public async Task An_existing_key_is_used_as_it_is()
    {
        var ownKey = _test.Directory.Combine("id_ed25519");
        File.WriteAllText(ownKey, "my key");
        var editor = NewSshEditor();
        editor.KeyChoiceExisting = true;
        editor.ExistingKeyFile = ownKey;
        _test.Dialogs.ConfirmAnswers.Enqueue(true);

        await editor.ConfirmCommand.ExecuteAsync(null);

        Assert.Empty(editor.Errors);
        Assert.Empty(_test.Ssh.Logins);
        var repository = editor.Result!.Repository;
        Assert.Equal(ownKey, repository.SshKeyFile);
        Assert.False(repository.SshKeyManaged);
        Assert.Contains($"-i '{ownKey}'", _test.Borg.Environments[0]["BORG_RSH"]);
    }

    [Fact]
    public async Task Missing_password_or_key_file_is_reported_before_contacting_the_server()
    {
        var editor = NewSshEditor();
        editor.ServerPassword = "";
        await editor.ConfirmCommand.ExecuteAsync(null);
        Assert.Contains(Strings.EditorServerPasswordRequired, editor.Errors);

        editor.KeyChoiceExisting = true;
        editor.ExistingKeyFile = _test.Directory.Combine("does-not-exist");
        await editor.ConfirmCommand.ExecuteAsync(null);
        Assert.Contains(Strings.EditorKeyFileMissing, editor.Errors);

        Assert.Equal(0, _test.Ssh.HostKeyRequests);
    }

    [Fact]
    public async Task Testing_asks_once_for_an_unknown_server_and_removing_deletes_the_key()
    {
        var key = _test.SshKeys.Create("repo-key", "test");
        var repository = new RepositoryConfig
        {
            Id = Guid.NewGuid(),
            Name = "NAS",
            ProviderId = "ssh",
            ProviderValues = new Dictionary<string, string>
            {
                ["host"] = "nas.local", ["port"] = "22", ["user"] = "backup", ["path"] = "borg/laptop", ["remoteBorg"] = "",
            },
            PassphraseMode = PassphraseMode.None,
            EncryptionMode = "none",
            SshKeyFile = key.PrivateKeyFile,
            SshKeyManaged = true,
            SshKeyRestricted = true,
        };
        _test.Store.Save([repository]);
        var viewModel = new MainViewModel(_test.Services);
        await viewModel.CheckBorgCommand.ExecuteAsync(null);
        _test.Borg.Calls.Clear();
        _test.Borg.Environments.Clear();

        Assert.True(viewModel.SelectedRepository!.IsSsh);
        Assert.Contains(Strings.SshKeyRestrictedSuffix, viewModel.SelectedRepository.SshKeyText);

        _test.Dialogs.ConfirmAnswers.Enqueue(true);
        await viewModel.TestRepositoryCommand.ExecuteAsync(null);
        await viewModel.TestRepositoryCommand.ExecuteAsync(null);

        Assert.Equal([Strings.HostKeyTitle], _test.Dialogs.Shown);
        Assert.Equal(1, _test.Ssh.HostKeyRequests);
        Assert.Equal(["info", "info"], _test.Borg.Commands);
        Assert.Contains($"-i '{key.PrivateKeyFile}'", _test.Borg.Environments[0]["BORG_RSH"]);
        Assert.True(viewModel.SelectedRepository.HasSuccess);

        _test.Dialogs.ConfirmAnswers.Enqueue(true);
        await viewModel.RemoveRepositoryCommand.ExecuteAsync(null);
        Assert.False(File.Exists(key.PrivateKeyFile));
    }

    [Fact]
    public async Task Repositories_without_key_cannot_be_tested()
    {
        _test.Store.Save([new RepositoryConfig
        {
            Id = Guid.NewGuid(),
            Name = "NAS",
            ProviderId = "ssh",
            ProviderValues = new Dictionary<string, string>
            {
                ["host"] = "nas.local", ["port"] = "22", ["user"] = "backup", ["path"] = "/srv/borg", ["remoteBorg"] = "",
            },
        }]);
        var viewModel = new MainViewModel(_test.Services);
        await viewModel.CheckBorgCommand.ExecuteAsync(null);
        _test.Borg.Calls.Clear();

        await viewModel.TestRepositoryCommand.ExecuteAsync(null);

        Assert.Equal(Strings.SshKeyMissing, viewModel.SelectedRepository!.ResultText);
        Assert.Empty(_test.Borg.Calls);
    }
}
