namespace BorgStudio.App.Services;

/// <summary>Where BorgStudio lives on the web; the product page's links follow the UI language.</summary>
public static class AppLinks
{
    public const string RepositoryUrl = "https://github.com/cuban8IO/borgstudio";

    public const string SiteUrl = "https://borgstudio.cuban8io.net/";

    private static bool German => AppLanguages.Neutral(AppLanguages.Current) == "de";

    public static Uri Website => new(German ? $"{SiteUrl}de/" : SiteUrl);

    public static Uri Guide { get; } = new($"{RepositoryUrl}/wiki");

    public static Uri Source { get; } = new(RepositoryUrl);

    public static Uri LegalNotice => new(German ? $"{SiteUrl}de/impressum.html" : $"{SiteUrl}legal-notice.html");

    public static Uri Privacy => new(German ? $"{SiteUrl}de/datenschutz.html" : $"{SiteUrl}privacy.html");

    public static Uri License { get; } = new($"{RepositoryUrl}/blob/main/LICENSE");

    /// <summary>
    /// THIRD-PARTY-NOTICES.txt of a published package: next to the program, in a macOS bundle under Contents/Resources;
    /// null for a local build, which has none.
    /// </summary>
    public static Uri? ThirdPartyNotices()
    {
        foreach (var folder in new[] { AppContext.BaseDirectory, Path.Combine(AppContext.BaseDirectory, "..", "Resources") })
        {
            var file = Path.GetFullPath(Path.Combine(folder, "THIRD-PARTY-NOTICES.txt"));
            if (File.Exists(file))
                return new Uri(file);
        }
        return null;
    }
}
