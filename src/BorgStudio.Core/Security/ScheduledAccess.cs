using BorgStudio.Core.Secrets;

namespace BorgStudio.Core.Security;

/// <summary>
/// Lets scheduled runs open the PIN-protected vault without anyone typing the PIN: the vault's key is kept in the
/// system keychain (Windows Credential Manager, macOS keychain, Secret Service) – or, where there is none, in a file
/// only the user may read. Protection is then that of the user's account; the app itself stays locked by the PIN.
/// Kept up to date when the PIN changes, removed when the vault is reset.
/// </summary>
public sealed class ScheduledAccess
{
    /// <summary>Name of the keychain entry.</summary>
    public const string SecretKey = "scheduled-access";

    private const string Label = "BorgStudio – key for scheduled backups";

    private readonly Vault _vault;
    private readonly ISecretStore _keychain;

    public ScheduledAccess(Vault vault, ISecretStore keychain, string fallbackFile)
    {
        _vault = vault;
        _keychain = keychain;
        FallbackFile = fallbackFile;
        vault.KeyChanged += (_, _) => Refresh();
        vault.StateChanged += (_, _) =>
        {
            if (_vault.State == VaultState.NotSetUp)
                Disable();
        };
    }

    /// <summary>Where the key goes when the system has no keychain.</summary>
    public string FallbackFile { get; }

    /// <summary>Whether scheduled runs can open the vault (or don't need to: no PIN set up).</summary>
    public bool IsEnabled => _vault.State == VaultState.NotSetUp || ReadKey() is not null;

    /// <summary>The key lives in a file rather than a keychain (shown to the user).</summary>
    public bool UsesFile => !_keychain.IsAvailable;

    /// <summary>Stores the vault's key for scheduled runs. The vault must be unlocked.</summary>
    /// <exception cref="VaultLockedException" />
    /// <exception cref="SecretStoreException">The keychain could not be written.</exception>
    public void Enable() => Store(_vault.ExportKey());

    /// <summary>Removes the stored key: scheduled runs can no longer open the vault.</summary>
    public void Disable()
    {
        if (_keychain.IsAvailable)
        {
            try
            {
                _keychain.Delete(SecretKey);
            }
            catch (SecretStoreException)
            {
                // Nothing stored, or the keychain is gone: nothing left to remove.
            }
        }
        if (File.Exists(FallbackFile))
            File.Delete(FallbackFile);
    }

    /// <summary>
    /// Opens the vault with the stored key; <c>true</c> if it is open afterwards (or was already). <c>false</c> if no key
    /// is stored or it no longer fits.
    /// </summary>
    public bool TryUnlock()
    {
        if (_vault.State == VaultState.Unlocked)
            return true;
        return ReadKey() is { } key && _vault.UnlockWithKey(key);
    }

    private void Refresh()
    {
        // Only where scheduled runs were allowed: the key follows the new PIN.
        if (ReadKey() is not null)
            Store(_vault.ExportKey());
    }

    private void Store(byte[] key)
    {
        var text = Convert.ToBase64String(key);
        if (_keychain.IsAvailable)
        {
            _keychain.Set(SecretKey, Label, text);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(FallbackFile))!);
        // Created empty and restricted first, so the key is never readable by others, not even for a moment.
        File.WriteAllText(FallbackFile, "");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(FallbackFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.WriteAllText(FallbackFile, text);
    }

    private byte[]? ReadKey()
    {
        string? text = null;
        if (_keychain.IsAvailable)
        {
            try
            {
                text = _keychain.Get(SecretKey);
            }
            catch (SecretStoreException)
            {
                text = null;
            }
        }
        else if (File.Exists(FallbackFile))
        {
            text = File.ReadAllText(FallbackFile).Trim();
        }

        try
        {
            return string.IsNullOrEmpty(text) ? null : Convert.FromBase64String(text);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
