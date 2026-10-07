namespace BorgStudio.Plugins;

/// <summary>
/// Entry point of a plugin. BorgStudio creates every public, non-abstract class implementing this
/// interface (it needs a public parameterless constructor) and calls <see cref="Register"/> once at startup.
/// </summary>
public interface IBorgStudioPlugin
{
    /// <summary>Registers everything the plugin contributes, e.g. repository providers.</summary>
    void Register(IPluginRegistrar registrar);
}

/// <summary>Collects the contributions of a plugin.</summary>
public interface IPluginRegistrar
{
    /// <summary>Adds a repository provider. Its <see cref="IRepositoryProvider.Id"/> must be unique.</summary>
    void AddRepositoryProvider(IRepositoryProvider provider);
}
