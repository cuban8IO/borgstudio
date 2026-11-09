using BorgStudio.Core.Repositories;
using BorgStudio.Core.Security;
using BorgStudio.Core.Settings;
using BorgStudio.Core.Ssh;
using BorgStudio.Core.Transfer;
using BorgStudio.Tests.TestSupport;

namespace BorgStudio.Tests.Transfer;

public sealed class ExportFileTests : IDisposable
{
    private const string Password = "a long export password";

    private readonly TemporaryDirectory _directory = new();

    public void Dispose() => _directory.Dispose();

    private string FilePath => _directory.Combine("export.borgstudio");

    private static ExportContents Sample()
    {
        var repository = new RepositoryConfig
        {
            Id = Guid.NewGuid(),
            Name = "NAS",
            ProviderId = "ssh",
            ProviderValues = new Dictionary<string, string> { ["host"] = "nas.local" },
            PassphraseMode = PassphraseMode.Vault,
            EncryptionMode = "keyfile-blake2",
            BorgRepositoryId = "0123abcd0123abcd",
            SshKeyId = Guid.NewGuid(),
            SshKeyRestricted = true,
        };
        return new ExportContents
        {
            Created = new DateTimeOffset(2026, 11, 6, 9, 0, 0, TimeSpan.Zero),
            AppVersion = "0.2.0",
            Machine = "NB-1",
            Repositories = [repository],
            Passphrases = { [repository.Id.ToString("N")] = "the repository passphrase" },
            SshKeys = [SshKeyEntry.Generate("NAS key", "me", DateTimeOffset.Now) with { Id = repository.SshKeyId!.Value }],
            KnownHosts = "[nas.local]:2222 ssh-ed25519 AAAA\n",
            Settings = new AppSettings { VaultAutoLockMinutes = 30 },
            BorgKeys = { [repository.Id.ToString("N")] = new ExportedBorgKey("BORG_KEY 0123\nKEY", Keyfile: true) },
        };
    }

    [Fact]
    public void An_export_reads_back_completely_and_nothing_is_readable_in_the_file()
    {
        var contents = Sample();

        ExportFile.Write(FilePath, contents, Password, TestServices.FastKdf);

        var text = File.ReadAllText(FilePath);
        Assert.Contains("borgstudio-export", text);
        foreach (var secret in new[] { "the repository passphrase", "NAS", "nas.local", "OPENSSH", "BORG_KEY" })
            Assert.DoesNotContain(secret, text);

        var read = ExportFile.Read(FilePath, Password);
        var repository = Assert.Single(read.Repositories);
        Assert.Equal(contents.Repositories[0], repository with { ProviderValues = contents.Repositories[0].ProviderValues });
        Assert.Equal("nas.local", repository.ProviderValues["host"]);
        Assert.Equal("the repository passphrase", read.PassphraseFor(repository.Id));
        Assert.Equal(contents.SshKeys[0], Assert.Single(read.SshKeys));
        Assert.Equal(contents.KnownHosts, read.KnownHosts);
        Assert.Equal(30, read.Settings.VaultAutoLockMinutes);
        Assert.Equal(new ExportedBorgKey("BORG_KEY 0123\nKEY", true), read.BorgKeyFor(repository.Id));
        Assert.Equal("NB-1", read.Machine);
        Assert.Equal(contents.Created, read.Created);
    }

    [Fact]
    public void A_wrong_password_is_refused()
    {
        ExportFile.Write(FilePath, Sample(), Password, TestServices.FastKdf);

        Assert.Throws<SecretBoxException>(() => ExportFile.Read(FilePath, "another long password"));
    }

    [Fact]
    public void The_password_must_be_long()
    {
        Assert.False(ExportFile.IsValidPassword("short pass"));
        Assert.Throws<ArgumentException>(() => ExportFile.Write(FilePath, Sample(), "short pass", TestServices.FastKdf));
        Assert.False(File.Exists(FilePath));
    }

    [Fact]
    public void Other_files_and_newer_exports_are_refused()
    {
        File.WriteAllText(FilePath, "hello");
        Assert.Throws<ExportFileException>(() => ExportFile.Read(FilePath, Password));

        var vault = new Vault(_directory.Combine("vault.json"), TestServices.FastKdf);
        vault.SetUp("123456");
        Assert.Throws<ExportFileException>(() => ExportFile.Read(vault.FilePath, Password));

        ExportFile.Write(FilePath, Sample() with { Version = 2 }, Password, TestServices.FastKdf);
        Assert.Throws<ExportFileException>(() => ExportFile.Read(FilePath, Password));
    }

    private sealed record Entry(Guid Id, string Value);

    [Fact]
    public void Merging_adds_new_entries_and_replaces_only_chosen_ones()
    {
        var (a, b, c) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        Entry[] current = [new(a, "here a"), new(b, "here b")];
        Entry[] imported = [new(b, "export b"), new(c, "export c"), new(a, "export a")];

        var keep = ImportMerge.Merge(current, imported, entry => entry.Id, ImportMode.Merge, new HashSet<Guid>());
        Assert.Equal(["here a", "here b", "export c"], keep.Select(entry => entry.Value));

        var replaceB = ImportMerge.Merge(current, imported, entry => entry.Id, ImportMode.Merge, new HashSet<Guid> { b });
        Assert.Equal(["here a", "export b", "export c"], replaceB.Select(entry => entry.Value));

        var replaceAll = ImportMerge.Merge(current, imported, entry => entry.Id, ImportMode.Replace, new HashSet<Guid>());
        Assert.Equal(["export b", "export c", "export a"], replaceAll.Select(entry => entry.Value));
    }

    [Fact]
    public void Known_hosts_are_merged_line_by_line_or_replaced()
    {
        var file = new KnownHostsFile(_directory.Combine("ssh", "known_hosts"));
        Assert.Equal("", file.Read());

        file.Merge("host1 ssh-ed25519 AAA1\nhost2 ssh-ed25519 AAA2\n");
        file.Merge("host2 ssh-ed25519 AAA2\nhost3 ssh-ed25519 AAA3");
        Assert.Equal("host1 ssh-ed25519 AAA1\nhost2 ssh-ed25519 AAA2\nhost3 ssh-ed25519 AAA3\n", file.Read());

        file.Replace("only ssh-ed25519 AAA\n");
        Assert.Equal("only ssh-ed25519 AAA\n", file.Read());
    }
}
