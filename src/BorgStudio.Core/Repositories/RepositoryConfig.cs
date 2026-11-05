using System.Text.Json.Serialization;

namespace BorgStudio.Core.Repositories;

/// <summary>How the passphrase of a repository is obtained.</summary>
public enum PassphraseMode
{
    /// <summary>Unencrypted repository: no passphrase.</summary>
    None,

    /// <summary>Kept in the system keychain (see <see cref="Secrets.ISecretStore"/>).</summary>
    Stored,

    /// <summary>Asked for every time it is needed.</summary>
    Ask,

    /// <summary>Kept in BorgStudio's PIN-protected vault (see <see cref="Security.Vault"/>).</summary>
    Vault,
}

/// <summary>Which borg major version a repository is used with.</summary>
public enum BorgVersionPreference
{
    /// <summary>Detected from the repository where possible, otherwise borg 1.</summary>
    Auto,
    Borg1,
    Borg2,
}

/// <summary>
/// A repository known to BorgStudio. Stored in repositories.json – never contains secrets.
/// </summary>
public sealed record RepositoryConfig
{
    public required Guid Id { get; init; }

    public required string Name { get; init; }

    /// <summary><see cref="BorgStudio.Plugins.IRepositoryProvider.Id"/> of the provider the location comes from.</summary>
    public required string ProviderId { get; init; }

    /// <summary>The values entered for the provider's fields (non-secret).</summary>
    public required IReadOnlyDictionary<string, string> ProviderValues { get; init; }

    public PassphraseMode PassphraseMode { get; init; } = PassphraseMode.Ask;

    public BorgVersionPreference BorgVersion { get; init; } = BorgVersionPreference.Auto;

    /// <summary>borg encryption mode as reported by borg, e.g. "repokey-blake2"; <c>null</c> until known.</summary>
    public string? EncryptionMode { get; init; }

    /// <summary>borg's repository id, to notice when a location suddenly holds a different repository.</summary>
    public string? BorgRepositoryId { get; init; }

    /// <summary>SSH repositories: the key in the vault (<see cref="Ssh.SshKeyEntry.Id"/>) borg logs in with.</summary>
    public Guid? SshKeyId { get; init; }

    /// <summary>The key was installed restricted to <c>borg serve</c> for this repository only.</summary>
    public bool SshKeyRestricted { get; init; }

    /// <summary>Older versions: the key file borg logged in with. Moved into the vault, then cleared.</summary>
    [JsonPropertyName("sshKeyFile")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacySshKeyFile { get; init; }

    /// <summary>Older versions: the key file was created by BorgStudio (and can be deleted once moved).</summary>
    [JsonPropertyName("sshKeyManaged")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool LegacySshKeyManaged { get; init; }

    /// <summary>The repository still uses a key file from an older version.</summary>
    [JsonIgnore]
    public bool HasLegacySshKey => LegacySshKeyFile is not null && SshKeyId is null;

    /// <summary>Key of the stored passphrase in the keychain.</summary>
    [JsonIgnore]
    public string SecretKey => $"repository/{Id:N}";
}
