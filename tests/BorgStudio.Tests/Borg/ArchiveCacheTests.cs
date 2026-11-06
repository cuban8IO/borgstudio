using BorgStudio.Core.Borg;
using BorgStudio.Core.Security;
using BorgStudio.Tests.TestSupport;

namespace BorgStudio.Tests.Borg;

public sealed class ArchiveCacheTests : IDisposable
{
    private readonly TemporaryDirectory _directory = new();
    private readonly Vault _vault;
    private readonly ArchiveCache _cache;
    private readonly Guid _repository = Guid.NewGuid();

    private static readonly BorgArchive[] Archives =
    [
        new("Docs-nb-2", "bb22", new DateTimeOffset(2026, 11, 6, 9, 30, 15, TimeSpan.FromHours(1)), TimeSpan.FromSeconds(44.5),
            new BorgArchiveStats(5000, 3000, 120, 42)),
        new("Docs-nb-1", "aa11", new DateTimeOffset(2026, 11, 5, 9, 0, 0, TimeSpan.FromHours(1))),
    ];

    public ArchiveCacheTests()
    {
        _vault = new Vault(_directory.Combine("vault.json"), TestServices.FastKdf);
        _cache = new ArchiveCache(_directory.Combine("archives"), _vault);
    }

    public void Dispose() => _directory.Dispose();

    private string[] Files() => Directory.GetFiles(_cache.Directory).Select(Path.GetFileName).ToArray()!;

    [Fact]
    public void Without_a_PIN_the_archives_are_kept_as_plain_json()
    {
        Assert.Null(_cache.Load(_repository));

        _cache.Save(_repository, Archives);

        Assert.Equal([$"{_repository:N}.json"], Files());
        Assert.Equal(Archives, _cache.Load(_repository));
    }

    [Fact]
    public void With_a_PIN_they_are_encrypted_and_plain_files_get_encrypted_too()
    {
        _cache.Save(_repository, Archives);
        _vault.SetUp("123456");

        _cache.Secure();

        Assert.Equal([$"{_repository:N}.vault"], Files());
        Assert.DoesNotContain("Docs-nb", File.ReadAllText(Path.Combine(_cache.Directory, Files()[0])));
        Assert.Equal(Archives, _cache.Load(_repository));

        _vault.Lock();
        Assert.Null(_cache.Load(_repository));
        _cache.Save(_repository, []); // ignored while locked
        Assert.True(_vault.Unlock("123456"));
        Assert.Equal(Archives, _cache.Load(_repository));
    }

    [Fact]
    public void A_new_PIN_or_a_reset_makes_the_encrypted_cache_go()
    {
        _vault.SetUp("123456");
        _cache.Save(_repository, Archives);

        Assert.True(_vault.ChangePin("123456", "654321"));
        Assert.Null(_cache.Load(_repository)); // sealed under the old key: start over

        _cache.Save(_repository, Archives);
        _vault.Reset();
        _cache.Secure();
        Assert.Empty(Files());
    }

    [Fact]
    public void Caches_of_removed_repositories_are_dropped()
    {
        var other = Guid.NewGuid();
        _cache.Save(_repository, Archives);
        _cache.Save(other, Archives);

        _cache.RemoveAllExcept([other]);
        Assert.Null(_cache.Load(_repository));
        Assert.NotNull(_cache.Load(other));

        _cache.Remove(other);
        Assert.Empty(Files());
    }

    [Fact]
    public void A_damaged_cache_is_ignored()
    {
        _cache.Save(_repository, Archives);
        File.WriteAllText(Path.Combine(_cache.Directory, $"{_repository:N}.json"), "{ not json");

        Assert.Null(_cache.Load(_repository));
    }
}
