using BorgStudio.Plugins;
using BorgStudio.Providers.Local.Resources;

namespace BorgStudio.Providers.Local;

/// <summary>Built into BorgStudio, but registered through the same plugin API as external providers.</summary>
public sealed class LocalProviderPlugin : IBorgStudioPlugin
{
    public void Register(IPluginRegistrar registrar) => registrar.AddRepositoryProvider(new LocalRepositoryProvider());
}

/// <summary>Repository in a folder on this computer or on an attached drive.</summary>
public sealed class LocalRepositoryProvider : IRepositoryProvider
{
    public const string PathKey = "path";

    public string Id => "local";

    public string DisplayName => Strings.DisplayName;

    public string Description => Strings.Description;

    public IReadOnlyList<ProviderField> Fields =>
        [new(PathKey, Strings.PathLabel, ProviderFieldKind.FolderPath) { Hint = Strings.PathHint }];

    public IReadOnlyList<string> Validate(IReadOnlyDictionary<string, string> values)
    {
        var path = values.GetValueOrDefault(PathKey)?.Trim();
        if (string.IsNullOrEmpty(path))
            return [Strings.PathRequired];
        if (!Path.IsPathFullyQualified(path))
            return [Strings.PathNotAbsolute];
        return [];
    }

    public RepositoryLocation GetLocation(IReadOnlyDictionary<string, string> values) =>
        new(Path.GetFullPath(values[PathKey].Trim()));
}
