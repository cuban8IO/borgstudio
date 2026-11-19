using BorgStudio.Core.Secrets;
using BorgStudio.Core.Security;

namespace BorgStudio.Core.Notifications;

/// <summary>
/// The SMTP password and the webhook's token – kept like the repositories' passphrases: in the vault when a PIN is set
/// up (scheduled runs open it with their own key), otherwise in the system keychain.
/// </summary>
public sealed class NotificationSecrets(Vault vault, ISecretStore keychain)
{
    public const string SmtpPassword = "notify-smtp-password";

    public const string WebhookHeader = "notify-webhook-header";

    /// <summary>The token the incoming webhook (<see cref="Trigger.TriggerServer"/>) asks for – kept the same way.</summary>
    public const string TriggerToken = "trigger-token";

    /// <summary>Where new secrets go: the vault (if set up) or the keychain.</summary>
    public bool UsesVault => vault.State != VaultState.NotSetUp;

    /// <summary>Whether a secret can be stored here at all (a vault, or a keychain).</summary>
    public bool CanStore => UsesVault || keychain.IsAvailable;

    /// <summary>The stored secret; <c>null</c> if there is none or it cannot be read now (locked vault, keychain error).</summary>
    public string? Get(string name)
    {
        if (vault.State == VaultState.Unlocked && vault.GetSecret(name) is { } fromVault)
            return fromVault;
        if (!keychain.IsAvailable)
            return null;
        try
        {
            return keychain.Get(name);
        }
        catch (SecretStoreException)
        {
            return null;
        }
    }

    /// <summary>Stores <paramref name="value"/>, or removes the secret if it is empty.</summary>
    /// <exception cref="VaultLockedException">A PIN is set up and the vault is locked.</exception>
    /// <exception cref="SecretStoreException">The keychain could not be written.</exception>
    public void Set(string name, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            Remove(name);
            return;
        }

        if (UsesVault)
        {
            vault.SetSecret(name, value);
            // An older copy in the keychain (from before the PIN) is no longer needed.
            RemoveFromKeychain(name);
        }
        else
        {
            keychain.Set(name, $"BorgStudio – {name}", value);
        }
    }

    /// <exception cref="VaultLockedException">A PIN is set up and the vault is locked.</exception>
    public void Remove(string name)
    {
        if (UsesVault)
            vault.RemoveSecret(name);
        RemoveFromKeychain(name);
    }

    private void RemoveFromKeychain(string name)
    {
        if (!keychain.IsAvailable)
            return;
        try
        {
            keychain.Delete(name);
        }
        catch (SecretStoreException)
        {
            // Nothing stored there.
        }
    }
}
