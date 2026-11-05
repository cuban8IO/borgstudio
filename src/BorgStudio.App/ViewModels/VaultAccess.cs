using BorgStudio.App.Resources;
using BorgStudio.App.Services;
using BorgStudio.Core.Repositories;
using BorgStudio.Core.Secrets;
using BorgStudio.Core.Security;

namespace BorgStudio.App.ViewModels;

/// <summary>Opens the vault when something needs it: asks for the PIN, or to set one up first.</summary>
public static class VaultAccess
{
    /// <summary><c>true</c> once the vault is unlocked; <c>false</c> if the user cancelled.</summary>
    /// <exception cref="SecretBoxException">The vault file is damaged.</exception>
    public static async Task<bool> EnsureUnlockedAsync(Vault vault, IDialogService dialogs)
    {
        switch (vault.State)
        {
            case VaultState.Unlocked:
                vault.Touch();
                return true;

            case VaultState.NotSetUp:
                if (await dialogs.AskNewPinAsync() is not { } newPin)
                    return false;
                // Key derivation takes a moment: keep the UI responsive.
                await Task.Run(() => vault.SetUp(newPin));
                return true;

            default:
                string? error = null;
                while (true)
                {
                    if (await dialogs.AskPinAsync(error) is not { } pin)
                        return false;
                    if (await Task.Run(() => vault.Unlock(pin)))
                        return true;
                    error = Strings.PinWrong;
                }
        }
    }
}

/// <summary>Reads, writes and removes repository passphrases where the repository keeps them: vault or keychain.</summary>
public sealed class PassphraseStore(AppServices services)
{
    /// <summary>The stored passphrase; <c>null</c> if none is stored. <c>Cancelled</c> if the vault stayed locked.</summary>
    /// <returns>An error text in <c>Error</c> if it could not be read.</returns>
    public async Task<(string? Passphrase, bool Cancelled, string? Error)> GetAsync(
        RepositoryConfig repository, PassphraseMode mode, IDialogService dialogs)
    {
        try
        {
            switch (mode)
            {
                case PassphraseMode.Vault:
                    if (!await VaultAccess.EnsureUnlockedAsync(services.Vault, dialogs))
                        return (null, true, null);
                    return (services.Vault.GetPassphrase(repository.Id), false, null);
                case PassphraseMode.Stored:
                    return (services.SecretStore.Get(repository.SecretKey), false, null);
                default:
                    return (null, false, null);
            }
        }
        catch (Exception exception) when (exception is SecretStoreException or SecretBoxException)
        {
            return (null, false, Describe(exception));
        }
    }

    /// <summary>Stores the passphrase; an error text if that failed or the vault stayed locked, otherwise <c>null</c>.</summary>
    public async Task<string?> SetAsync(RepositoryConfig repository, PassphraseMode mode,
        string passphrase, IDialogService dialogs)
    {
        try
        {
            switch (mode)
            {
                case PassphraseMode.Vault:
                    if (!await VaultAccess.EnsureUnlockedAsync(services.Vault, dialogs))
                        return Strings.VaultNotUnlocked;
                    services.Vault.SetPassphrase(repository.Id, passphrase);
                    break;
                case PassphraseMode.Stored:
                    services.SecretStore.Set(repository.SecretKey, BorgTexts.Format(Strings.SecretLabel, repository.Name), passphrase);
                    break;
            }

            return null;
        }
        catch (Exception exception) when (exception is SecretStoreException or SecretBoxException)
        {
            return Describe(exception);
        }
    }

    /// <summary>
    /// Removes a stored passphrase. Never asks for the PIN: a passphrase left in the locked vault is cleaned up the next
    /// time it is unlocked (<see cref="Vault.RemovePassphrasesExcept"/>).
    /// </summary>
    public string? Delete(RepositoryConfig repository, PassphraseMode mode)
    {
        try
        {
            switch (mode)
            {
                case PassphraseMode.Vault when services.Vault.State == VaultState.Unlocked:
                    services.Vault.RemovePassphrase(repository.Id);
                    break;
                case PassphraseMode.Stored:
                    services.SecretStore.Delete(repository.SecretKey);
                    break;
            }

            return null;
        }
        catch (Exception exception) when (exception is SecretStoreException or SecretBoxException)
        {
            return Describe(exception);
        }
    }

    private static string Describe(Exception exception) => exception is SecretBoxException
        ? BorgTexts.Format(Strings.VaultError, exception.Message)
        : BorgTexts.Format(Strings.KeychainError, exception.Message);
}
