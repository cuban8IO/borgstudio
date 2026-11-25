using System.Globalization;
using System.Text.RegularExpressions;
using BorgStudio.Plugins;
using BorgStudio.Providers.Server.Resources;

namespace BorgStudio.Providers.Server;

/// <summary>Built into BorgStudio, but registered through the same plugin API as external providers.</summary>
public sealed class ServerProviderPlugin(ServerAccounts accounts, ServerHttp? http = null) : IBorgStudioPlugin
{
    public void Register(IPluginRegistrar registrar) => registrar.AddRepositoryProvider(new ServerProvider(accounts, http ?? new ServerHttp()));
}

/// <summary>
/// Repository on a BorgStudio Server. The computer is paired with the server once (<see cref="ServerAccounts"/>); then
/// the provider lists and creates repositories there, registers the login key, reads usage and quota and reports runs
/// for the server's statistics. borg itself goes over the server's own SSH service.
/// </summary>
public sealed partial class ServerProvider(ServerAccounts accounts, ServerHttp http)
    : IRepositoryProvider, ISshKeyProvisioner, IStorageQuotaSource, IRunReportReceiver
{
    public const string ProviderId = "borgstudio-server";

    /// <summary>The server's address (<see cref="ServerAddress.Normalize"/>).</summary>
    public const string ServerKey = "server";

    /// <summary>The repository's id on the server; empty for one still to be created.</summary>
    public const string RepositoryIdKey = "repositoryId";

    /// <summary>"owner/name", to show.</summary>
    public const string RepositoryNameKey = "repository";

    /// <summary>For a repository to be created: its name, datastore (id, empty: the server's choice) and quota (bytes, empty: none).</summary>
    public const string NewNameKey = "newName";

    public const string NewDatastoreKey = "newDatastore";
    public const string NewQuotaKey = "newQuota";

    /// <summary>Kept by the provider: the borg address with this computer's key, and the SSH host key fingerprints.</summary>
    public const string AddressKey = "address";

    public const string HostKeysKey = "hostKeys";

    public ServerAccounts Accounts => accounts;

    public ServerHttp Http => http;

    public string Id => ProviderId;

    public string DisplayName => Strings.DisplayName;

    public string Description => Strings.Description;

    public bool UsesSsh => true;

    /// <summary>None: the editor shows the paired servers and their repositories instead.</summary>
    public IReadOnlyList<ProviderField> Fields => [];

    public IReadOnlyList<string> Validate(IReadOnlyDictionary<string, string> values)
    {
        if (ServerAddress.Normalize(Value(values, ServerKey)) is null)
            return [Strings.ServerRequired];
        return Value(values, RepositoryIdKey).Length == 0 && Value(values, NewNameKey).Length == 0 ? [Strings.RepositoryRequired] : [];
    }

    /// <exception cref="ArgumentException">No address yet: the key was never registered.</exception>
    public RepositoryLocation GetLocation(IReadOnlyDictionary<string, string> values)
    {
        var match = SshUrl().Match(Value(values, AddressKey));
        if (!match.Success)
            throw new ArgumentException("The repository on the BorgStudio Server has no address yet.", nameof(values));
        var host = match.Groups["host"].Value.Trim('[', ']');
        var port = match.Groups["port"].Success ? int.Parse(match.Groups["port"].Value, CultureInfo.InvariantCulture) : 22;
        return new RepositoryLocation(match.Value)
        {
            Ssh = new SshEndpoint(host, port, match.Groups["user"].Value, match.Groups["path"].Value)
            {
                KeyInstallation = SshKeyInstallation.Provider,
                PublishedHostKeyFingerprints = Value(values, HostKeysKey).Split(' ', StringSplitOptions.RemoveEmptyEntries),
            },
        };
    }

    /// <summary>The client for the repository's server with this computer's token.</summary>
    /// <exception cref="ServerException"><see cref="ServerProblem.NotPaired"/>.</exception>
    public ServerClient ClientFor(string server)
    {
        var url = ServerAddress.Normalize(server);
        var account = url is null ? null : accounts.Find(url);
        if (account is null || accounts.Token(account.Url) is not { } token)
            throw new ServerException(ServerProblem.NotPaired,
                string.Format(CultureInfo.CurrentCulture, account is null ? Strings.NotPaired : Strings.TokenLocked, ServerAddress.Display(url ?? server)));
        return new ServerClient(http, account.Url, token, account.PinnedCertificate);
    }

    /// <summary>The repository as the server sees it now (usage, quota, access, who else uses it).</summary>
    /// <exception cref="ServerException" />
    public Task<ServerRepository> GetRepositoryAsync(IReadOnlyDictionary<string, string> values, CancellationToken cancellationToken) =>
        Guid.TryParse(Value(values, RepositoryIdKey), out var id)
            ? ClientFor(Value(values, ServerKey)).GetRepositoryAsync(id, cancellationToken)
            : throw new ServerException(ServerProblem.NotFound, Strings.RepositoryNotFound);

    /// <summary>The page of the repository in the server's web interface.</summary>
    public static string? WebPage(IReadOnlyDictionary<string, string> values) =>
        ServerAddress.Normalize(Value(values, ServerKey)) is { } url && Guid.TryParse(Value(values, RepositoryIdKey), out var id)
            ? $"{url}repositories/{id}"
            : null;

    // --- Setting up the key (and the repository) ---

    public async Task<IReadOnlyDictionary<string, string>?> ProvisionAsync(SshKeyProvisionRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var client = ClientFor(Value(request.Values, ServerKey));
            var result = new Dictionary<string, string>();
            if (!Guid.TryParse(Value(request.Values, RepositoryIdKey), out var id))
            {
                if (!request.CreateNew || Value(request.Values, NewNameKey) is not { Length: > 0 } name)
                    throw new RepositoryUnavailableException(Strings.RepositoryRequired);
                // Created by an earlier attempt that failed later (e.g. borg init): that one, not a second.
                var created = (await client.GetRepositoriesAsync(cancellationToken))
                    .FirstOrDefault(repository => repository.IsOwner && repository.UsedBytes == 0 && repository.Name == name)
                    ?? await client.CreateRepositoryAsync(name,
                        Guid.TryParse(Value(request.Values, NewDatastoreKey), out var datastore) ? datastore : null,
                        long.TryParse(Value(request.Values, NewQuotaKey), NumberStyles.None, CultureInfo.InvariantCulture, out var quota) && quota > 0 ? quota : null,
                        cancellationToken);
                id = created.Id;
                result[RepositoryIdKey] = id.ToString();
                result[RepositoryNameKey] = created.FullName;
                // Created now: no second one if borg fails and the user tries again.
                result[NewNameKey] = "";
            }

            var registration = await client.SetSshKeyAsync(id, request.PublicKey, cancellationToken);
            result[AddressKey] = registration.Url;
            result[HostKeysKey] = string.Join(' ', registration.Ssh.HostKeys.Select(key => key.Fingerprint));
            return result;
        }
        catch (ServerException exception)
        {
            throw new RepositoryUnavailableException(exception.Message);
        }
    }

    // --- Quota ---

    /// <summary>Usage and what the repository may grow to (its quota, the owner's, the datastore's space); <c>null</c> if unknown now.</summary>
    public async Task<StorageQuota?> GetQuotaAsync(StorageQuotaRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var repository = await GetRepositoryAsync(request.Values, cancellationToken);
            return repository.Capacity is { } capacity ? new StorageQuota(repository.UsedBytes, capacity) : null;
        }
        catch (ServerException)
        {
            return null;
        }
    }

    // --- Reports for the server's statistics ---

    public async Task SendReportAsync(RunReportRequest request, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(Value(request.Values, RepositoryIdKey), out var id))
            throw new RunReportRejectedException("No repository id.");
        var report = request.Report;
        try
        {
            await ClientFor(Value(request.Values, ServerKey)).ReportAsync(id, new
            {
                kind = report.Kind.ToString().ToLowerInvariant(),
                outcome = report.Outcome.ToString().ToLowerInvariant(),
                startedAt = report.StartedAt,
                durationSeconds = Math.Max(0, report.Duration.TotalSeconds),
                archiveName = report.ArchiveName,
                originalSize = report.OriginalSize,
                compressedSize = report.CompressedSize,
                deduplicatedSize = report.DeduplicatedSize,
                files = report.Files,
                archives = report.Archives,
                totalSize = report.TotalSize,
                totalCompressedSize = report.TotalCompressedSize,
                uniqueCompressedSize = report.UniqueCompressedSize,
                message = report.Message is { Length: > 2000 } text ? text[..2000] : report.Message,
            }, cancellationToken);
        }
        catch (ServerException exception) when (exception.Problem is ServerProblem.NotFound or ServerProblem.Rejected)
        {
            throw new RunReportRejectedException(exception.Message);
        }
        catch (ServerException exception)
        {
            throw new RepositoryUnavailableException(exception.Message);
        }
    }

    private static string Value(IReadOnlyDictionary<string, string> values, string key) =>
        values.GetValueOrDefault(key)?.Trim() ?? "";

    [GeneratedRegex(@"^ssh://(?<user>[A-Za-z0-9._-]+)@(?<host>\[[0-9A-Fa-f:.]+\]|[A-Za-z0-9.-]+)(:(?<port>[0-9]{1,5}))?/(?<path>\S+)$")]
    private static partial Regex SshUrl();
}
