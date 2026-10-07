namespace BorgStudio.Plugins;

/// <summary>
/// A place where borg repositories can live: a local folder, an SSH server, a storage provider, ...
/// The provider describes the input it needs and turns that input into a borg repository location.
/// </summary>
/// <remarks>
/// Texts (<see cref="DisplayName"/>, field labels, validation messages) are shown to the user as they are,
/// so return them in <see cref="System.Globalization.CultureInfo.CurrentUICulture"/>.
/// </remarks>
public interface IRepositoryProvider
{
    /// <summary>Stable, unique identifier stored with each repository, e.g. "local" or "acme-backup". Never change it.</summary>
    string Id { get; }

    /// <summary>Name shown to the user, e.g. "Local folder".</summary>
    string DisplayName { get; }

    /// <summary>One sentence explaining when to use this provider.</summary>
    string Description { get; }

    /// <summary>The input the user has to enter, in display order.</summary>
    IReadOnlyList<ProviderField> Fields { get; }

    /// <summary>
    /// Checks the entered values (keyed by <see cref="ProviderField.Key"/>).
    /// Returns error messages for the user; empty if everything is fine.
    /// </summary>
    IReadOnlyList<string> Validate(IReadOnlyDictionary<string, string> values);

    /// <summary>Builds the borg repository location from valid values.</summary>
    RepositoryLocation GetLocation(IReadOnlyDictionary<string, string> values);

    /// <summary>
    /// Whether repositories are reached over SSH. BorgStudio then asks how to log in (new key or existing key)
    /// and expects <see cref="RepositoryLocation.Ssh"/> to be set.
    /// </summary>
    bool UsesSsh => false;
}
