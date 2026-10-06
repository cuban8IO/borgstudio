using BorgStudio.Core.Borg;

namespace BorgStudio.Tests.Borg;

public class BorgVersionTests
{
    [Theory]
    [InlineData("borg 1.4.0", 1, 4, 0, null)]
    [InlineData("borg 1.2.8\n", 1, 2, 8, null)]
    [InlineData("borg-linux64 1.2.8", 1, 2, 8, null)]
    [InlineData("borg.exe 1.4.1\r\n", 1, 4, 1, null)]
    [InlineData("borg 2.0.0b14", 2, 0, 0, "b14")]
    public void TryParse_reads_version_output(string output, int major, int minor, int patch, string? pre)
    {
        Assert.True(BorgVersion.TryParse(output, out var version));
        Assert.Equal(new BorgVersion(major, minor, patch, pre), version);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("borg")]
    [InlineData("borg: command not found")]
    [InlineData("'borg' is not recognized as an internal or external command")]
    public void TryParse_rejects_anything_else(string? output)
    {
        Assert.False(BorgVersion.TryParse(output, out var version));
        Assert.Null(version);
    }

    [Fact]
    public void IsBorg2_distinguishes_major_versions()
    {
        Assert.False(new BorgVersion(1, 4, 0).IsBorg2);
        Assert.True(new BorgVersion(2, 0, 0, "b14").IsBorg2);
    }

    [Fact]
    public void ToString_round_trips_the_version_number()
    {
        Assert.Equal("2.0.0b14", new BorgVersion(2, 0, 0, "b14").ToString());
    }
}
