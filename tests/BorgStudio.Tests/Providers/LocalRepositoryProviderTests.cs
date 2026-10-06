using System.Globalization;
using BorgStudio.Providers.Local;

namespace BorgStudio.Tests.Providers;

public class LocalRepositoryProviderTests
{
    private readonly LocalRepositoryProvider _provider = new();

    private static Dictionary<string, string> Path(string path) => new() { [LocalRepositoryProvider.PathKey] = path };

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("relative/repo")]
    public void Rejects_missing_or_relative_paths(string path)
    {
        Assert.Single(_provider.Validate(Path(path)));
    }

    [Fact]
    public void Accepts_an_absolute_path_and_uses_it_as_location()
    {
        var folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "borg-repo");

        Assert.Empty(_provider.Validate(Path($"  {folder}  ")));
        var location = _provider.GetLocation(Path($"  {folder}  "));
        Assert.Equal(folder, location.Url);
        Assert.Empty(location.BorgArguments);
    }

    [Theory]
    [InlineData("en", "Local folder")]
    [InlineData("de", "Lokaler Ordner")]
    public void Texts_follow_the_UI_language(string culture, string expectedName)
    {
        var previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
            Assert.Equal(expectedName, _provider.DisplayName);
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }
}
