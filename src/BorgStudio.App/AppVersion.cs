using System.Reflection;

namespace BorgStudio.App;

/// <summary>The app's version as set by the release workflow (<c>0.0.0-dev</c> for local builds).</summary>
public static class AppVersion
{
    public static string Current { get; } = Display(
        typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(AppVersion).Assembly.GetName().Version?.ToString(3) ?? "");

    /// <summary>Drops the build metadata the SDK appends ("0.2.0+3f2a…" -> "0.2.0").</summary>
    public static string Display(string informationalVersion)
    {
        var plus = informationalVersion.IndexOf('+');
        return plus < 0 ? informationalVersion : informationalVersion[..plus];
    }
}
