using BorgStudio.Core.Borg;
using BorgStudio.Plugins;
using BorgStudio.Providers.Sftp;
using BorgStudio.Tests.TestSupport;

namespace BorgStudio.Tests.Borg;

/// <summary>Repositories on SFTP-only servers: the folder mounted with sshfs around borg's calls (fake borg and sshfs).</summary>
public sealed class SftpMountTests : IDisposable
{
    private readonly TemporaryDirectory _directory = new();
    private readonly FakeBorg _borg = new();

    public void Dispose() => _directory.Dispose();

    private static BorgInstallation Wsl => FakeBorg.Installation with { Runtime = BorgRuntime.Wsl };

    private static RepositoryLocation Location(string path = "backups/laptop") => new SftpProvider().GetLocation(new Dictionary<string, string>
    {
        [SftpProvider.HostKey] = "nas.example", [SftpProvider.PortKey] = "2222", [SftpProvider.UserKey] = "me", [SftpProvider.PathKey] = path,
    });

    private SshAccess Ssh => new("PRIVATE KEY", _directory.Combine("known_hosts"));

    private BorgClient Client(TimeSpan? linger = null) =>
        new(_borg, FakeBorg.WslExecutable) { NativeIsPosix = true, SftpLinger = linger ?? TimeSpan.FromMinutes(2) };

    private string MountPoint => _borg.SshfsCalls[0][^1];

    /// <summary>borg's own calls (not the shell scripts around them).</summary>
    private IEnumerable<(IReadOnlyList<string> Arguments, string? Passphrase)> BorgCalls => _borg.Calls.Where(call => call.Arguments[0] == "info");

    [Fact]
    public void The_provider_describes_a_mounted_sftp_folder()
    {
        var location = Location("./backups/laptop/");
        Assert.True(location.MountOverSftp);
        Assert.Equal("sftp://me@nas.example:2222/backups/laptop", location.Url);
        Assert.Equal(("nas.example", 2222, "me", "backups/laptop"), (location.Ssh!.Host, location.Ssh.Port, location.Ssh.User, location.Ssh.RepositoryPath));
        Assert.Equal(SshKeyInstallation.Sftp, location.Ssh.KeyInstallation);
        Assert.Equal("/srv/borg/pc", Location("/srv/borg/pc").Ssh!.RepositoryPath);

        var provider = new SftpProvider();
        Assert.Empty(provider.Validate(new Dictionary<string, string> { ["host"] = "nas", ["port"] = "22", ["user"] = "me", ["path"] = "a/b" }));
        Assert.Equal(4, provider.Validate(new Dictionary<string, string> { ["host"] = "", ["port"] = "0", ["user"] = "", ["path"] = "" }).Count);
        Assert.Single(provider.Validate(new Dictionary<string, string> { ["host"] = "nas", ["user"] = "me", ["path"] = "a/../b" }));
    }

    [Fact]
    public async Task Borg_works_on_the_mounted_parent_folder_and_the_mount_is_reused_then_closed()
    {
        var client = Client();

        var info = await client.InfoAsync(Wsl, Location(), null, Ssh);
        await client.InfoAsync(Wsl, Location(), null, Ssh);

        var sshfs = Assert.Single(_borg.SshfsCalls);
        Assert.Equal(["-f", "-p", "2222"], sshfs.Take(3)); // inside WSL: wsl.exe -e sshfs …
        Assert.Contains("StrictHostKeyChecking=yes", sshfs);
        Assert.Equal("me@nas.example:backups", sshfs[^2]);
        Assert.StartsWith("/home/me/.borgstudio/sftp/", MountPoint);
        // borg sees a local folder below the mount point, and its guard checked the mount first.
        Assert.All(BorgCalls, call => Assert.Contains(MountPoint + "/laptop", call.Arguments));
        Assert.Equal(2, BorgCalls.Count());
        Assert.Equal([MountPoint, MountPoint], _borg.GuardedMounts);
        Assert.DoesNotContain(BorgCalls.SelectMany(call => call.Arguments), argument => argument.StartsWith("--upload-ratelimit", StringComparison.Ordinal));
        Assert.Empty(_borg.SftpUnmounts); // kept for the next call

        await client.CloseSftpMountsAsync();
        Assert.Equal([MountPoint], _borg.SftpUnmounts);
        Assert.Empty(_borg.SftpMounts);
        _ = info;
    }

    [Fact]
    public async Task Unused_it_is_unmounted_after_a_while()
    {
        var client = Client(TimeSpan.FromMilliseconds(50));

        await client.InfoAsync(Wsl, Location(), null, Ssh);
        for (var wait = 0; wait < 100 && _borg.SftpUnmounts.Count == 0; wait++)
            await Task.Delay(20);

        Assert.Single(_borg.SftpUnmounts);
    }

    [Fact]
    public async Task Without_sshfs_or_with_a_refused_login_borg_does_not_run()
    {
        _borg.SshfsMissing = true;
        Assert.Equal(BorgErrorKind.SshfsMissing, (await Client().InfoAsync(Wsl, Location(), null, Ssh)).Error!.Kind);

        _borg.SshfsMissing = false;
        _borg.SshfsError = "me@nas.example: Permission denied (publickey).\nread: Connection reset by peer";
        Assert.Equal(BorgErrorKind.SshAuthenticationFailed, (await Client().InfoAsync(Wsl, Location(), null, Ssh)).Error!.Kind);

        _borg.SshfsError = "Host key verification failed.";
        Assert.Equal(BorgErrorKind.SshHostKeyFailed, (await Client().InfoAsync(Wsl, Location(), null, Ssh)).Error!.Kind);
        Assert.Empty(BorgCalls);
    }

    [Fact]
    public async Task A_folder_gone_before_borg_starts_stops_it_instead_of_writing_locally()
    {
        var client = Client();
        _borg.SftpVanished = true;

        var result = await client.InfoAsync(Wsl, Location(), null, Ssh);

        Assert.Equal(BorgErrorKind.ConnectionFailed, result.Error!.Kind);
        Assert.Single(_borg.GuardedMounts);
        Assert.Empty(BorgCalls);
    }

    [Fact]
    public async Task Native_borg_on_windows_cannot_mount()
    {
        var client = new BorgClient(_borg) { NativeIsPosix = false };

        var result = await client.InfoAsync(FakeBorg.Installation, Location(), null, Ssh);

        Assert.Equal(BorgErrorKind.UnsupportedPath, result.Error!.Kind);
        Assert.Empty(_borg.SshfsCalls);
    }
}
