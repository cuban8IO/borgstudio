namespace BorgStudio.Core.Secrets;

/// <summary>Stores secrets (repository passphrases) in the keychain of the operating system.</summary>
public interface ISecretStore
{
    /// <summary>Whether this system has a usable keychain (e.g. not on Linux without a Secret Service).</summary>
    bool IsAvailable { get; }

    /// <summary>The secret stored under <paramref name="key"/>, or <c>null</c> if there is none.</summary>
    /// <exception cref="SecretStoreException">The keychain could not be read.</exception>
    string? Get(string key);

    /// <summary>Stores or replaces a secret.</summary>
    /// <param name="label">Human-readable description, shown by keychain apps where supported.</param>
    /// <exception cref="SecretStoreException">The keychain could not be written.</exception>
    void Set(string key, string label, string secret);

    /// <summary>Removes a secret; does nothing if there is none.</summary>
    /// <exception cref="SecretStoreException">The keychain could not be written.</exception>
    void Delete(string key);
}

public sealed class SecretStoreException(string message) : Exception(message);

public static class SecretStores
{
    /// <summary>Service / prefix under which BorgStudio's secrets appear in the keychain.</summary>
    public const string ServiceName = "BorgStudio";

    public static ISecretStore CreateDefault() =>
        OperatingSystem.IsWindows() ? new WindowsCredentialStore()
        : OperatingSystem.IsMacOS() ? new MacKeychainStore()
        : new LibSecretStore();
}
