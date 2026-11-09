using System.Reflection;
using System.Runtime.Loader;
using BorgStudio.Plugins;

namespace BorgStudio.Core.Plugins;

/// <summary>
/// Isolated load context per external plugin: its private dependencies come from its own folder,
/// while the plugin API is always BorgStudio's copy (otherwise the plugin's types would not match ours).
/// </summary>
internal sealed class PluginLoadContext : AssemblyLoadContext
{
    private static readonly string SharedAssemblyName = typeof(IBorgStudioPlugin).Assembly.GetName().Name!;

    private readonly string _directory;
    private readonly AssemblyDependencyResolver? _resolver;

    public PluginLoadContext(string assemblyPath) : base(Path.GetFileNameWithoutExtension(assemblyPath))
    {
        _directory = Path.GetDirectoryName(assemblyPath)!;
        try
        {
            _resolver = new AssemblyDependencyResolver(assemblyPath);
        }
        catch (InvalidOperationException)
        {
            // No host support for dependency resolution (e.g. some single-file hosts): fall back to the plugin folder.
        }
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (assemblyName.Name == SharedAssemblyName)
            return null;

        var path = _resolver?.ResolveAssemblyToPath(assemblyName) ?? ProbePluginFolder(assemblyName);
        return path is null ? null : LoadFromAssemblyPath(path);
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver?.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
    }

    private string? ProbePluginFolder(AssemblyName assemblyName)
    {
        var path = Path.Combine(_directory, assemblyName.Name + ".dll");
        return File.Exists(path) ? path : null;
    }
}
