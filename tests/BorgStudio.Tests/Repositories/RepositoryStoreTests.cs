using BorgStudio.Core.Repositories;
using BorgStudio.Tests.TestSupport;

namespace BorgStudio.Tests.Repositories;

public sealed class RepositoryStoreTests : IDisposable
{
    private readonly TemporaryDirectory _directory = new();

    public void Dispose() => _directory.Dispose();

    private RepositoryStore Store() => new(_directory.Combine("data", "repositories.json"));

    private static RepositoryConfig Sample(string name = "External disk") => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        ProviderId = "local",
        ProviderValues = new Dictionary<string, string> { ["path"] = @"E:\borg" },
        PassphraseMode = PassphraseMode.Stored,
        BorgVersion = BorgVersionPreference.Auto,
        EncryptionMode = "repokey-blake2",
        BorgRepositoryId = "abc123",
    };

    [Fact]
    public void Missing_file_means_no_repositories()
    {
        var result = Store().Load();

        Assert.Empty(result.Repositories);
        Assert.Null(result.BrokenFileBackup);
    }

    [Fact]
    public void Saves_and_loads_repositories()
    {
        var first = Sample();
        var second = Sample("NAS") with { PassphraseMode = PassphraseMode.Ask, EncryptionMode = null };

        Store().Save([first, second]);
        var loaded = Store().Load().Repositories;

        Assert.Equal(2, loaded.Count);
        Assert.Equal(first with { ProviderValues = loaded[0].ProviderValues }, loaded[0]);
        Assert.Equal(first.ProviderValues, loaded[0].ProviderValues);
        Assert.Equal(PassphraseMode.Ask, loaded[1].PassphraseMode);
        Assert.Null(loaded[1].EncryptionMode);
    }

    [Fact]
    public void File_is_readable_json_without_secrets()
    {
        Store().Save([Sample()]);

        var json = File.ReadAllText(Store().FilePath);
        Assert.Contains("\"passphraseMode\": \"stored\"", json);
        Assert.Contains("\"borgVersion\": \"auto\"", json);
        Assert.DoesNotContain("secretKey", json, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Store().FilePath + ".tmp"));
    }

    [Fact]
    public void Broken_file_is_moved_aside_instead_of_being_overwritten()
    {
        var store = Store();
        store.Save([Sample()]);
        File.WriteAllText(store.FilePath, "{ this is not json");

        var result = store.Load();

        Assert.Empty(result.Repositories);
        Assert.NotNull(result.BrokenFileBackup);
        Assert.Equal("{ this is not json", File.ReadAllText(result.BrokenFileBackup));
        Assert.False(File.Exists(store.FilePath));
    }
}
