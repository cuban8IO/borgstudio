namespace BorgStudio.Plugins;

/// <summary>
/// Optional for an <see cref="IRepositoryProvider"/> whose repositories have to be found or connected before borg can use
/// them – a drive that may get another letter, a share to mount, a network disk to log in to. BorgStudio connects before
/// every use of the repository and disconnects when nothing uses it any more; while connected, borg works with
/// <see cref="RepositoryConnection.Location"/> instead of <see cref="IRepositoryProvider.GetLocation"/>.
/// </summary>
/// <remarks>Providers without this interface are used as before: <see cref="IRepositoryProvider.GetLocation"/> is the location.</remarks>
public interface IRepositoryConnector
{
    /// <summary>
    /// Finds or connects the repository. Throws <see cref="RepositoryUnavailableException"/> with a message for the user if
    /// it cannot be reached now (e.g. the drive is not attached).
    /// </summary>
    Task<RepositoryConnection> ConnectAsync(RepositoryConnectRequest request, CancellationToken cancellationToken);
}

/// <summary>What BorgStudio knows when it connects a repository.</summary>
/// <param name="Values">The values the user entered (keyed by <see cref="ProviderField.Key"/>).</param>
public sealed record RepositoryConnectRequest(IReadOnlyDictionary<string, string> Values)
{
    /// <summary>
    /// The borg repository ID (hex, as <c>borg info --json</c> reports it and the repository's <c>config</c> file holds it)
    /// – known once BorgStudio has reached the repository; <c>null</c> before.
    /// </summary>
    public string? RepositoryId { get; init; }

    /// <summary>Whether a run without window (a scheduled backup or check) connects – nobody is there to answer questions.</summary>
    public bool Unattended { get; init; }
}

/// <summary>A connected repository; BorgStudio disconnects it through <see cref="DisconnectAsync"/>.</summary>
/// <param name="location">Where borg finds the repository now.</param>
/// <param name="disconnect">
/// Undoes the connection (unmount, log out, eject …); returns a message for the log if that did not work. <c>null</c>:
/// nothing to undo.
/// </param>
public sealed class RepositoryConnection(RepositoryLocation location, Func<Task<string?>>? disconnect = null)
{
    /// <summary>Where borg finds the repository while it is connected.</summary>
    public RepositoryLocation Location { get; } = location;

    /// <summary>
    /// Values to keep instead of the entered ones, e.g. the folder where a moved drive's repository was found;
    /// <c>null</c>: unchanged.
    /// </summary>
    public IReadOnlyDictionary<string, string>? UpdatedValues { get; init; }

    /// <summary>Undoes the connection; a message for the log if that did not work, otherwise <c>null</c>.</summary>
    public Task<string?> DisconnectAsync() => disconnect?.Invoke() ?? Task.FromResult<string?>(null);
}

/// <summary>The repository cannot be reached now; the message is shown to the user as it is.</summary>
public sealed class RepositoryUnavailableException(string message) : Exception(message);
