using System.Text;
using BorgStudio.Core.Ssh;
using BorgStudio.Tests.TestSupport;
using Renci.SshNet;

namespace BorgStudio.Tests.Ssh;

public sealed class SshKeysTests : IDisposable
{
    private readonly TemporaryDirectory _directory = new();

    public void Dispose() => _directory.Dispose();

    [Fact]
    public void Generated_keys_are_valid_OpenSSH_ed25519_keys()
    {
        var pair = SshKeyPair.GenerateEd25519("borgstudio-test");

        Assert.StartsWith("-----BEGIN OPENSSH PRIVATE KEY-----\n", pair.PrivateKeyPem);
        Assert.EndsWith("-----END OPENSSH PRIVATE KEY-----\n", pair.PrivateKeyPem);
        Assert.Matches(@"^ssh-ed25519 [A-Za-z0-9+/]+=* borgstudio-test$", pair.PublicKeyLine);

        // An independent SSH implementation must read the private key and derive the same public key.
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes(pair.PrivateKeyPem));
        var parsed = new PrivateKeyFile(stream);
        Assert.Equal("ssh-ed25519", parsed.Key.ToString());
        Assert.Equal(pair.PublicKeyLine.Split(' ')[1], Convert.ToBase64String(parsed.HostKeyAlgorithms.First().Data));
    }

    [Fact]
    public void Every_key_is_new()
    {
        Assert.NotEqual(SshKeyPair.GenerateEd25519("a").PublicKeyLine, SshKeyPair.GenerateEd25519("a").PublicKeyLine);
    }

    [Fact]
    public void Key_store_deletes_only_its_own_old_key_files()
    {
        var store = new SshKeyStore(_directory.Combine("ssh"));
        Directory.CreateDirectory(store.Directory);
        var own = Path.Combine(store.Directory, "repo-1");
        File.WriteAllText(own, "key");
        File.WriteAllText(own + ".pub", "ssh-ed25519 AAAA");
        var foreign = _directory.Combine("my-own-key");
        File.WriteAllText(foreign, "not BorgStudio's");

        Assert.True(store.Contains(own));
        Assert.False(store.Contains(foreign));
        store.Delete(foreign);
        Assert.True(File.Exists(foreign));

        store.Delete(own);
        Assert.False(File.Exists(own));
        Assert.False(File.Exists(own + ".pub"));
    }

    [Fact]
    public void Temporary_key_files_are_private_and_old_leftovers_go()
    {
        var directory = _directory.Combine("ssh");
        var leftover = TemporaryKeyFile.Write(directory, "key-", "old");
        File.SetLastWriteTimeUtc(leftover, DateTime.UtcNow - TimeSpan.FromHours(2));
        var recent = TemporaryKeyFile.Write(directory, "key-", "recent");

        var path = TemporaryKeyFile.Write(directory, "key-", "PRIVATE KEY");

        Assert.Equal("PRIVATE KEY\n", File.ReadAllText(path));
        Assert.False(File.Exists(leftover));
        Assert.True(File.Exists(recent));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(directory));
        }
    }

    [Fact]
    public void Generated_key_entries_carry_type_and_fingerprint()
    {
        var key = SshKeyEntry.Generate("NAS", "borgstudio-test", DateTimeOffset.Now);

        Assert.Equal("NAS", key.Name);
        Assert.True(key.Generated);
        Assert.Equal("ssh-ed25519", key.Type);
        Assert.Matches(@"^SHA256:[A-Za-z0-9+/]{43}$", key.Fingerprint);
        Assert.Equal(SshPublicKey.WithoutComment(key.PublicKey), SshPublicKey.WithoutComment(key.PublicKey + " other comment"));
        Assert.DoesNotContain(key.PrivateKey, new Core.Borg.SshAccess(key.PrivateKey, "/known_hosts").ToString());
    }

    [Fact]
    public void Host_keys_expose_type_and_fingerprint_like_OpenSSH()
    {
        var key = new SshHostKey(FakeSsh.HostKeyBlob("ssh-ed25519", 7));

        Assert.Equal("ssh-ed25519", key.Type);
        Assert.Matches(@"^SHA256:[A-Za-z0-9+/]{43}$", key.Fingerprint);
        Assert.True(key.SameAs(new SshHostKey(FakeSsh.HostKeyBlob("ssh-ed25519", 7))));
        Assert.False(key.SameAs(new SshHostKey(FakeSsh.HostKeyBlob("ssh-ed25519", 8))));
    }

    [Fact]
    public void Known_hosts_uses_OpenSSHs_host_patterns()
    {
        var file = new KnownHostsFile(_directory.Combine("known_hosts"));
        var standard = new SshHostKey(FakeSsh.HostKeyBlob("ssh-ed25519", 1));
        var custom = new SshHostKey(FakeSsh.HostKeyBlob("ssh-ed25519", 2));

        Assert.Empty(file.Find("nas.local", 22));
        file.Add("NAS.local", 22, standard);
        file.Add("nas.local", 2222, custom);

        var lines = File.ReadAllLines(file.Path);
        Assert.Equal($"nas.local ssh-ed25519 {standard.Base64}", lines[0]);
        Assert.Equal($"[nas.local]:2222 ssh-ed25519 {custom.Base64}", lines[1]);
        Assert.True(standard.SameAs(Assert.Single(file.Find("nas.local", 22))));
        Assert.True(custom.SameAs(Assert.Single(file.Find("NAS.LOCAL", 2222))));
        Assert.Empty(file.Find("other.host", 22));
    }

    [Fact]
    public void Authorized_keys_lines_restrict_the_key_to_borg_serve()
    {
        const string publicKey = "ssh-ed25519 AAAAC3Nza borgstudio-pc-1234";

        Assert.Equal(publicKey, AuthorizedKeys.Line(publicKey, restrictToRepository: null));
        Assert.Equal(
            "command=\"borg-1.4 serve --restrict-to-repository backups/laptop\",restrict " + publicKey,
            AuthorizedKeys.Line(publicKey, "backups/laptop", "borg-1.4"));
    }

    [Theory]
    [InlineData("backups/my repo")]
    [InlineData("~/backups")]
    [InlineData("backups/*")]
    public void Paths_a_shell_would_change_are_quoted(string path)
    {
        Assert.Equal(
            $"command=\"borg serve --restrict-to-repository \\\"{path}\\\"\",restrict ssh-ed25519 AAAA c",
            AuthorizedKeys.Line("ssh-ed25519 AAAA c", path));
    }

    [Fact]
    public void Append_command_quotes_the_line_and_refuses_single_quotes()
    {
        var line = AuthorizedKeys.Line("ssh-ed25519 AAAA c", "repo");

        Assert.Equal(
            "umask 077 && mkdir -p ~/.ssh && printf '%s\\n' '" + line + "' >> ~/.ssh/authorized_keys",
            AuthorizedKeys.AppendCommand(line));
        Assert.Throws<ArgumentException>(() => AuthorizedKeys.AppendCommand("ssh-ed25519 AAAA it's"));
    }
}
