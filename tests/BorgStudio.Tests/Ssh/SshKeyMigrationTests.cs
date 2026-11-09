using BorgStudio.Core.Repositories;
using BorgStudio.Core.Security;
using BorgStudio.Core.Ssh;
using BorgStudio.Tests.TestSupport;

namespace BorgStudio.Tests.Ssh;

/// <summary>Key files of older versions move into the vault.</summary>
public sealed class SshKeyMigrationTests : IDisposable
{
    private readonly TemporaryDirectory _directory = new();
    private readonly SshKeyStore _store;
    private readonly Vault _vault;

    public SshKeyMigrationTests()
    {
        _store = new SshKeyStore(_directory.Combine("ssh"));
        Directory.CreateDirectory(_store.Directory);
        _vault = new Vault(_directory.Combine("vault.json"), TestServices.FastKdf);
        _vault.SetUp("123456");
    }

    public void Dispose() => _directory.Dispose();

    private static RepositoryConfig Repository(string name, string? keyFile, bool managed) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        ProviderId = "ssh",
        ProviderValues = new Dictionary<string, string>(),
        LegacySshKeyFile = keyFile,
        LegacySshKeyManaged = managed,
        SshKeyRestricted = true,
    };

    /// <summary>A key file the way older BorgStudio versions created them (with .pub).</summary>
    private string OwnKeyFile(string name, out SshKeyPair pair)
    {
        pair = SshKeyPair.GenerateEd25519("borgstudio-old");
        var file = Path.Combine(_store.Directory, name);
        File.WriteAllText(file, pair.PrivateKeyPem);
        File.WriteAllText(file + ".pub", pair.PublicKeyLine + "\n");
        return file;
    }

    private Task<SshKeyMigrationResult> MigrateAsync(params RepositoryConfig[] repositories) =>
        SshKeyMigration.MigrateAsync(repositories, _vault, _store, SshKeygen.CreateDefault(), DateTimeOffset.Now);

    [Fact]
    public async Task BorgStudios_own_key_files_move_into_the_vault_and_are_then_deleted()
    {
        var file = OwnKeyFile("0123-20261104", out var pair);
        File.SetLastWriteTimeUtc(file, new DateTime(2026, 11, 4, 14, 9, 49, DateTimeKind.Utc));
        var local = Repository("Local", null, false) with { ProviderId = "local" };

        var result = await MigrateAsync(local, Repository("NAS", file, managed: true));

        Assert.Equal(1, result.Moved);
        Assert.Empty(result.Failed);
        Assert.Equal([file], result.FilesToDelete);
        Assert.Same(local, result.Repositories[0]);

        var migrated = result.Repositories[1];
        Assert.False(migrated.HasLegacySshKey);
        Assert.Null(migrated.LegacySshKeyFile);
        Assert.True(migrated.SshKeyRestricted);
        var key = _vault.GetKey(migrated.SshKeyId!.Value)!;
        Assert.Equal("NAS", key.Name);
        Assert.True(key.Generated);
        Assert.Equal(pair.PrivateKeyPem, key.PrivateKey);
        Assert.Equal(pair.PublicKeyLine, key.PublicKey);
        Assert.Equal(new DateTimeOffset(2026, 11, 4, 14, 9, 49, TimeSpan.Zero), key.Created);
    }

    [Fact]
    public async Task One_file_used_by_several_repositories_becomes_one_key()
    {
        var file = OwnKeyFile("shared", out _);

        var result = await MigrateAsync(Repository("A", file, managed: true), Repository("B", file, managed: true));

        Assert.Equal(1, result.Moved);
        Assert.Single(_vault.Keys);
        Assert.Equal(result.Repositories[0].SshKeyId, result.Repositories[1].SshKeyId);
    }

    [Fact]
    public async Task A_missing_file_is_reported_and_the_repository_kept_as_it_was()
    {
        var repository = Repository("Gone", _directory.Combine("does-not-exist"), managed: true);

        var result = await MigrateAsync(repository);

        Assert.Equal(["Gone"], result.Failed);
        Assert.Same(repository, result.Repositories[0]);
        Assert.Empty(_vault.Keys);
    }

    [SshKeygenFact]
    public async Task Keys_from_elsewhere_are_copied_and_left_in_place()
    {
        var pair = SshKeyPair.GenerateEd25519("me@laptop");
        var file = _directory.Combine("id_ed25519");
        File.WriteAllText(file, pair.PrivateKeyPem);

        var result = await MigrateAsync(Repository("NAS", file, managed: false));

        Assert.Empty(result.FilesToDelete);
        Assert.True(File.Exists(file));
        var key = _vault.GetKey(result.Repositories[0].SshKeyId!.Value)!;
        Assert.Equal("id_ed25519", key.Name);
        Assert.False(key.Generated);
        Assert.Equal(SshPublicKey.WithoutComment(pair.PublicKeyLine), SshPublicKey.WithoutComment(key.PublicKey));
    }
}
