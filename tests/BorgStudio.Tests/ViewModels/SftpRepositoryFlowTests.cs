using BorgStudio.App.ViewModels;
using BorgStudio.Tests.TestSupport;

namespace BorgStudio.Tests.ViewModels;

/// <summary>Adding a repository on an SFTP-only server: the key goes there over SFTP and is never restricted to borg.</summary>
public sealed class SftpRepositoryFlowTests : IDisposable
{
    private readonly TestServices _test = new();

    // SSH keys live in the vault.
    public SftpRepositoryFlowTests() => _test.Vault.SetUp("123456");

    public void Dispose() => _test.Dispose();

    private static FieldViewModel Field(RepositoryEditorViewModel editor, string key) => editor.Fields.Single(field => field.Key == key);

    [Fact]
    public async Task The_key_is_written_over_sftp_unrestricted_and_borg_runs_on_the_mounted_folder()
    {
        var editor = new RepositoryEditorViewModel(_test.Services, FakeBorg.Installation);
        editor.SelectedProvider = editor.Providers.Single(option => option.Provider.Id == "sftp");
        editor.NextCommand.Execute(null);
        Assert.True(editor.IsSftpMount);
        Assert.False(editor.RestrictKey);
        Assert.True(editor.ShowInstallOptions);

        editor.Name = "NAS";
        Field(editor, "host").Value = "nas.example";
        Field(editor, "user").Value = "me";
        Field(editor, "path").Value = "backups/laptop";
        editor.CreateNew = true;
        editor.Passphrase = editor.PassphraseConfirmation = "secret";
        editor.StorageAsk = true;
        editor.ServerPassword = "nas-password";
        _test.Dialogs.ConfirmAnswers.Enqueue(true); // the server's host key

        await editor.ConfirmCommand.ExecuteAsync(null);

        Assert.Empty(editor.Errors);
        var append = Assert.Single(_test.Ssh.SftpAppends);
        Assert.Equal(("nas.example", 22, "nas-password"), (append.Endpoint.Host, append.Endpoint.Port, append.Password));
        Assert.StartsWith("ssh-ed25519 ", append.Line);
        Assert.Equal(["init", "info"], _test.Borg.Calls.Select(call => call.Arguments[0]).Where(command => command is "init" or "info"));
        var mountPoint = Assert.Single(_test.Borg.SshfsCalls)[^1];
        Assert.Contains(_test.Borg.Calls, call => call.Arguments[0] == "init" && call.Arguments.Contains(mountPoint + "/laptop"));
        Assert.False(editor.Result!.Repository.SshKeyRestricted);
        await _test.Services.BorgClient.CloseSftpMountsAsync();
        Assert.Single(_test.Borg.SftpUnmounts);
    }
}
