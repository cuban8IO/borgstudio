using System.Globalization;
using BorgStudio.App.Resources;
using BorgStudio.App.ViewModels;
using BorgStudio.Tests.TestSupport;

namespace BorgStudio.Tests.ViewModels;

/// <summary>Adding a Hetzner Storage Box repository, with fake borg and a fake SSH server.</summary>
public sealed class HetznerRepositoryFlowTests : IDisposable
{
    private readonly TestServices _test = new();

    public void Dispose() => _test.Dispose();

    private RepositoryEditorViewModel NewStorageBoxEditor()
    {
        var editor = new RepositoryEditorViewModel(_test.Services, FakeBorg.Installation);
        editor.SelectedProvider = editor.Providers.Single(option => option.Provider.Id == "hetzner-storage-box");
        editor.NextCommand.Execute(null);
        editor.Name = "Storage Box";
        Field(editor, "user").Value = "u123456";
        Field(editor, "path").Value = "backups/laptop";
        editor.CreateNew = true;
        editor.Passphrase = editor.PassphraseConfirmation = "secret";
        editor.ServerPassword = "box-password";
        return editor;
    }

    private static FieldViewModel Field(RepositoryEditorViewModel editor, string key) => editor.Fields.Single(field => field.Key == key);

    [Fact]
    public void The_borg_version_is_a_choice_with_borg_1_4_preselected()
    {
        var field = Field(NewStorageBoxEditor(), "remoteBorg");

        Assert.True(field.IsChoice);
        Assert.False(field.IsTextInput);
        Assert.Equal(["borg-1.4", "borg-1.2"], field.Options.Select(option => option.Value));
        Assert.Equal("borg-1.4", field.Value);
        Assert.Same(field.Options[0], field.SelectedOption);

        field.SelectedOption = field.Options[1];
        Assert.Equal("borg-1.2", field.Value);
    }

    [Fact]
    public async Task Creating_writes_a_restricted_key_over_SFTP_and_runs_borg_1_4()
    {
        var editor = NewStorageBoxEditor();
        _test.Dialogs.ConfirmAnswers.Enqueue(true);

        await editor.ConfirmCommand.ExecuteAsync(null);

        Assert.Empty(editor.Errors);
        var append = Assert.Single(_test.Ssh.SftpAppends);
        Assert.Equal("u123456.your-storagebox.de", append.Endpoint.Host);
        Assert.Equal(23, append.Endpoint.Port);
        Assert.Equal("box-password", append.Password);
        Assert.StartsWith("command=\"borg-1.4 serve --restrict-to-repository backups/laptop\",restrict ssh-ed25519 ", append.Line);
        Assert.Empty(_test.Ssh.Logins);

        Assert.Equal(["init", "info"], _test.Borg.Commands);
        Assert.All(_test.Borg.Calls, call => Assert.Contains("--remote-path=borg-1.4", call.Arguments));
        Assert.Contains("ssh://u123456@u123456.your-storagebox.de:23/./backups/laptop", _test.Borg.Calls[0].Arguments);
        Assert.True(editor.Result!.Repository.SshKeyRestricted);
    }

    [Fact]
    public async Task Unrestricted_keys_are_installed_with_install_ssh_key()
    {
        var editor = NewStorageBoxEditor();
        editor.RestrictKey = false;
        _test.Dialogs.ConfirmAnswers.Enqueue(true);

        await editor.ConfirmCommand.ExecuteAsync(null);

        var login = Assert.Single(_test.Ssh.Logins);
        Assert.Equal("install-ssh-key", login.Command);
        Assert.StartsWith("ssh-ed25519 ", login.StandardInput);
        Assert.Empty(_test.Ssh.SftpAppends);
        Assert.False(editor.Result!.Repository.SshKeyRestricted);
    }

    [Fact]
    public async Task A_host_key_Hetzner_did_not_publish_is_only_trusted_after_a_warning()
    {
        // The fake server's key is not one of Hetzner's.
        var editor = NewStorageBoxEditor();
        _test.Dialogs.ConfirmAnswers.Enqueue(false);

        await editor.ConfirmCommand.ExecuteAsync(null);

        Assert.Equal(
            string.Format(CultureInfo.CurrentCulture, Strings.HostKeyNotPublished,
                "u123456.your-storagebox.de:23", FakeSsh.ServerKey.Type, FakeSsh.ServerKey.Fingerprint),
            Assert.Single(_test.Dialogs.Messages));
        Assert.Equal(Strings.HostKeyRejected, Assert.Single(editor.Errors));
        Assert.Empty(_test.Ssh.SftpAppends);
        Assert.Empty(_test.Borg.Calls);
    }

    [Fact]
    public async Task Choosing_another_borg_version_after_a_failure_installs_a_matching_key()
    {
        _test.Borg.EncryptionMode = null; // "info" fails after a successful "init"
        var editor = NewStorageBoxEditor();
        _test.Dialogs.ConfirmAnswers.Enqueue(true);
        await editor.ConfirmCommand.ExecuteAsync(null);

        Field(editor, "remoteBorg").Value = "borg-1.2";
        await editor.ConfirmCommand.ExecuteAsync(null);

        Assert.Equal(2, _test.Ssh.SftpAppends.Count);
        Assert.StartsWith("command=\"borg-1.2 serve ", _test.Ssh.SftpAppends[1].Line);
        Assert.Single(Directory.GetFiles(_test.SshKeys.Directory, "*.pub"));
    }
}
