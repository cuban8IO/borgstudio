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
}
