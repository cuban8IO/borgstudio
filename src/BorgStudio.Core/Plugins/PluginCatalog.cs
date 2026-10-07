using System.Reflection;
using BorgStudio.Plugins;

namespace BorgStudio.Core.Plugins;

/// <summary>All plugins (built-in first, then external ones from the plugin folder) and their contributions.</summary>
public sealed class PluginCatalog
{
    private PluginCatalog(IReadOnlyList<PluginInfo> plugins)
    {
        Plugins = plugins;
        RepositoryProviders = plugins.SelectMany(plugin => plugin.RepositoryProviders).ToList();
    }

    public static PluginCatalog Empty { get; } = new([]);

    public IReadOnlyList<PluginInfo> Plugins { get; }

    public IReadOnlyList<IRepositoryProvider> RepositoryProviders { get; }

    public IRepositoryProvider? FindRepositoryProvider(string id) =>
        RepositoryProviders.FirstOrDefault(provider => provider.Id == id);

    /// <summary>
    /// Registers the built-in plugins, then loads every sub folder of <paramref name="pluginsDirectory"/>
    /// (<c>&lt;Name&gt;/&lt;Name&gt;.dll</c>). A broken plugin never stops the others; its problems end up in
    /// <see cref="PluginInfo.Errors"/>.
    /// </summary>
    public static PluginCatalog Load(IEnumerable<IBorgStudioPlugin> builtInPlugins, string pluginsDirectory)
    {
        var plugins = new List<PluginInfo>();
        var providerOwners = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var plugin in builtInPlugins)
        {
            var assembly = plugin.GetType().Assembly;
            plugins.Add(Register([plugin], Describe(assembly, isBuiltIn: true), providerOwners));
        }

        if (Directory.Exists(pluginsDirectory))
        {
            foreach (var directory in Directory.GetDirectories(pluginsDirectory).Order(StringComparer.OrdinalIgnoreCase))
                plugins.Add(LoadExternal(directory, providerOwners));
        }

        return new PluginCatalog(plugins);
    }

    private static PluginInfo LoadExternal(string directory, Dictionary<string, string> providerOwners)
    {
        var name = Path.GetFileName(directory);
        var info = new PluginInfo(name, null, IsBuiltIn: false) { Directory = directory };

        var assemblyPath = Path.Combine(directory, name + ".dll");
        if (!File.Exists(assemblyPath))
            return info with { Errors = [new PluginError(PluginErrorKind.AssemblyMissing, name + ".dll")] };

        Assembly assembly;
        Type[] pluginTypes;
        try
        {
            assembly = new PluginLoadContext(assemblyPath).LoadFromAssemblyPath(Path.GetFullPath(assemblyPath));
            pluginTypes = assembly.GetExportedTypes()
                .Where(type => type is { IsClass: true, IsAbstract: false }
                    && typeof(IBorgStudioPlugin).IsAssignableFrom(type)
                    && type.GetConstructor(Type.EmptyTypes) is not null)
                .ToArray();
        }
        catch (Exception exception)
        {
            return info with { Errors = [new PluginError(PluginErrorKind.LoadFailed, exception.Message)] };
        }

        info = Describe(assembly, isBuiltIn: false) with { Directory = directory };
        if (pluginTypes.Length == 0)
            return info with { Errors = [new PluginError(PluginErrorKind.NoPluginClass)] };

        try
        {
            var instances = pluginTypes.Select(type => (IBorgStudioPlugin)Activator.CreateInstance(type)!).ToList();
            return Register(instances, info, providerOwners);
        }
        catch (Exception exception)
        {
            return info with { Errors = [new PluginError(PluginErrorKind.RegistrationFailed, InnermostMessage(exception))] };
        }
    }

    private static PluginInfo Register(
        IReadOnlyList<IBorgStudioPlugin> instances, PluginInfo info, Dictionary<string, string> providerOwners)
    {
        var registrar = new Registrar();
        try
        {
            foreach (var instance in instances)
                instance.Register(registrar);
        }
        catch (Exception exception)
        {
            // Whatever was registered before the failure is discarded: a half-registered plugin is worse than none.
            return info with { Errors = [new PluginError(PluginErrorKind.RegistrationFailed, InnermostMessage(exception))] };
        }

        var providers = new List<IRepositoryProvider>();
        var errors = new List<PluginError>();
        foreach (var provider in registrar.RepositoryProviders)
        {
            if (providerOwners.TryAdd(provider.Id, info.Name))
                providers.Add(provider);
            else
                errors.Add(new PluginError(PluginErrorKind.DuplicateProvider, provider.Id) { OtherPlugin = providerOwners[provider.Id] });
        }

        return info with { RepositoryProviders = providers, Errors = errors };
    }

    private static PluginInfo Describe(Assembly assembly, bool isBuiltIn)
    {
        var name = assembly.GetCustomAttribute<AssemblyTitleAttribute>()?.Title ?? assembly.GetName().Name!;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString();

        // "1.2.0+<commit hash>" -> "1.2.0"
        var metadataStart = version?.IndexOf('+') ?? -1;
        return new PluginInfo(name, metadataStart > 0 ? version![..metadataStart] : version, isBuiltIn);
    }

    private static string InnermostMessage(Exception exception) =>
        (exception is TargetInvocationException { InnerException: { } inner } ? inner : exception).Message;

    private sealed class Registrar : IPluginRegistrar
    {
        public List<IRepositoryProvider> RepositoryProviders { get; } = [];

        public void AddRepositoryProvider(IRepositoryProvider provider)
        {
            ArgumentNullException.ThrowIfNull(provider);
            RepositoryProviders.Add(provider);
        }
    }
}
