using BorgStudio.App.Resources;
using BorgStudio.App.ViewModels;
using BorgStudio.Tests.TestSupport;

namespace BorgStudio.Tests.ViewModels;

/// <summary>Adding an rsync.net repository, with fake borg and a fake SSH server.</summary>
public sealed class RsyncNetRepositoryFlowTests : IDisposable
{
    private readonly TestServices _test = new();

    // SSH keys live in the vault.
    public RsyncNetRepositoryFlowTests() => _test.Vault.SetUp("123456");

    public void Dispose() => _test.Dispose();

    private RepositoryEditorViewModel NewEditor()
    {
        var editor = new RepositoryEditorViewModel(_test.Services, FakeBorg.Installation);
        editor.SelectedProvider = editor.Providers.Single(option => option.Provider.Id == "rsync-net");
        editor.NextCommand.Execute(null);
        editor.Name = "rsync.net";
        Field(editor, "host").Value = "zh1234";
        Field(editor, "user").Value = "zh1234";
        Field(editor, "path").Value = "borg/laptop";
        editor.CreateNew = true;
        editor.Passphrase = editor.PassphraseConfirmation = "secret";
        editor.StorageAsk = true;
        editor.ServerPassword = "account-password";
        // The fake server's key is not one rsync.net published: confirmed after the warning.
        _test.Dialogs.ConfirmAnswers.Enqueue(true);
        return editor;
    }

    private static FieldViewModel Field(RepositoryEditorViewModel editor, string key) => editor.Fields.Single(field => field.Key == key);

    [Fact]
    public async Task A_restricted_key_is_appended_with_dd_and_borg_14_runs()
    {
        var editor = NewEditor();
        Assert.Equal("borg14", Field(editor, "remoteBorg").Value);

        await editor.ConfirmCommand.ExecuteAsync(null);

        Assert.Empty(editor.Errors);
        var login = Assert.Single(_test.Ssh.Logins);
        Assert.Equal("zh1234.rsync.net", login.Endpoint.Host);
        Assert.Equal(22, login.Endpoint.Port);
        Assert.Equal("account-password", login.Password);
        Assert.Equal("dd of=.ssh/authorized_keys oflag=append conv=notrunc", login.Command);
        Assert.StartsWith("\ncommand=\"borg14 serve --restrict-to-repository borg/laptop\",restrict ssh-ed25519 ", login.StandardInput);
        Assert.EndsWith("\n", login.StandardInput);
        Assert.Empty(_test.Ssh.SftpAppends);

        Assert.Equal(["init", "info"], _test.Borg.Commands);
        Assert.All(_test.Borg.Calls, call => Assert.Contains("--remote-path=borg14", call.Arguments));
        Assert.Contains("ssh://zh1234@zh1234.rsync.net:22/./borg/laptop", _test.Borg.Calls[0].Arguments);
        Assert.True(editor.Result!.Repository.SshKeyRestricted);
    }

    [Fact]
    public async Task An_unrestricted_key_goes_the_same_way()
    {
        var editor = NewEditor();
        editor.RestrictKey = false;

        await editor.ConfirmCommand.ExecuteAsync(null);

        var login = Assert.Single(_test.Ssh.Logins);
        Assert.Equal("dd of=.ssh/authorized_keys oflag=append conv=notrunc", login.Command);
        Assert.StartsWith("\nssh-ed25519 ", login.StandardInput);
        Assert.False(editor.Result!.Repository.SshKeyRestricted);
    }

    [Fact]
    public async Task When_the_restricted_key_is_not_accepted_the_editor_suggests_an_unrestricted_one()
    {
        _test.Borg.EncryptionMode = null;
        _test.Borg.InfoError = "ConnectionClosed";
        var editor = NewEditor();
        editor.CreateNew = false;

        await editor.ConfirmCommand.ExecuteAsync(null);

        Assert.Contains(Strings.EditorRestrictedKeyRefused, editor.Errors);

        // Unrestricted, the hint is not given.
        editor.RestrictKey = false;
        await editor.ConfirmCommand.ExecuteAsync(null);
        Assert.DoesNotContain(Strings.EditorRestrictedKeyRefused, editor.Errors);
    }
}
