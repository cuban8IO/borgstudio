using BorgStudio.Core.Borg;
using BorgStudio.Core.Plugins;
using BorgStudio.Core.Processes;
using BorgStudio.Core.Repositories;
using BorgStudio.Core.Storage;
using BorgStudio.Plugins;
using BorgStudio.Providers.RsyncNet;
using BorgStudio.Tests.TestSupport;

namespace BorgStudio.Tests.Providers;

/// <summary>The rsync.net provider: location, validation, published host keys and the account's quota.</summary>
public sealed class RsyncNetProviderTests : IDisposable
{
    private const long Gigabyte = 1L << 30;

    private readonly RsyncNetProvider _provider = new();
    private readonly TemporaryDirectory _directory = new();

    public void Dispose() => _directory.Dispose();

    private static Dictionary<string, string> Values(string host = "zh1234", string user = "zh1234", string path = "borg/laptop",
        string remoteBorg = "borg14") => new()
    {
        [RsyncNetProvider.HostKey] = host,
        [RsyncNetProvider.UserKey] = user,
        [RsyncNetProvider.PathKey] = path,
        [RsyncNetProvider.RemoteBorgKey] = remoteBorg,
    };

    [Fact]
    public void The_location_uses_port_22_the_chosen_borg_and_rsync_nets_host_keys()
    {
        Assert.Empty(_provider.Validate(Values()));

        var location = _provider.GetLocation(Values(host: "ZH1234.rsync.net", path: "./borg/laptop/"));

        Assert.Equal("ssh://zh1234@zh1234.rsync.net:22/./borg/laptop", location.Url);
        Assert.Equal(["--remote-path=borg14"], location.BorgArguments);
        var ssh = location.Ssh!;
        Assert.Equal(("zh1234.rsync.net", 22, "zh1234", "borg/laptop"), (ssh.Host, ssh.Port, ssh.User, ssh.RepositoryPath));
        Assert.Equal(SshKeyInstallation.Command, ssh.KeyInstallation);
        Assert.Equal("dd of=.ssh/authorized_keys oflag=append conv=notrunc", ssh.InstallKeyCommand);
        Assert.Contains("SHA256:ZgHI92oPwu+HpSnOC0aEPLLUNhOYwAZGHUNGsYAiJUM", ssh.PublishedHostKeyFingerprints);
        Assert.All(ssh.PublishedHostKeyFingerprints, fingerprint => Assert.Matches("^SHA256:[A-Za-z0-9+/]{43}$", fingerprint));
        Assert.Equal(["--remote-path=borg12"], _provider.GetLocation(Values(remoteBorg: "borg12")).BorgArguments);
    }

    [Fact]
    public void Wrong_values_are_named()
    {
        Assert.Equal(4, _provider.Validate(Values(host: "", user: "", path: "", remoteBorg: "borg1")).Count);
        Assert.Single(_provider.Validate(Values(host: "example.com")));
        Assert.Single(_provider.Validate(Values(user: "a b")));
        Assert.Single(_provider.Validate(Values(path: "/abs")));
        Assert.Single(_provider.Validate(Values(path: "borg/../x")));
    }

    [Theory]
    // FreeBSD style: 1K blocks.
    [InlineData("Disk quotas for user zh1234 (uid 1234):\n     Filesystem  usage    quota    limit   grace  files\n     /data1   1048576  4194304  4404019          120\n", 1L << 30, 4L << 30)]
    // Units in the header.
    [InlineData("Filesystem   Usage (GB)   Soft Quota (GB)   Hard Quota (GB)   Files\ndata1        12.5         400               440               9001\n", (long)(12.5 * (1L << 30)), 400L << 30)]
    // Units at the numbers, over quota marked with *.
    [InlineData("Filesystem  used  quota  limit\ndata1  410G*  400G  440G\n", 410L << 30, 400L << 30)]
    public void The_quota_is_read_in_its_different_forms(string output, long used, long total)
    {
        var quota = RsyncNetProvider.ParseQuota(output);

        Assert.Equal(new StorageQuota(used, total), quota);
    }

    [Theory]
    [InlineData("")]
    [InlineData("borg serve: command not allowed")]
    [InlineData("Filesystem usage quota\ndata1 none none")]
    public void Unreadable_quota_output_is_no_quota(string output) => Assert.Null(RsyncNetProvider.ParseQuota(output));

    [Fact]
    public async Task The_storage_overview_takes_the_quota_where_the_key_allows_it()
    {
        var processes = new RecordingProcesses();
        processes.Results["ssh"] = new ProcessResult(0, "Filesystem usage quota limit\ndata1 100G 400G 440G\n", "");
        var probe = new StorageProbe(new BorgClient(processes), plugins: PluginCatalog.Load([new RsyncNetProviderPlugin()], "no-plugins-here"));
        var repository = new RepositoryConfig { Id = Guid.NewGuid(), Name = "rsync", ProviderId = "rsync-net", ProviderValues = Values() };
        var location = _provider.GetLocation(Values());
        var ssh = new SshAccess("KEY", _directory.Combine("known_hosts"));

        var usage = await probe.MeasureDirectAsync(repository, FakeBorg.Installation, location, ssh);

        Assert.Equal(StorageSource.Server, usage!.Source);
        Assert.Equal(400 * Gigabyte, usage.Total);
        Assert.Equal(300 * Gigabyte, usage.Free);
        Assert.Equal("quota", processes.Calls.Single(call => call.FileName == "ssh").Arguments[^1]);

        // A key restricted to borg cannot run quota (nor df).
        processes.Calls.Clear();
        Assert.Null(await probe.MeasureDirectAsync(repository with { SshKeyRestricted = true }, FakeBorg.Installation, location, ssh));
        Assert.DoesNotContain(processes.Calls, call => call.FileName == "ssh");
    }
}
