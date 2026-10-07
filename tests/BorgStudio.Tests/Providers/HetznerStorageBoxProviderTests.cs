using BorgStudio.Plugins;
using BorgStudio.Providers.Hetzner;

namespace BorgStudio.Tests.Providers;

public class HetznerStorageBoxProviderTests
{
    private readonly HetznerStorageBoxProvider _provider = new();

    private static Dictionary<string, string> Values(string user = "u123456", string path = "backups/laptop", string remoteBorg = "borg-1.4") => new()
    {
        [HetznerStorageBoxProvider.UserKey] = user,
        [HetznerStorageBoxProvider.PathKey] = path,
        [HetznerStorageBoxProvider.RemoteBorgKey] = remoteBorg,
    };

    [Fact]
    public void Offers_the_supported_borg_versions_with_1_4_preselected()
    {
        var field = _provider.Fields.Single(field => field.Key == HetznerStorageBoxProvider.RemoteBorgKey);

        Assert.True(_provider.UsesSsh);
        Assert.Equal(ProviderFieldKind.Choice, field.Kind);
        Assert.Equal(["borg-1.4", "borg-1.2"], field.Options.Select(option => option.Value));
        Assert.Equal("borg-1.4", field.DefaultValue);
    }

    [Fact]
    public void Builds_the_storage_box_location_on_port_23()
    {
        var location = _provider.GetLocation(Values());

        Assert.Equal("ssh://u123456@u123456.your-storagebox.de:23/./backups/laptop", location.Url);
        Assert.Equal(["--remote-path=borg-1.4"], location.BorgArguments);

        var endpoint = location.Ssh!;
        Assert.Equal(("u123456.your-storagebox.de", 23, "u123456", "backups/laptop"),
            (endpoint.Host, endpoint.Port, endpoint.User, endpoint.RepositoryPath));
        Assert.Equal(SshKeyInstallation.Sftp, endpoint.KeyInstallation);
        Assert.Equal("install-ssh-key", endpoint.InstallKeyCommand);
        Assert.Equal(HetznerStorageBoxProvider.HostKeyFingerprints, endpoint.PublishedHostKeyFingerprints);
    }

    [Fact]
    public void Sub_accounts_have_their_own_host_name()
    {
        var location = _provider.GetLocation(Values(user: "U123456-sub2", remoteBorg: "borg-1.2"));

        Assert.Equal("ssh://u123456-sub2@u123456-sub2.your-storagebox.de:23/./backups/laptop", location.Url);
        Assert.Equal(["--remote-path=borg-1.2"], location.BorgArguments);
    }

    [Theory]
    [InlineData("./backups/laptop")]
    [InlineData("~/backups/laptop")]
    [InlineData("backups/laptop/")]
    public void Home_relative_spellings_mean_the_same_path(string path)
    {
        Assert.Empty(_provider.Validate(Values(path: path)));
        Assert.Equal("backups/laptop", _provider.GetLocation(Values(path: path)).Ssh!.RepositoryPath);
    }

    [Fact]
    public void Published_fingerprints_are_well_formed()
    {
        Assert.Equal(3, HetznerStorageBoxProvider.HostKeyFingerprints.Count);
        Assert.All(HetznerStorageBoxProvider.HostKeyFingerprints,
            fingerprint => Assert.Matches("^SHA256:[A-Za-z0-9+/]{43}$", fingerprint));
    }

    [Theory]
    [InlineData("", "backups/laptop", "borg-1.4")]
    [InlineData("backup", "backups/laptop", "borg-1.4")]
    [InlineData("u123456@host", "backups/laptop", "borg-1.4")]
    [InlineData("u123456-sub", "backups/laptop", "borg-1.4")]
    [InlineData("u123456", "", "borg-1.4")]
    [InlineData("u123456", "/home/backups", "borg-1.4")]
    [InlineData("u123456", "my backups", "borg-1.4")]
    [InlineData("u123456", "../other", "borg-1.4")]
    [InlineData("u123456", "backups/-x", "borg-1.4")]
    [InlineData("u123456", "backups//laptop", "borg-1.4")]
    [InlineData("u123456", "backups/$HOME", "borg-1.4")]
    [InlineData("u123456", "backups/laptop", "borg-1.1")]
    [InlineData("u123456", "backups/laptop", "")]
    public void Rejects_invalid_input(string user, string path, string remoteBorg)
    {
        Assert.NotEmpty(_provider.Validate(Values(user, path, remoteBorg)));
    }
}
