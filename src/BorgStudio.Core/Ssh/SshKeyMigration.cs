using BorgStudio.Core.Repositories;
using BorgStudio.Core.Security;

namespace BorgStudio.Core.Ssh;

/// <param name="Repositories">All repositories, migrated ones now referring to their key in the vault.</param>
/// <param name="Moved">Number of keys moved into the vault.</param>
/// <param name="Failed">Repositories whose key file could not be read (missing, password-protected, …).</param>
/// <param name="FilesToDelete">BorgStudio's own key files that are in the vault now; delete them once the repositories are saved.</param>
public sealed record SshKeyMigrationResult(
    IReadOnlyList<RepositoryConfig> Repositories, int Moved, IReadOnlyList<string> Failed, IReadOnlyList<string> FilesToDelete);

/// <summary>
/// Moves the key files of older versions into the vault: BorgStudio's own files (which are deleted afterwards) and
/// keys the user picked elsewhere (copied – the original stays where it is). A file used by several repositories
/// becomes one key.
/// </summary>
public static class SshKeyMigration
{
    /// <exception cref="VaultLockedException" />
    public static async Task<SshKeyMigrationResult> MigrateAsync(IReadOnlyList<RepositoryConfig> repositories, Vault vault,
        SshKeyStore store, SshKeygen keygen, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var keyIds = new Dictionary<string, Guid>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var migrated = new List<RepositoryConfig>();
        var failed = new List<string>();
        var filesToDelete = new List<string>();
        var moved = 0;

        foreach (var repository in repositories)
        {
            if (!repository.HasLegacySshKey)
            {
                migrated.Add(repository);
                continue;
            }

            var file = Path.GetFullPath(repository.LegacySshKeyFile!);
            if (!keyIds.TryGetValue(file, out var keyId))
            {
                var managed = repository.LegacySshKeyManaged && store.Contains(file);
                var key = await ReadAsync(file, managed, repository.Name, store, keygen, now, cancellationToken);
                if (key is null)
                {
                    failed.Add(repository.Name);
                    migrated.Add(repository);
                    continue;
                }

                vault.SetKey(key);
                keyId = keyIds[file] = key.Id;
                moved++;
                if (managed)
                    filesToDelete.Add(file);
            }

            migrated.Add(repository with { SshKeyId = keyId, LegacySshKeyFile = null, LegacySshKeyManaged = false });
        }

        return new SshKeyMigrationResult(migrated, moved, failed, filesToDelete);
    }

    private static async Task<SshKeyEntry?> ReadAsync(string file, bool managed, string repositoryName, SshKeyStore store,
        SshKeygen keygen, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!File.Exists(file))
            return null;

        // BorgStudio's own keys come with their .pub; everything else is read by ssh-keygen.
        string privateKey, publicKey;
        if (managed && File.Exists(file + ".pub"))
        {
            privateKey = await File.ReadAllTextAsync(file, cancellationToken);
            publicKey = (await File.ReadAllTextAsync(file + ".pub", cancellationToken)).Trim();
        }
        else
        {
            var read = await keygen.ReadAsync(file, null, store.Directory, cancellationToken);
            if (read.Status != SshKeyReadStatus.Success)
                return null;
            (privateKey, publicKey) = (read.PrivateKey!, read.PublicKey!);
        }

        return new SshKeyEntry
        {
            Id = Guid.NewGuid(),
            Name = managed ? repositoryName : Path.GetFileName(file),
            PrivateKey = privateKey,
            PublicKey = publicKey,
            // When the key was made, not when it moved.
            Created = File.GetLastWriteTimeUtc(file) is var written && written.Year > 1601 ? new DateTimeOffset(written) : now,
            Generated = managed,
        };
    }
}
