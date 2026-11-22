using BorgStudio.Core.Plugins;
using BorgStudio.Core.Repositories;
using BorgStudio.Plugins;
using BorgStudio.Providers.Usb;
using BorgStudio.Tests.TestSupport;

namespace BorgStudio.Tests.Providers;

/// <summary>The USB drive provider: finding a repository by its ID on any drive, and ejecting after automatic runs.</summary>
public sealed class UsbProviderTests : IDisposable
{
    private const string Id = "4f1c0a6e9d2b47c38a5e0b1d2c3f4a5b6c7d8e9f0a1b2c3d4e5f6a7b8c9d0e1f";

    private readonly TemporaryDirectory _directory = new();
    private readonly FakeDrives _drives = new();
    private readonly UsbRepositoryProvider _provider;

    public UsbProviderTests()
    {
        _provider = new UsbRepositoryProvider(_drives);
        // Two "drives": the old letter is gone, the drive is attached under another one.
        _drives.Attached.Add(new DriveRoot(Drive("F"), IsNetwork: false));
        _drives.Attached.Add(new DriveRoot(Drive("Z"), IsNetwork: true));
    }

    public void Dispose() => _directory.Dispose();

    private string Drive(string letter)
    {
        var root = _directory.Combine("drives", letter);
        Directory.CreateDirectory(root);
        return root;
    }

    private static string Repository(string folder, string id = Id)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "config"), $"[repository]\nversion = 1\nsegments_per_dir = 1000\nid = {id}\nappend_only = 0\n");
        return folder;
    }

    private string LastSeen => Path.Combine(_directory.Combine("drives", "E"), "Backups", "pc", "repo");

    private Task<RepositoryConnection> ConnectAsync(string? id = Id, bool unattended = false, bool eject = false) =>
        _provider.ConnectAsync(new RepositoryConnectRequest(new Dictionary<string, string>
        {
            [UsbRepositoryProvider.PathKey] = LastSeen,
            [UsbRepositoryProvider.EjectKey] = eject ? "true" : "false",
        }) { RepositoryId = id, Unattended = unattended }, CancellationToken.None);

    [Fact]
    public async Task Where_it_was_last_seen_it_is_used_as_it_is()
    {
        Repository(LastSeen);

        var connection = await ConnectAsync();

        Assert.Equal(LastSeen, connection.Location.Url);
        Assert.Null(connection.UpdatedValues);
    }

    [Fact]
    public async Task Under_another_drive_letter_the_same_folder_is_found_and_kept()
    {
        var moved = Repository(Path.Combine(Drive("F"), "Backups", "pc", "repo"));

        var connection = await ConnectAsync();

        Assert.Equal(moved, connection.Location.Url);
        Assert.Equal(moved, connection.UpdatedValues![UsbRepositoryProvider.PathKey]);
        Assert.Equal("false", connection.UpdatedValues[UsbRepositoryProvider.EjectKey]);
    }

    [Fact]
    public async Task Elsewhere_it_is_searched_two_levels_deep_on_every_drive_by_its_id()
    {
        // Another repository where it was: not this one.
        Repository(LastSeen, id: "0000");
        Repository(Path.Combine(Drive("F"), "Old", "repo"), id: "1111");
        var found = Repository(Path.Combine(Drive("Z"), "borg", "laptop"));
        // Deeper than two levels is not searched.
        Repository(Path.Combine(Drive("F"), "a", "b", "c"));

        var connection = await ConnectAsync();

        Assert.Equal(found, connection.Location.Url);
    }

    [Fact]
    public async Task Not_attached_it_says_where_it_was_last_seen()
    {
        Repository(Path.Combine(Drive("F"), "a", "b", "c")); // too deep

        var missing = await Assert.ThrowsAsync<RepositoryUnavailableException>(() => ConnectAsync());

        Assert.Contains(LastSeen, missing.Message);
    }

    [Fact]
    public async Task Without_a_known_id_only_the_last_place_counts()
    {
        Repository(Path.Combine(Drive("F"), "Backups", "pc", "repo"));
        await Assert.ThrowsAsync<RepositoryUnavailableException>(() => ConnectAsync(id: null));

        Repository(LastSeen, id: "anything");
        Assert.Equal(LastSeen, (await ConnectAsync(id: null)).Location.Url);
    }

    [Fact]
    public async Task Only_automatic_runs_eject_and_only_if_switched_on()
    {
        Repository(LastSeen);

        Assert.Null(await (await ConnectAsync(unattended: false, eject: true)).DisconnectAsync());
        Assert.Null(await (await ConnectAsync(unattended: true, eject: false)).DisconnectAsync());
        Assert.Empty(_drives.Ejected);

        _drives.EjectProblem = "E: is still in use";
        Assert.Equal("E: is still in use", await (await ConnectAsync(unattended: true, eject: true)).DisconnectAsync());
        Assert.Equal([LastSeen], _drives.Ejected);
    }

    [Fact]
    public void The_fields_validate_like_a_local_folder()
    {
        Assert.Equal([ProviderFieldKind.FolderPath, ProviderFieldKind.Toggle], _provider.Fields.Select(field => field.Kind));
        Assert.NotEmpty(_provider.Validate(new Dictionary<string, string> { [UsbRepositoryProvider.PathKey] = "" }));
        Assert.NotEmpty(_provider.Validate(new Dictionary<string, string> { [UsbRepositoryProvider.PathKey] = "relative" }));
        Assert.Empty(_provider.Validate(new Dictionary<string, string> { [UsbRepositoryProvider.PathKey] = LastSeen }));
        Assert.True(UsbRepositoryProvider.IsEjecting(new Dictionary<string, string> { [UsbRepositoryProvider.EjectKey] = "true" }));
        Assert.False(UsbRepositoryProvider.IsEjecting(new Dictionary<string, string>()));
    }

    // --- Connections shared by everything in the app ---

    [Fact]
    public async Task Uses_of_a_repository_share_one_connection_that_ends_with_the_last()
    {
        var connector = new CountingProvider();
        var connections = new RepositoryConnections(PluginCatalog.Load([new SinglePlugin(connector)], "no-plugins-here"));
        var repository = new RepositoryConfig
        {
            Id = Guid.NewGuid(), Name = "Disk", ProviderId = connector.Id, ProviderValues = new Dictionary<string, string>(),
        };

        var first = await connections.ConnectAsync(repository, unattended: true);
        var second = await connections.ConnectAsync(repository);
        Assert.Equal(1, connector.Connects);
        Assert.True(connections.IsConnected(repository.Id));

        Assert.Null(await first!.ReleaseAsync());
        Assert.Null(await first.ReleaseAsync()); // once only
        Assert.Equal(0, connector.Disconnects);
        Assert.Equal("disconnected", await second!.ReleaseAsync());
        Assert.Equal(1, connector.Disconnects);
        Assert.False(connections.IsConnected(repository.Id));

        // A plugin's own failure is told like an unreachable repository.
        connector.Failure = new IOException("device not ready");
        var failed = await Assert.ThrowsAsync<RepositoryUnavailableException>(() => connections.ConnectAsync(repository));
        Assert.Equal("device not ready", failed.Message);
    }

    [Fact]
    public async Task A_new_place_is_saved_with_the_repository()
    {
        var store = new RepositoryStore(_directory.Combine("repositories.json"));
        var connections = new RepositoryConnections(PluginCatalog.Load([new UsbProviderPlugin(_drives)], "no-plugins-here"), store);
        var repository = new RepositoryConfig
        {
            Id = Guid.NewGuid(), Name = "USB", ProviderId = UsbRepositoryProvider.ProviderId, BorgRepositoryId = Id,
            ProviderValues = new Dictionary<string, string> { [UsbRepositoryProvider.PathKey] = LastSeen },
        };
        store.Save([repository]);
        var moved = Repository(Path.Combine(Drive("F"), "Backups", "pc", "repo"));

        await using var lease = await connections.ConnectAsync(repository);

        Assert.Equal(moved, lease!.Location.Url);
        Assert.Equal(moved, lease.Repository.ProviderValues[UsbRepositoryProvider.PathKey]);
        Assert.Equal(moved, store.Load().Repositories.Single().ProviderValues[UsbRepositoryProvider.PathKey]);
    }

    private sealed class SinglePlugin(IRepositoryProvider provider) : IBorgStudioPlugin
    {
        public void Register(IPluginRegistrar registrar) => registrar.AddRepositoryProvider(provider);
    }

    private sealed class CountingProvider : IRepositoryProvider, IRepositoryConnector
    {
        public int Connects { get; private set; }

        public int Disconnects { get; private set; }

        public Exception? Failure { get; set; }

        public string Id => "counting";

        public string DisplayName => "Counting";

        public string Description => "";

        public IReadOnlyList<ProviderField> Fields => [];

        public IReadOnlyList<string> Validate(IReadOnlyDictionary<string, string> values) => [];

        public RepositoryLocation GetLocation(IReadOnlyDictionary<string, string> values) => new("/last");

        public async Task<RepositoryConnection> ConnectAsync(RepositoryConnectRequest request, CancellationToken cancellationToken)
        {
            await Task.Yield();
            if (Failure is not null)
                throw Failure;
            Connects++;
            return new RepositoryConnection(new RepositoryLocation("/now"), () =>
            {
                Disconnects++;
                return Task.FromResult<string?>("disconnected");
            });
        }
    }
}
