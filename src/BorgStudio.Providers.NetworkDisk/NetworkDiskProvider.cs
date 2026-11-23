using System.Globalization;
using BorgStudio.Plugins;
using BorgStudio.Providers.NetworkDisk.Resources;
using BorgStudio.Providers.Usb;

namespace BorgStudio.Providers.NetworkDisk;

/// <summary>Built into BorgStudio, but registered through the same plugin API as external providers.</summary>
public sealed class NetworkDiskProviderPlugin(INetworkDiskSystem? system = null, IDriveSystem? drives = null) : IBorgStudioPlugin
{
    public NetworkDiskProviderPlugin() : this(null, null)
    {
    }

    public void Register(IPluginRegistrar registrar) => registrar.AddRepositoryProvider(new NetworkDiskProvider(system, drives));
}

/// <summary>
/// A repository on a network disk (iSCSI, or NVMe over TCP on Linux). Before each use the target is logged in to – unless
/// its volume is there already – and the repository is found on it by its ID (like a USB drive); afterwards it is logged
/// out again, if BorgStudio logged in. Logging in needs administrator rights, so the editor sets that up once
/// (<see cref="IRepositoryPreparation"/>): Windows gets tasks with highest rights for exactly this target, Linux a sudoers
/// rule for exactly its login and logout.
/// </summary>
public sealed class NetworkDiskProvider(INetworkDiskSystem? system = null, IDriveSystem? drives = null)
    : IRepositoryProvider, IRepositoryConnector, IRepositoryPreparation
{
    public const string ProtocolKey = "protocol";
    public const string PortalKey = "portal";
    public const string TargetKey = "target";
    public const string ChapUserKey = "chapUser";
    public const string ChapSecretKey = "chapSecret";
    public const string FolderKey = "folder";

    /// <summary>Kept by the provider: what was set up, the id it was set up under, where the repository was last.</summary>
    public const string SetupKey = "setup";
    public const string DiskIdKey = "diskId";
    public const string LastPathKey = "lastPath";

    /// <summary>How long the disk's volume may take to appear after logging in.</summary>
    public static TimeSpan AppearTimeout { get; set; } = TimeSpan.FromSeconds(60);

    public static TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);

    public INetworkDiskSystem System { get; } = system
        ?? (OperatingSystem.IsWindows() ? new WindowsIscsi() : OperatingSystem.IsMacOS() ? new MacNetworkDisks() : new LinuxNetworkDisks());

    public IDriveSystem Drives { get; } = drives ?? new SystemDrives();

    public string Id => "network-disk";

    public string DisplayName => Strings.DisplayName;

    public string Description => Strings.Description;

    /// <summary>Not yet tried against a real iSCSI or NVMe/TCP target.</summary>
    public bool IsUntested => true;

    public IReadOnlyList<ProviderField> Fields =>
    [
        new(ProtocolKey, Strings.ProtocolLabel, ProviderFieldKind.Choice)
        {
            DefaultValue = "iscsi",
            Options = [new("iscsi", Strings.ProtocolIscsi), new("nvme-tcp", Strings.ProtocolNvme)],
        },
        new(PortalKey, Strings.PortalLabel) { Hint = Strings.PortalHint },
        new(TargetKey, Strings.TargetLabel) { Hint = Strings.TargetHint },
        new(ChapUserKey, Strings.ChapUserLabel) { Required = false, Hint = Strings.ChapUserHint },
        new(ChapSecretKey, Strings.ChapSecretLabel, ProviderFieldKind.Secret) { Required = false },
        new(FolderKey, Strings.FolderLabel) { Hint = Strings.FolderHint },
    ];

    public IReadOnlyList<string> Validate(IReadOnlyDictionary<string, string> values)
    {
        var errors = new List<string>();
        var protocol = Protocol(values);
        if (NetworkDiskTarget.Portal(Value(values, PortalKey), protocol) is null)
            errors.Add(Strings.PortalInvalid);
        if (!NetworkDiskTarget.IsValidName(Value(values, TargetKey)))
            errors.Add(Strings.TargetInvalid);
        if (Value(values, ChapUserKey) is { Length: > 0 } user && (!NetworkDiskTarget.IsValidChapUser(user) || protocol != NetworkDiskProtocol.Iscsi))
            errors.Add(protocol == NetworkDiskProtocol.Iscsi ? Strings.ChapUserInvalid : Strings.ChapOnlyIscsi);
        var folder = Folder(values);
        if (folder.Length == 0 || Path.IsPathRooted(folder) || folder.Split('/', '\\').Any(part => part is ".." or ""))
            errors.Add(Strings.FolderInvalid);
        if (Unsupported(protocol) is { } unsupported)
            errors.Add(unsupported);
        return errors;
    }

    /// <summary>Where the repository was last (the overview shows it); borg gets the place found when connecting.</summary>
    public RepositoryLocation GetLocation(IReadOnlyDictionary<string, string> values) =>
        new(Value(values, LastPathKey) is { Length: > 0 } last ? last : Folder(values));

    // --- Setting up once ---

    public async Task<IReadOnlyDictionary<string, string>> PrepareAsync(RepositoryPreparationRequest request, CancellationToken cancellationToken)
    {
        var target = Target(request.Values);
        if (Unsupported(target.Protocol) is { } unsupported)
            throw new RepositoryUnavailableException(unsupported);
        var secret = request.Secrets.GetValueOrDefault(ChapSecretKey);
        var id = Value(request.Values, DiskIdKey) is { Length: > 0 } known ? known : request.RepositoryId.ToString("N");
        if (target.IsSetUp(Value(request.Values, SetupKey), secret) && Value(request.Values, DiskIdKey).Length > 0)
            return new Dictionary<string, string>();

        await System.SetUpAsync(target, id, secret, cancellationToken);
        return new Dictionary<string, string> { [SetupKey] = target.SetupMarker(secret), [DiskIdKey] = id };
    }

    // --- Connecting before each use ---

    public async Task<RepositoryConnection> ConnectAsync(RepositoryConnectRequest request, CancellationToken cancellationToken)
    {
        NetworkDiskTarget target;
        try
        {
            target = Target(request.Values);
        }
        catch (ArgumentException)
        {
            throw new RepositoryUnavailableException(Strings.PortalInvalid);
        }
        if (Unsupported(target.Protocol) is { } unsupported)
            throw new RepositoryUnavailableException(unsupported);
        if (Value(request.Values, DiskIdKey) is not { Length: > 0 } id)
            throw new RepositoryUnavailableException(Strings.NotSetUp);

        // Attached already (e.g. connected for good by the user): used as it is, and left attached.
        var before = Roots();
        if (await FindAsync(request, before, created: [], cancellationToken) is { } there)
            return Connected(request, there, disconnect: null);

        await System.ConnectAsync(target, id, cancellationToken);
        var started = DateTime.UtcNow;
        while (true)
        {
            var now = Roots();
            var appeared = now.Where(root => !before.Contains(root, StringComparer.OrdinalIgnoreCase)).ToList();
            if (await FindAsync(request, now, appeared, cancellationToken) is { } found)
                return Connected(request, found, () => System.DisconnectAsync(target, id, CancellationToken.None));
            if (DateTime.UtcNow - started > AppearTimeout)
                break;
            await Task.Delay(PollInterval, cancellationToken);
        }
        await System.DisconnectAsync(target, id, CancellationToken.None);
        throw new RepositoryUnavailableException(string.Format(CultureInfo.CurrentCulture, Strings.RepositoryNotFound, Folder(request.Values)));
    }

    private static RepositoryConnection Connected(RepositoryConnectRequest request, string path, Func<Task<string?>>? disconnect) =>
        new(new RepositoryLocation(path), disconnect)
        {
            UpdatedValues = Value(request.Values, LastPathKey) == path ? null : new Dictionary<string, string>(request.Values) { [LastPathKey] = path },
        };

    /// <summary>
    /// The repository's folder: with a known ID searched like on a USB drive; without (a repository being created or
    /// connected in the editor) the folder below a volume that just appeared – or anywhere, if it already holds a repository.
    /// </summary>
    private async Task<string?> FindAsync(RepositoryConnectRequest request, IReadOnlyList<string> roots, IReadOnlyList<string> created,
        CancellationToken cancellationToken)
    {
        var folder = Folder(request.Values);
        var last = Value(request.Values, LastPathKey) is { Length: > 0 } known ? known : Under(roots.FirstOrDefault() ?? "", folder);
        if (request.RepositoryId is { Length: > 0 } repositoryId)
            return await RepositoryFinder.FindAsync(Drives, last, repositoryId, cancellationToken);

        foreach (var root in roots)
            if (RepositoryFinder.IsRepository(Under(root, folder), null))
                return Under(root, folder);
        // A new repository: on the disk that just appeared, if its parent folder is there.
        foreach (var root in created)
        {
            var candidate = Under(root, folder);
            if (Path.GetDirectoryName(candidate) is { } parent && Directory.Exists(parent))
                return candidate;
        }
        return null;
    }

    private static string Under(string root, string folder) => Path.Combine(root, folder.Replace('/', Path.DirectorySeparatorChar));

    private List<string> Roots() => Drives.Roots().Where(root => !root.IsNetwork).Select(root => root.Path).ToList();

    // --- Values ---

    private static NetworkDiskProtocol Protocol(IReadOnlyDictionary<string, string> values) =>
        Value(values, ProtocolKey) == "nvme-tcp" ? NetworkDiskProtocol.NvmeTcp : NetworkDiskProtocol.Iscsi;

    /// <exception cref="ArgumentException">The portal is not valid.</exception>
    public static NetworkDiskTarget Target(IReadOnlyDictionary<string, string> values)
    {
        var protocol = Protocol(values);
        var (host, port) = NetworkDiskTarget.Portal(Value(values, PortalKey), protocol)
            ?? throw new ArgumentException("The portal is not valid.", nameof(values));
        return new NetworkDiskTarget(protocol, host, port, Value(values, TargetKey),
            Value(values, ChapUserKey) is { Length: > 0 } user ? user : null);
    }

    private static string Folder(IReadOnlyDictionary<string, string> values) =>
        Value(values, FolderKey).Replace('\\', '/').Trim('/');

    /// <summary>What this computer cannot do: macOS has no built-in initiator, Windows clients no NVMe/TCP.</summary>
    private string? Unsupported(NetworkDiskProtocol protocol) => System.Unsupported(protocol);

    private static string Value(IReadOnlyDictionary<string, string> values, string key) =>
        values.GetValueOrDefault(key)?.Trim() ?? "";
}
