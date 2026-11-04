using BorgStudio.App;

namespace BorgStudio.Tests.ViewModels;

public sealed class AppVersionTests
{
    [Theory]
    [InlineData("0.2.0+3f2a9c1d", "0.2.0")]
    [InlineData("0.0.0-dev+abc", "0.0.0-dev")]
    [InlineData("1.4.0", "1.4.0")]
    public void Build_metadata_is_not_shown(string informationalVersion, string expected) =>
        Assert.Equal(expected, AppVersion.Display(informationalVersion));

    [Fact]
    public void The_current_version_comes_from_the_build()
    {
        // 0.0.0-dev from Directory.Build.props unless a build passes -p:Version.
        Assert.NotEmpty(AppVersion.Current);
        Assert.DoesNotContain('+', AppVersion.Current);
    }
}
