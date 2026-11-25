using BorgStudio.Core.Secrets;
using BorgStudio.Core.Security;
using BorgStudio.Providers.Server;

namespace BorgStudio.App.Services;

/// <summary>
/// The BorgStudio Server tokens in the vault. While the window's vault is locked they are read from a copy opened with the
/// key for scheduled runs (if that is allowed), so runs in the background can still report to the server.
/// </summary>
/// <param name="scheduledKeyFile">Where the key for scheduled runs lives when there is no keychain.</param>
public sealed class VaultServerSecrets(Vault vault, ISecretStore keychain, string scheduledKeyFile) : IServerSecrets
{
    public string? Get(string name)
    {
        try
        {
            if (vault.State == VaultState.Unlocked)
                return vault.GetSecret(name);
        }
        catch (VaultLockedException)
        {
            // Locked in the meantime: the key for scheduled runs it is.
        }

        if (vault.State == VaultState.NotSetUp)
            return null;
        var copy = new Vault(vault.FilePath);
        try
        {
            return new ScheduledAccess(copy, keychain, scheduledKeyFile).TryUnlock() ? copy.GetSecret(name) : null;
        }
        catch (Exception exception) when (exception is VaultLockedException or SecretBoxException or SecretStoreException or IOException)
        {
            return null;
        }
        finally
        {
            copy.Lock();
        }
    }

    /// <exception cref="VaultLockedException" />
    public void Set(string name, string value) => vault.SetSecret(name, value);

    /// <exception cref="VaultLockedException" />
    public void Remove(string name) => vault.RemoveSecret(name);
}
