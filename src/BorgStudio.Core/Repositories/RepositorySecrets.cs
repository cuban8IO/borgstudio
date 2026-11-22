using BorgStudio.Core.Security;
using BorgStudio.Plugins;

namespace BorgStudio.Core.Repositories;

/// <summary>
/// The values of a provider's <see cref="ProviderFieldKind.Secret"/> fields (e.g. an API token): kept in the vault under
/// "repository/&lt;id&gt;/&lt;key&gt;", never with the repository itself.
/// </summary>
public static class RepositorySecrets
{
    public const string Prefix = "repository/";

    public static string Name(Guid repositoryId, string key) => $"{Prefix}{repositoryId:N}/{key}";

    /// <summary>The keys of the provider's secret fields.</summary>
    public static IReadOnlyList<string> Keys(IRepositoryProvider? provider) =>
        provider?.Fields.Where(field => field.Kind == ProviderFieldKind.Secret).Select(field => field.Key).ToList() ?? [];

    /// <summary>The repository's secrets that can be read now (none while the vault is locked).</summary>
    public static IReadOnlyDictionary<string, string> Read(Vault vault, RepositoryConfig repository, IRepositoryProvider? provider)
    {
        var secrets = new Dictionary<string, string>();
        foreach (var key in Keys(provider))
        {
            try
            {
                if (vault.GetSecret(Name(repository.Id, key)) is { Length: > 0 } value)
                    secrets[key] = value;
            }
            catch (VaultLockedException)
            {
                break;
            }
        }
        return secrets;
    }

    /// <summary>Stores the entered secrets; an empty one is removed.</summary>
    /// <exception cref="VaultLockedException" />
    public static void Write(Vault vault, Guid repositoryId, IReadOnlyDictionary<string, string> secrets)
    {
        foreach (var (key, value) in secrets)
        {
            if (value.Length > 0)
                vault.SetSecret(Name(repositoryId, key), value);
            else
                vault.RemoveSecret(Name(repositoryId, key));
        }
    }

    /// <summary>Removes all secrets of the repository.</summary>
    /// <exception cref="VaultLockedException" />
    public static void Remove(Vault vault, Guid repositoryId, IRepositoryProvider? provider)
    {
        foreach (var key in Keys(provider))
            vault.RemoveSecret(Name(repositoryId, key));
    }
}
