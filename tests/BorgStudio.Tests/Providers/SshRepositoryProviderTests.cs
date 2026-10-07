using BorgStudio.Plugins;
using BorgStudio.Providers.Ssh;

namespace BorgStudio.Tests.Providers;

public class SshRepositoryProviderTests
{
    private readonly SshRepositoryProvider _provider = new();

    private static Dictionary<string, string> Values(
        string host = "backup.example.com", string port = "22", string user = "borg", string path = "/srv/borg/laptop",
        string remoteBorg = "") => new()
    {
        [SshRepositoryProvider.HostKey] = host,
        [SshRepositoryProvider.PortKey] = port,
        [SshRepositoryProvider.UserKey] = user,
        [SshRepositoryProvider.PathKey] = path,
        [SshRepositoryProvider.RemoteBorgKey] = remoteBorg,
    };

    [Fact]
    public void Is_an_SSH_provider_with_port_22_by_default()
    {
        Assert.True(_provider.UsesSsh);
        Assert.Equal("22", _provider.Fields.Single(field => field.Key == SshRepositoryProvider.PortKey).DefaultValue);
        Assert.False(_provider.Fields.Single(field => field.Key == SshRepositoryProvider.RemoteBorgKey).Required);
    }

    [Theory]
    [InlineData("/srv/borg/laptop", "ssh://borg@backup.example.com:22/srv/borg/laptop", "/srv/borg/laptop")]
    [InlineData("backups/laptop", "ssh://borg@backup.example.com:22/./backups/laptop", "backups/laptop")]
    [InlineData("~/backups/laptop", "ssh://borg@backup.example.com:22/./backups/laptop", "backups/laptop")]
    public void Builds_borg_URLs_for_absolute_and_home_relative_paths(string path, string url, string serverPath)
    {
        var location = _provider.GetLocation(Values(path: path));

        Assert.Equal(url, location.Url);
        Assert.Equal(new SshEndpoint("backup.example.com", 22, "borg", serverPath), location.Ssh);
        Assert.Empty(location.BorgArguments);
    }

    [Fact]
    public void Custom_port_IPv6_and_remote_borg()
    {
        var location = _provider.GetLocation(Values(host: "fd00::5", port: "2222", remoteBorg: "/usr/local/bin/borg"));

        Assert.Equal("ssh://borg@[fd00::5]:2222/srv/borg/laptop", location.Url);
        Assert.Equal(2222, location.Ssh!.Port);
        Assert.Equal(["--remote-path=/usr/local/bin/borg"], location.BorgArguments);
    }

    [Fact]
    public void Valid_input_has_no_errors()
    {
        Assert.Empty(_provider.Validate(Values()));
    }

    [Theory]
    [InlineData("", "22", "borg", "/repo")]
    [InlineData("bad host", "22", "borg", "/repo")]
    [InlineData("host", "0", "borg", "/repo")]
    [InlineData("host", "70000", "borg", "/repo")]
    [InlineData("host", "x", "borg", "/repo")]
    [InlineData("host", "22", "", "/repo")]
    [InlineData("host", "22", "bo rg", "/repo")]
    [InlineData("host", "22", "borg", "")]
    [InlineData("host", "22", "borg", "/repo\"; rm -rf /")]
    [InlineData("host", "22", "borg", "-oProxyCommand=evil")]
    [InlineData("host", "22", "borg", "/repo/$HOME")]
    public void Rejects_invalid_or_dangerous_input(string host, string port, string user, string path)
    {
        Assert.NotEmpty(_provider.Validate(Values(host, port, user, path)));
    }

    [Fact]
    public void Rejects_dangerous_remote_borg_commands()
    {
        Assert.NotEmpty(_provider.Validate(Values(remoteBorg: "borg; rm -rf ~")));
    }
}
