using System.Text;
using BorgStudio.Plugins;
using BorgStudio.Providers.NetworkDisk;
using BorgStudio.Providers.Usb;
using BorgStudio.Tests.TestSupport;

namespace BorgStudio.Tests.Providers;

/// <summary>The network disk provider: setting up once, logging in before use, finding the repository, logging out.</summary>
public sealed class NetworkDiskProviderTests : IDisposable
{
    private const string Id = "00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff";
    private const string Iqn = "iqn.2000-01.com.synology:nas.backup";

    private readonly TemporaryDirectory _directory = new();
    private readonly FakeDrives _drives = new();
    private readonly FakeNetworkDisks _disks;
    private readonly NetworkDiskProvider _provider;

    public NetworkDiskProviderTests()
    {
        _disks = new FakeNetworkDisks(_drives);
        _provider = new NetworkDiskProvider(_disks, _drives);
        _drives.Attached.Add(new DriveRoot(Root("C"), IsNetwork: false));
        NetworkDiskProvider.AppearTimeout = TimeSpan.FromMilliseconds(300);
        NetworkDiskProvider.PollInterval = TimeSpan.FromMilliseconds(20);
    }

    public void Dispose() => _directory.Dispose();

    private string Root(string letter)
    {
        var root = _directory.Combine("volumes", letter);
        Directory.CreateDirectory(root);
        return root;
    }

    private static Dictionary<string, string> Values(string portal = "nas.local", string? chapUser = null) => new()
    {
        [NetworkDiskProvider.ProtocolKey] = "iscsi",
        [NetworkDiskProvider.PortalKey] = portal,
        [NetworkDiskProvider.TargetKey] = Iqn,
        [NetworkDiskProvider.ChapUserKey] = chapUser ?? "",
        [NetworkDiskProvider.FolderKey] = "borg/laptop",
    };

    private async Task<Dictionary<string, string>> PreparedAsync(Dictionary<string, string> values, string? secret = null)
    {
        var prepared = await _provider.PrepareAsync(new RepositoryPreparationRequest(values,
            secret is null ? new Dictionary<string, string>() : new Dictionary<string, string> { [NetworkDiskProvider.ChapSecretKey] = secret },
            Guid.Parse("6f1c2d3e-4b5a-6978-8a9b-0c1d2e3f4a5b")), CancellationToken.None);
        var merged = new Dictionary<string, string>(values);
        foreach (var (key, value) in prepared)
            merged[key] = value;
        return merged;
    }

    private static void PutRepository(string folder, string? id = Id)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "config"), $"[repository]\nversion = 1\nid = {id}\n");
    }

    [Fact]
    public void Portals_targets_and_folders_are_checked()
    {
        Assert.Empty(_provider.Validate(Values()));
        Assert.Equal(("nas.local", 3260), (NetworkDiskProvider.Target(Values()).Host, NetworkDiskProvider.Target(Values()).Port));
        Assert.Equal(3261, NetworkDiskProvider.Target(Values("nas.local:3261")).Port);
        Assert.Equal("fd00::5", NetworkDiskProvider.Target(Values("[fd00::5]:3260")).Host);
        Assert.Single(_provider.Validate(Values("nas local")));
        Assert.Single(_provider.Validate(new Dictionary<string, string>(Values()) { [NetworkDiskProvider.TargetKey] = "iqn with space" }));
        Assert.Single(_provider.Validate(new Dictionary<string, string>(Values()) { [NetworkDiskProvider.FolderKey] = "../x" }));
        Assert.Single(_provider.Validate(new Dictionary<string, string>(Values()) { [NetworkDiskProvider.ProtocolKey] = "nvme-tcp" })); // Windows
        Assert.Equal("borg/laptop", _provider.GetLocation(Values()).Url);
    }

    [Fact]
    public async Task Setting_up_happens_once_and_again_when_the_target_or_secret_changes()
    {
        var values = await PreparedAsync(Values(chapUser: "backup"), "s3cret");
        var (target, id, secret) = Assert.Single(_disks.SetUps);
        Assert.Equal((Iqn, "backup", "s3cret"), (target.Name, target.ChapUser, secret));
        Assert.Equal("6f1c2d3e4b5a69788a9b0c1d2e3f4a5b", id);
        Assert.DoesNotContain("s3cret", values[NetworkDiskProvider.SetupKey]);

        await PreparedAsync(values, "s3cret");
        await PreparedAsync(values); // the secret not at hand (vault locked): left as it is
        Assert.Single(_disks.SetUps);

        await PreparedAsync(values, "n3w");
        await PreparedAsync(new Dictionary<string, string>(values) { [NetworkDiskProvider.PortalKey] = "nas2.local" }, "n3w");
        Assert.Equal(3, _disks.SetUps.Count);
        Assert.All(_disks.SetUps, setUp => Assert.Equal(id, setUp.Id));
    }

    [Fact]
    public async Task Before_use_it_logs_in_finds_the_repository_by_its_id_and_logs_out_afterwards()
    {
        var values = await PreparedAsync(Values());
        _disks.Volume = Root("E");
        PutRepository(Path.Combine(Root("E"), "borg", "laptop"));
        _drives.Attached.Clear(); // the volume is not there before logging in

        var connection = await _provider.ConnectAsync(new RepositoryConnectRequest(values) { RepositoryId = Id }, CancellationToken.None);

        Assert.Single(_disks.Connects);
        Assert.Equal(Path.Combine(Root("E"), "borg", "laptop"), connection.Location.Url);
        Assert.Equal(connection.Location.Url, connection.UpdatedValues![NetworkDiskProvider.LastPathKey]);
        Assert.Null(await connection.DisconnectAsync());
        Assert.Single(_disks.Disconnects);

        // Attached already (connected for good): used, and left attached.
        _drives.Attached.Add(new DriveRoot(Root("E"), IsNetwork: false));
        var attached = await _provider.ConnectAsync(new RepositoryConnectRequest(values) { RepositoryId = Id }, CancellationToken.None);
        await attached.DisconnectAsync();
        Assert.Single(_disks.Connects);
        Assert.Single(_disks.Disconnects);
    }

    [Fact]
    public async Task Not_set_up_or_nothing_appearing_is_told_and_logged_out()
    {
        var notSetUp = await Assert.ThrowsAsync<RepositoryUnavailableException>(() =>
            _provider.ConnectAsync(new RepositoryConnectRequest(Values()) { RepositoryId = Id }, CancellationToken.None));
        Assert.Contains("Editor", notSetUp.Message, StringComparison.OrdinalIgnoreCase);

        var values = await PreparedAsync(Values());
        _disks.Volume = null;
        await Assert.ThrowsAsync<RepositoryUnavailableException>(() =>
            _provider.ConnectAsync(new RepositoryConnectRequest(values) { RepositoryId = Id }, CancellationToken.None));
        Assert.Single(_disks.Disconnects);
    }

    [Fact]
    public async Task A_new_repository_goes_below_the_volume_that_appeared()
    {
        var values = await PreparedAsync(Values());
        _disks.Volume = Root("F");
        Directory.CreateDirectory(Path.Combine(Root("F"), "borg"));

        var connection = await _provider.ConnectAsync(new RepositoryConnectRequest(values), CancellationToken.None);

        Assert.Equal(Path.Combine(Root("F"), "borg", "laptop"), connection.Location.Url);
    }

    [Fact]
    public void The_windows_tasks_log_in_to_exactly_this_target_with_highest_rights()
    {
        var target = NetworkDiskProvider.Target(Values(chapUser: "backup"));
        var connect = WindowsIscsi.ConnectCommand(target, "it's");
        Assert.Contains($"Connect-IscsiTarget -NodeAddress '{Iqn}' -TargetPortalAddress 'nas.local' -TargetPortalPortNumber 3260", connect);
        Assert.Contains("-AuthenticationType ONEWAYCHAP -ChapUsername 'backup' -ChapSecret 'it''s'", connect);
        Assert.DoesNotContain("Chap", WindowsIscsi.ConnectCommand(target, null));
        Assert.Equal($"Disconnect-IscsiTarget -NodeAddress '{Iqn}' -Confirm:$false", WindowsIscsi.DisconnectCommand(target));

        var setup = WindowsIscsi.SetupScript(target, "abc", "it's", @"PC\me", @"C:\temp\log.txt");
        Assert.Contains("-LogonType S4U -RunLevel Highest", setup);
        Assert.Contains("'NetworkDisk-abc-connect'", setup);
        Assert.Contains("'NetworkDisk-abc-disconnect'", setup);
        Assert.Contains("New-IscsiTargetPortal -TargetPortalAddress 'nas.local' -TargetPortalPortNumber 3260", setup);
        Assert.Contains(WindowsIscsi.Encode(connect), setup);
        Assert.DoesNotContain("it's", setup);
        Assert.Equal(connect, Encoding.Unicode.GetString(Convert.FromBase64String(WindowsIscsi.Encode(connect))));
    }

    [Fact]
    public void Linux_logs_in_with_the_commands_its_sudoers_rule_allows()
    {
        var target = NetworkDiskProvider.Target(Values());
        Assert.Equal(["iscsiadm", "-m", "node", "-T", Iqn, "-p", "nas.local:3260", "--login"], LinuxNetworkDisks.LoginCommand(target));
        Assert.Equal("--logout", LinuxNetworkDisks.LogoutCommand(target)[^1]);
        var nvme = target with { Protocol = NetworkDiskProtocol.NvmeTcp, Port = 4420 };
        Assert.Equal(["nvme", "connect", "-t", "tcp", "-a", "nas.local", "-s", "4420", "-n", Iqn], LinuxNetworkDisks.LoginCommand(nvme));
        Assert.Contains("visudo -cf", LinuxNetworkDisks.SetupScript);
        Assert.Contains("-m node -T $n -p $p --login, $a -m node -T $n -p $p --logout", LinuxNetworkDisks.SetupScript);
    }
}
