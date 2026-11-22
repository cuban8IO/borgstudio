namespace BorgStudio.Plugins;

/// <summary>
/// Optional for an SSH <see cref="IRepositoryProvider"/> whose service sets up login keys (and repositories) through its
/// own API instead of a password login – return <see cref="SshKeyInstallation.Provider"/> from the endpoint. BorgStudio
/// calls it with the login key before it first reaches the repository.
/// </summary>
public interface ISshKeyProvisioner
{
    /// <summary>
    /// Registers the key with the service and gives it access to the repository – creating the repository there first
    /// if <see cref="SshKeyProvisionRequest.CreateNew"/> and it does not exist. Returns the values to keep (e.g. the
    /// repository's address and the server's host key fingerprints), or <c>null</c> if the service cannot be asked
    /// (e.g. no API token): BorgStudio then shows the public key for the user to add by hand.
    /// Throws <see cref="RepositoryUnavailableException"/> with a message for the user when the service refuses.
    /// </summary>
    Task<IReadOnlyDictionary<string, string>?> ProvisionAsync(SshKeyProvisionRequest request, CancellationToken cancellationToken);
}

/// <summary>What a key is set up with.</summary>
/// <param name="Values">The values the user entered (keyed by <see cref="ProviderField.Key"/>), without secrets.</param>
/// <param name="Secrets">The values of <see cref="ProviderFieldKind.Secret"/> fields (keyed the same way); missing when empty.</param>
/// <param name="PublicKey">The login key, as one line of <c>authorized_keys</c> ("ssh-ed25519 AAAA… comment").</param>
/// <param name="KeyName">A name for the key at the service, e.g. "borgstudio-laptop-1a2b3c4d".</param>
/// <param name="RepositoryName">The name the user gave the repository in BorgStudio.</param>
/// <param name="CreateNew">The user creates a new repository (borg init follows) rather than connecting an existing one.</param>
public sealed record SshKeyProvisionRequest(
    IReadOnlyDictionary<string, string> Values,
    IReadOnlyDictionary<string, string> Secrets,
    string PublicKey,
    string KeyName,
    string RepositoryName,
    bool CreateNew);
