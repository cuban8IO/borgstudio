using System.Reflection;

namespace BorgStudio.App;

/// <summary>The app's version as set by the release workflow (<c>0.0.0-dev</c> for local builds).</summary>
public static class AppVersion
{
    public static string Current { get; } = Display(Informational);

    private static string Informational =>
        typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(AppVersion).Assembly.GetName().Version?.ToString(3) ?? "";

    /// <summary>The window title: "BorgStudio 0.5.0", or for a local build "BorgStudio – Entwicklungsversion (f9caf18)".</summary>
    public static string Title { get; } = TitleFor(Informational);

    public static string TitleFor(string informationalVersion)
    {
        var version = Display(informationalVersion);
        if (!version.StartsWith("0.0.0-dev", StringComparison.Ordinal))
            return $"BorgStudio {version}";
        // The SDK appends the commit the build was made from ("0.0.0-dev+f9caf18…").
        var plus = informationalVersion.IndexOf('+');
        var commit = plus < 0 ? "" : informationalVersion[(plus + 1)..];
        return commit.Length >= 7
            ? $"BorgStudio – {Resources.Strings.DevelopmentVersion} ({commit[..7]})"
            : $"BorgStudio – {Resources.Strings.DevelopmentVersion}";
    }

    /// <summary>Drops the build metadata the SDK appends ("0.2.0+3f2a…" -> "0.2.0").</summary>
    public static string Display(string informationalVersion)
    {
        var plus = informationalVersion.IndexOf('+');
        return plus < 0 ? informationalVersion : informationalVersion[..plus];
    }
}
