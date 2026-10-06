using BorgStudio.Plugins;

namespace BorgStudio.Core.Plugins;

public enum PluginErrorKind
{
    /// <summary>The plugin folder has no assembly named like the folder. Detail: expected file name.</summary>
    AssemblyMissing,

    /// <summary>The assembly could not be loaded. Detail: exception message.</summary>
    LoadFailed,

    /// <summary>The assembly contains no usable <see cref="IBorgStudioPlugin"/> class.</summary>
    NoPluginClass,

    /// <summary>Creating the plugin or its <see cref="IBorgStudioPlugin.Register"/> threw. Detail: exception message.</summary>
    RegistrationFailed,

    /// <summary>A provider was skipped because its id is taken. Detail: provider id; <see cref="PluginError.OtherPlugin"/>: owner.</summary>
    DuplicateProvider,
}

/// <summary>Why a plugin (or part of it) is not available. The UI turns this into a message.</summary>
public sealed record PluginError(PluginErrorKind Kind, string? Detail = null)
{
    /// <summary>For <see cref="PluginErrorKind.DuplicateProvider"/>: the plugin that already has the id.</summary>
    public string? OtherPlugin { get; init; }
}

/// <summary>A built-in or external plugin and what it contributed.</summary>
public sealed record PluginInfo(string Name, string? Version, bool IsBuiltIn)
{
    /// <summary>Folder of an external plugin; <c>null</c> for built-in ones.</summary>
    public string? Directory { get; init; }

    public IReadOnlyList<IRepositoryProvider> RepositoryProviders { get; init; } = [];

    public IReadOnlyList<PluginError> Errors { get; init; } = [];
}
