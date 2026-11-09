using System.Runtime.Loader;
using BorgStudio.Core.Plugins;
using BorgStudio.Plugins;

namespace BorgStudio.Tests.Plugins;

public sealed class PluginCatalogTests : IDisposable
{
    private const string SampleName = "BorgStudio.SamplePlugin";

    private static readonly string SampleBuildOutput = Path.Combine(AppContext.BaseDirectory, "plugins", SampleName);

    private readonly string _pluginsDirectory =
        Path.Combine(Path.GetTempPath(), "borgstudio-tests", Guid.NewGuid().ToString("N"));

    public PluginCatalogTests() => Directory.CreateDirectory(_pluginsDirectory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_pluginsDirectory, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Loaded plugin assemblies stay locked on Windows; the temp folder gets cleaned up eventually.
        }
    }

    private sealed class FakeProvider(string id) : IRepositoryProvider
    {
        public string Id => id;
        public string DisplayName => id;
        public string Description => "";
        public IReadOnlyList<ProviderField> Fields => [];
        public IReadOnlyList<string> Validate(IReadOnlyDictionary<string, string> values) => [];
        public RepositoryLocation GetLocation(IReadOnlyDictionary<string, string> values) => new("/" + id);
    }

    private sealed class FakePlugin(params string[] providerIds) : IBorgStudioPlugin
    {
        public void Register(IPluginRegistrar registrar)
        {
            foreach (var id in providerIds)
                registrar.AddRepositoryProvider(new FakeProvider(id));
        }
    }

    private sealed class FailingPlugin : IBorgStudioPlugin
    {
        public void Register(IPluginRegistrar registrar)
        {
            registrar.AddRepositoryProvider(new FakeProvider("half-registered"));
            throw new InvalidOperationException("boom");
        }
    }

    /// <summary>Installs the sample plugin like a user would, optionally under another name.</summary>
    private string InstallSample(string name = SampleName)
    {
        var target = Directory.CreateDirectory(Path.Combine(_pluginsDirectory, name)).FullName;
        foreach (var file in Directory.GetFiles(SampleBuildOutput))
        {
            var fileName = Path.GetFileName(file).Replace(SampleName, name, StringComparison.Ordinal);
            File.Copy(file, Path.Combine(target, fileName));
        }
        return target;
    }

    [Fact]
    public void Loads_an_external_plugin_in_its_own_load_context()
    {
        var directory = InstallSample();

        var catalog = PluginCatalog.Load([], _pluginsDirectory);

        var plugin = Assert.Single(catalog.Plugins);
        Assert.Equal(SampleName, plugin.Name);
        Assert.False(plugin.IsBuiltIn);
        Assert.Equal(directory, plugin.Directory);
        Assert.Empty(plugin.Errors);

        var provider = Assert.Single(catalog.RepositoryProviders);
        Assert.Equal("sample", provider.Id);
        Assert.NotSame(AssemblyLoadContext.Default, AssemblyLoadContext.GetLoadContext(provider.GetType().Assembly));
    }

    [Fact]
    public void External_providers_work_across_the_load_context_boundary()
    {
        InstallSample();
        var provider = PluginCatalog.Load([], _pluginsDirectory).FindRepositoryProvider("sample")!;
        var values = new Dictionary<string, string> { ["user"] = "jdoe", ["folder"] = "borg" };

        Assert.Empty(provider.Validate(values));
        var location = provider.GetLocation(values);
        Assert.Equal("ssh://jdoe@backup.example.com/./borg", location.Url);
        Assert.Equal(["--remote-path=borg-1.4"], location.BorgArguments);
    }

    [Fact]
    public void The_plugin_API_is_not_shipped_with_the_sample()
    {
        Assert.True(File.Exists(Path.Combine(SampleBuildOutput, SampleName + ".dll")));
        Assert.False(File.Exists(Path.Combine(SampleBuildOutput, "BorgStudio.Plugins.Abstractions.dll")));
    }

    [Fact]
    public void Built_in_plugins_come_first_and_keep_their_provider_ids()
    {
        InstallSample();

        var catalog = PluginCatalog.Load([new FakePlugin("local", "sample")], _pluginsDirectory);

        Assert.Equal(["local", "sample"], catalog.RepositoryProviders.Select(provider => provider.Id));
        Assert.True(catalog.Plugins[0].IsBuiltIn);
        var external = catalog.Plugins[1];
        Assert.Empty(external.RepositoryProviders);
        var error = Assert.Single(external.Errors);
        Assert.Equal(PluginErrorKind.DuplicateProvider, error.Kind);
        Assert.Equal("sample", error.Detail);
        Assert.Equal(catalog.Plugins[0].Name, error.OtherPlugin);
    }

    [Fact]
    public void Same_plugin_installed_twice_reports_the_duplicate_provider()
    {
        InstallSample("A.Copy");
        InstallSample();

        var catalog = PluginCatalog.Load([], _pluginsDirectory);

        Assert.Single(catalog.RepositoryProviders);
        Assert.Equal(PluginErrorKind.DuplicateProvider, Assert.Single(catalog.Plugins[1].Errors).Kind);
    }

    [Fact]
    public void Folder_without_matching_assembly()
    {
        Directory.CreateDirectory(Path.Combine(_pluginsDirectory, "Empty.Plugin"));

        var plugin = Assert.Single(PluginCatalog.Load([], _pluginsDirectory).Plugins);

        Assert.Equal("Empty.Plugin", plugin.Name);
        Assert.Equal(new PluginError(PluginErrorKind.AssemblyMissing, "Empty.Plugin.dll"), Assert.Single(plugin.Errors));
    }

    [Fact]
    public void Corrupt_assembly()
    {
        var directory = Directory.CreateDirectory(Path.Combine(_pluginsDirectory, "Broken")).FullName;
        File.WriteAllText(Path.Combine(directory, "Broken.dll"), "this is not an assembly");

        var plugin = Assert.Single(PluginCatalog.Load([], _pluginsDirectory).Plugins);

        Assert.Equal(PluginErrorKind.LoadFailed, Assert.Single(plugin.Errors).Kind);
    }

    [Fact]
    public void Assembly_without_plugin_class()
    {
        // Any assembly without an IBorgStudioPlugin class will do, e.g. the plugin API itself.
        var name = "BorgStudio.Plugins.Abstractions";
        var directory = Directory.CreateDirectory(Path.Combine(_pluginsDirectory, name)).FullName;
        File.Copy(typeof(IBorgStudioPlugin).Assembly.Location, Path.Combine(directory, name + ".dll"));

        var plugin = Assert.Single(PluginCatalog.Load([], _pluginsDirectory).Plugins);

        Assert.Equal(PluginErrorKind.NoPluginClass, Assert.Single(plugin.Errors).Kind);
    }

    [Fact]
    public void A_failing_plugin_does_not_stop_the_others()
    {
        InstallSample();

        var catalog = PluginCatalog.Load([new FailingPlugin(), new FakePlugin("local")], _pluginsDirectory);

        var failing = catalog.Plugins[0];
        Assert.Empty(failing.RepositoryProviders);
        Assert.Equal(new PluginError(PluginErrorKind.RegistrationFailed, "boom"), Assert.Single(failing.Errors));
        Assert.Equal(["local", "sample"], catalog.RepositoryProviders.Select(provider => provider.Id));
    }

    [Fact]
    public void Missing_plugins_directory_means_built_in_plugins_only()
    {
        var catalog = PluginCatalog.Load([new FakePlugin("local")], Path.Combine(_pluginsDirectory, "does-not-exist"));

        Assert.Equal("local", Assert.Single(catalog.RepositoryProviders).Id);
        Assert.Null(catalog.FindRepositoryProvider("sample"));
    }
}
