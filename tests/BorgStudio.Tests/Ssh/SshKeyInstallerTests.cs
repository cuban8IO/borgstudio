using BorgStudio.Core.Ssh;
using BorgStudio.Plugins;
using BorgStudio.Tests.TestSupport;

namespace BorgStudio.Tests.Ssh;

public class SshKeyInstallerTests
{
    private const string PublicKey = "ssh-ed25519 AAAA borgstudio-test";

    private readonly FakeSsh _ssh = new();

    private static readonly SshEndpoint ShellServer = new("nas.local", 22, "backup", "borg/laptop");

    private static readonly SshEndpoint StorageService = new("box.example.com", 23, "u1", "backups/laptop")
    {
        KeyInstallation = SshKeyInstallation.Sftp,
        InstallKeyCommand = "install-ssh-key",
    };

    private Task Install(SshEndpoint endpoint, bool restrict, string remoteBorg = "borg") =>
        SshKeyInstaller.InstallAsync(_ssh, endpoint, "password", FakeSsh.ServerKey, PublicKey, restrict, remoteBorg);

    [Fact]
    public async Task Servers_with_a_shell_get_the_line_appended_by_a_shell_command()
    {
        await Install(ShellServer, restrict: true);

        var login = Assert.Single(_ssh.Logins);
        Assert.Equal(AuthorizedKeys.AppendCommand(AuthorizedKeys.Line(PublicKey, "borg/laptop")), login.Command);
        Assert.Null(login.StandardInput);
        Assert.Empty(_ssh.SftpAppends);
    }

    [Fact]
    public async Task Restricted_keys_for_servers_without_shell_are_written_over_SFTP()
    {
        await Install(StorageService, restrict: true, remoteBorg: "borg-1.4");

        var append = Assert.Single(_ssh.SftpAppends);
        Assert.Equal("password", append.Password);
        Assert.Equal($"command=\"borg-1.4 serve --restrict-to-repository backups/laptop\",restrict {PublicKey}", append.Line);
        Assert.Empty(_ssh.Logins);
    }

    [Fact]
    public async Task Unrestricted_keys_go_through_the_servers_install_command_on_standard_input()
    {
        await Install(StorageService, restrict: false);

        var login = Assert.Single(_ssh.Logins);
        Assert.Equal("install-ssh-key", login.Command);
        Assert.Equal(PublicKey + "\n", login.StandardInput);
        Assert.Empty(_ssh.SftpAppends);
    }

    [Fact]
    public async Task Unrestricted_keys_without_install_command_use_the_installation_method()
    {
        await Install(StorageService with { InstallKeyCommand = null }, restrict: false);

        Assert.Equal(PublicKey, Assert.Single(_ssh.SftpAppends).Line);
    }
}
