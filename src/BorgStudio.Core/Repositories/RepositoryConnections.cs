using BorgStudio.Core.Plugins;
using BorgStudio.Plugins;

namespace BorgStudio.Core.Repositories;

/// <summary>
/// Connects repositories whose provider has to find or connect them first (<see cref="IRepositoryConnector"/>) and
/// disconnects them when the last user is done. Uses of the same repository share one connection.
/// </summary>
/// <param name="store">Keeps values a connection updated (e.g. the folder a moved drive's repository was found in).</param>
public sealed class RepositoryConnections(PluginCatalog plugins, RepositoryStore? store = null)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<Guid, Shared> _connected = [];

    /// <summary>
    /// The repository's location for borg, connected if its provider needs that. <c>null</c> if its provider is gone or its
    /// values are not usable. Throws <see cref="RepositoryUnavailableException"/> if it cannot be reached now.
    /// </summary>
    /// <param name="unattended">A run without window connects (see <see cref="RepositoryConnectRequest.Unattended"/>).</param>
    public async Task<RepositoryLease?> ConnectAsync(RepositoryConfig repository, bool unattended = false,
        CancellationToken cancellationToken = default)
    {
        if (plugins.FindRepositoryProvider(repository.ProviderId) is not { } provider)
            return null;
        if (provider is not IRepositoryConnector connector)
            return Location(provider, repository) is { } location ? new RepositoryLease(repository, location, null) : null;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!_connected.TryGetValue(repository.Id, out var shared))
            {
                var connection = await ConnectAsync(connector, repository, unattended, cancellationToken);
                shared = new Shared(connection, Keep(repository, connection.UpdatedValues));
                _connected[repository.Id] = shared;
            }
            shared.Users++;
            return new RepositoryLease(shared.Repository, shared.Connection.Location, () => ReleaseAsync(repository.Id, shared));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Whether something uses the repository's connection right now.</summary>
    public bool IsConnected(Guid repositoryId)
    {
        _gate.Wait();
        try
        {
            return _connected.ContainsKey(repositoryId);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static RepositoryLocation? Location(IRepositoryProvider provider, RepositoryConfig repository)
    {
        try
        {
            return provider.GetLocation(repository.ProviderValues);
        }
        catch (Exception exception) when (exception is KeyNotFoundException or ArgumentException or FormatException)
        {
            return null;
        }
    }

    private static async Task<RepositoryConnection> ConnectAsync(IRepositoryConnector connector, RepositoryConfig repository, bool unattended,
        CancellationToken cancellationToken)
    {
        var request = new RepositoryConnectRequest(repository.ProviderValues) { RepositoryId = repository.BorgRepositoryId, Unattended = unattended };
        try
        {
            return await connector.ConnectAsync(request, cancellationToken);
        }
        catch (Exception exception) when (exception is not (RepositoryUnavailableException or OperationCanceledException))
        {
            // A plugin's own failure: shown like an unreachable repository, never crashing the app.
            throw new RepositoryUnavailableException(exception.Message);
        }
    }

    /// <summary>The repository with the values a connection updated – saved, so the overview and the next run use them.</summary>
    private RepositoryConfig Keep(RepositoryConfig repository, IReadOnlyDictionary<string, string>? values)
    {
        if (values is null || SameValues(values, repository.ProviderValues))
            return repository;
        var updated = repository with { ProviderValues = new Dictionary<string, string>(values) };
        if (store is not null)
        {
            var repositories = store.Load().Repositories;
            if (repositories.Any(candidate => candidate.Id == repository.Id))
                store.Save(repositories.Select(candidate => candidate.Id == repository.Id ? candidate with { ProviderValues = updated.ProviderValues } : candidate));
        }
        return updated;
    }

    private static bool SameValues(IReadOnlyDictionary<string, string> a, IReadOnlyDictionary<string, string> b) =>
        a.Count == b.Count && a.All(pair => b.TryGetValue(pair.Key, out var value) && value == pair.Value);

    private async Task<string?> ReleaseAsync(Guid repositoryId, Shared shared)
    {
        await _gate.WaitAsync();
        try
        {
            if (--shared.Users > 0)
                return null;
            _connected.Remove(repositoryId);
            try
            {
                return await shared.Connection.DisconnectAsync();
            }
            catch (Exception exception)
            {
                return exception.Message;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private sealed class Shared(RepositoryConnection connection, RepositoryConfig repository)
    {
        public RepositoryConnection Connection { get; } = connection;

        public RepositoryConfig Repository { get; } = repository;

        public int Users { get; set; }
    }
}

/// <summary>A use of a repository's connection; releasing the last one disconnects it.</summary>
public sealed class RepositoryLease : IAsyncDisposable
{
    private Func<Task<string?>>? _release;

    internal RepositoryLease(RepositoryConfig repository, RepositoryLocation location, Func<Task<string?>>? release)
    {
        Repository = repository;
        Location = location;
        _release = release;
    }

    /// <summary>The repository as connected – with the values the connection updated, if any.</summary>
    public RepositoryConfig Repository { get; }

    /// <summary>Where borg finds the repository now.</summary>
    public RepositoryLocation Location { get; }

    /// <summary>Releases this use (once); a message for the log if disconnecting did not work.</summary>
    public Task<string?> ReleaseAsync() => Interlocked.Exchange(ref _release, null)?.Invoke() ?? Task.FromResult<string?>(null);

    public async ValueTask DisposeAsync() => await ReleaseAsync();
}
