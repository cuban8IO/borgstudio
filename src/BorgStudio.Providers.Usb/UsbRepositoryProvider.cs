using System.Globalization;
using BorgStudio.Plugins;
using BorgStudio.Providers.Usb.Resources;

namespace BorgStudio.Providers.Usb;

/// <summary>Built into BorgStudio, but registered through the same plugin API as external providers.</summary>
/// <param name="drives">The drives to search and eject (tests replace them); default: the real ones.</param>
public sealed class UsbProviderPlugin(IDriveSystem? drives = null) : IBorgStudioPlugin
{
    public UsbProviderPlugin() : this(null)
    {
    }

    public void Register(IPluginRegistrar registrar) => registrar.AddRepositoryProvider(new UsbRepositoryProvider(drives));
}

/// <summary>
/// A repository on a drive that is not always attached. Before every use it is looked for by its borg repository ID –
/// where it was last seen, then on every drive – so a new drive letter or mount point does not matter. After automatic
/// runs the drive can be ejected.
/// </summary>
public sealed class UsbRepositoryProvider(IDriveSystem? drives = null) : IRepositoryProvider, IRepositoryConnector
{
    public const string ProviderId = "usb";
    public const string PathKey = "path";
    public const string EjectKey = "eject";

    public IDriveSystem Drives { get; } = drives ?? new SystemDrives();

    public string Id => ProviderId;

    public string DisplayName => Strings.DisplayName;

    public string Description => Strings.Description;

    public IReadOnlyList<ProviderField> Fields =>
    [
        new(PathKey, Strings.PathLabel, ProviderFieldKind.FolderPath) { Hint = Strings.PathHint },
        new(EjectKey, Strings.EjectLabel, ProviderFieldKind.Toggle) { Hint = Strings.EjectHint, DefaultValue = "false" },
    ];

    public IReadOnlyList<string> Validate(IReadOnlyDictionary<string, string> values)
    {
        var path = values.GetValueOrDefault(PathKey)?.Trim();
        if (string.IsNullOrEmpty(path))
            return [Strings.PathRequired];
        if (!Path.IsPathFullyQualified(path))
            return [Strings.PathNotAbsolute];
        return [];
    }

    /// <summary>Where the repository was last seen (the overview shows it; borg gets the place found when connecting).</summary>
    public RepositoryLocation GetLocation(IReadOnlyDictionary<string, string> values) =>
        new(Path.GetFullPath(values[PathKey].Trim()));

    public async Task<RepositoryConnection> ConnectAsync(RepositoryConnectRequest request, CancellationToken cancellationToken)
    {
        var lastPath = GetLocation(request.Values).Url;
        var found = await RepositoryFinder.FindAsync(Drives, lastPath, request.RepositoryId, cancellationToken)
            ?? throw new RepositoryUnavailableException(string.Format(CultureInfo.CurrentCulture,
                string.IsNullOrEmpty(request.RepositoryId) ? Strings.NotFoundUnknownId : Strings.NotFound, lastPath));

        var eject = request.Unattended && IsEjecting(request.Values);
        var connection = new RepositoryConnection(new RepositoryLocation(found), eject ? () => Drives.EjectAsync(found) : null);
        if (string.Equals(found, lastPath, StringComparison.Ordinal))
            return connection;
        return new RepositoryConnection(connection.Location, eject ? () => Drives.EjectAsync(found) : null)
        {
            UpdatedValues = new Dictionary<string, string>(request.Values) { [PathKey] = found },
        };
    }

    /// <summary>Whether the drive is ejected after automatic runs.</summary>
    public static bool IsEjecting(IReadOnlyDictionary<string, string> values) =>
        bool.TryParse(values.GetValueOrDefault(EjectKey), out var eject) && eject;
}
