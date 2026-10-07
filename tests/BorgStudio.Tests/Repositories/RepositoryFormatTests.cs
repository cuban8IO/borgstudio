using BorgStudio.Core.Repositories;
using BorgStudio.Plugins;
using BorgStudio.Tests.TestSupport;

namespace BorgStudio.Tests.Repositories;

public sealed class RepositoryFormatTests : IDisposable
{
    private readonly TemporaryDirectory _directory = new();

    public void Dispose() => _directory.Dispose();

    [Fact]
    public void Detects_borg1_repository_by_its_config_file()
    {
        File.WriteAllText(_directory.Combine("config"), "[repository]\nversion = 1\nsegments_per_dir = 1000\n");

        Assert.Equal(1, RepositoryFormat.DetectLocal(_directory.Path));
    }

    [Fact]
    public void Detects_borg2_repository_by_its_config_directory()
    {
        Directory.CreateDirectory(_directory.Combine("config"));

        Assert.Equal(2, RepositoryFormat.DetectLocal(_directory.Path));
    }

    [Fact]
    public void Empty_missing_or_foreign_folders_have_no_format()
    {
        File.WriteAllText(_directory.Combine("config"), "something else");

        Assert.Null(RepositoryFormat.DetectLocal(_directory.Path));
        Assert.Null(RepositoryFormat.DetectLocal(_directory.Combine("missing")));
    }

    [Fact]
    public void Resolve_prefers_the_users_choice_then_detection_then_borg1()
    {
        Directory.CreateDirectory(_directory.Combine("config"));
        var local = new RepositoryLocation(_directory.Path);
        var remote = new RepositoryLocation("ssh://user@host/./repo");

        Assert.Equal(1, RepositoryFormat.Resolve(BorgVersionPreference.Borg1, local));
        Assert.Equal(2, RepositoryFormat.Resolve(BorgVersionPreference.Auto, local));
        Assert.Equal(1, RepositoryFormat.Resolve(BorgVersionPreference.Auto, remote));
        Assert.Equal(2, RepositoryFormat.Resolve(BorgVersionPreference.Borg2, remote));
        Assert.Equal(1, RepositoryFormat.Resolve(BorgVersionPreference.Auto, new RepositoryLocation(_directory.Combine("new"))));
    }
}
