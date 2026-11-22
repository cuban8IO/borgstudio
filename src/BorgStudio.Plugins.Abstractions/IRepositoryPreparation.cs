namespace BorgStudio.Plugins;

/// <summary>
/// Optional for an <see cref="IRepositoryProvider"/> that has to set something up on this computer once before it can
/// connect – e.g. an iSCSI target that later runs without administrator rights. BorgStudio calls it when the user saves the
/// repository in the editor, so it may ask for those rights then (scheduled runs cannot ask).
/// </summary>
/// <remarks>
/// A provider implementing this and <see cref="IRepositoryConnector"/> is connected by the editor itself (with no
/// repository ID yet), so the repository can be created or found on what was connected.
/// </remarks>
public interface IRepositoryPreparation
{
    /// <summary>
    /// Sets up what connecting needs, if it is not set up yet (or the values changed). Returns the values to keep (e.g. a
    /// marker of what was set up). Throws <see cref="RepositoryUnavailableException"/> with a message for the user when it
    /// cannot (e.g. the rights were refused).
    /// </summary>
    Task<IReadOnlyDictionary<string, string>> PrepareAsync(RepositoryPreparationRequest request, CancellationToken cancellationToken);
}

/// <summary>What a repository is prepared with.</summary>
/// <param name="Values">The values the user entered, without secrets.</param>
/// <param name="Secrets">The values of <see cref="ProviderFieldKind.Secret"/> fields; missing when empty.</param>
/// <param name="RepositoryId">BorgStudio's id of the repository (stable; e.g. to name what is set up for it).</param>
public sealed record RepositoryPreparationRequest(
    IReadOnlyDictionary<string, string> Values,
    IReadOnlyDictionary<string, string> Secrets,
    Guid RepositoryId);
