using BorgStudio.Core.Borg;

namespace BorgStudio.Tests.Borg;

public class BorgCompatibilityTests
{
    [Theory]
    [InlineData("borg 1.1.18", BorgSupport.TooOld)]
    [InlineData("borg 1.2.0", BorgSupport.Supported)]
    [InlineData("borg 1.4.5", BorgSupport.Supported)]
    [InlineData("borg 1.5.0", BorgSupport.Supported)]
    [InlineData("borg 0.30.0", BorgSupport.TooOld)]
    [InlineData("borg 2.0.0b14", BorgSupport.NotYetSupported)]
    public void Evaluate_classifies_versions(string versionOutput, BorgSupport expected)
    {
        Assert.True(BorgVersion.TryParse(versionOutput, out var version));
        Assert.Equal(expected, BorgCompatibility.Evaluate(version));
    }
}
