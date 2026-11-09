using System.Globalization;
using BorgStudio.App.Resources;
using BorgStudio.Core.Plugins;

namespace BorgStudio.App.ViewModels;

/// <summary>The plugin overview: what is loaded, what it provides and what went wrong.</summary>
public sealed class PluginsViewModel(PluginCatalog catalog, string pluginsDirectory) : ViewModelBase
{
    public string PluginsDirectory { get; } = pluginsDirectory;

    public IReadOnlyList<PluginItemViewModel> Plugins { get; } =
        catalog.Plugins.Select(plugin => new PluginItemViewModel(plugin)).ToList();
}

public sealed class PluginItemViewModel(PluginInfo plugin)
{
    public string Name => plugin.Name;

    /// <summary>e.g. "Built-in · Version 0.2.0".</summary>
    public string Details => plugin.Version is null
        ? Kind
        : $"{Kind} · {Format(Strings.PluginVersion, plugin.Version)}";

    private string Kind => plugin.IsBuiltIn ? Strings.PluginBuiltIn : Strings.PluginExternal;

    public IReadOnlyList<string> Providers { get; } =
        plugin.RepositoryProviders.Select(provider => $"{provider.DisplayName} – {provider.Description}").ToList();

    public IReadOnlyList<string> Errors { get; } = plugin.Errors.Select(Describe).ToList();

    private static string Describe(PluginError error) => error.Kind switch
    {
        PluginErrorKind.AssemblyMissing => Format(Strings.PluginErrorAssemblyMissing, error.Detail),
        PluginErrorKind.LoadFailed => Format(Strings.PluginErrorLoadFailed, error.Detail),
        PluginErrorKind.NoPluginClass => Strings.PluginErrorNoPluginClass,
        PluginErrorKind.RegistrationFailed => Format(Strings.PluginErrorRegistrationFailed, error.Detail),
        PluginErrorKind.DuplicateProvider => Format(Strings.PluginErrorDuplicateProvider, error.Detail, error.OtherPlugin),
        _ => error.Kind.ToString(),
    };

    private static string Format(string format, params object?[] arguments) =>
        string.Format(CultureInfo.CurrentCulture, format, arguments);
}
