using System.Globalization;

namespace BorgStudio.App.Services;

/// <summary>The UI languages and which one to use; applied once at startup (texts are bound statically).</summary>
public static class AppLanguages
{
    /// <summary>Culture names with a translation; the first is the neutral (fallback) one.</summary>
    public static IReadOnlyList<string> Supported { get; } = ["en", "de"];

    /// <summary>The system's UI language, before <see cref="Apply"/> changed it.</summary>
    public static CultureInfo SystemCulture { get; private set; } = CultureInfo.CurrentUICulture;

    /// <summary>The language the running app shows.</summary>
    public static CultureInfo Current { get; private set; } = Resolve(null, CultureInfo.CurrentUICulture);

    /// <summary>
    /// The chosen language if supported; otherwise the system's (kept as is, e.g. de-AT, when it has a translation)
    /// or English.
    /// </summary>
    public static CultureInfo Resolve(string? setting, CultureInfo system)
    {
        if (setting is not null && Supported.Contains(setting, StringComparer.OrdinalIgnoreCase))
            return CultureInfo.GetCultureInfo(setting);
        if (Supported.Contains(Neutral(system), StringComparer.OrdinalIgnoreCase))
            return system;
        return CultureInfo.GetCultureInfo(Supported[0]);
    }

    /// <summary>Sets the UI language of every thread; number and date formats stay the system's.</summary>
    public static void Apply(string? setting)
    {
        var culture = Resolve(setting, SystemCulture);
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentUICulture = culture;
        Current = culture;
    }

    /// <summary>Whether <paramref name="setting"/> shows a different language than the running app.</summary>
    public static bool NeedsRestart(string? setting) =>
        !string.Equals(Neutral(Resolve(setting, SystemCulture)), Neutral(Current), StringComparison.OrdinalIgnoreCase);

    /// <summary>"de" for "de-AT".</summary>
    public static string Neutral(CultureInfo culture)
    {
        while (!culture.IsNeutralCulture && culture.Parent != CultureInfo.InvariantCulture)
            culture = culture.Parent;
        return culture.Name;
    }
}
