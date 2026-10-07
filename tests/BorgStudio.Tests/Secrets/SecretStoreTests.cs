using BorgStudio.Core.Secrets;
using BorgStudio.Tests.TestSupport;

namespace BorgStudio.Tests.Secrets;

/// <summary>Against the real keychain of the OS; every test cleans up its own entry.</summary>
public sealed class SecretStoreTests : IDisposable
{
    private readonly ISecretStore _store = SecretStores.CreateDefault();
    private readonly string _key = $"test/{Guid.NewGuid():N}";

    public void Dispose()
    {
        if (_store.IsAvailable)
            _store.Delete(_key);
    }

    [KeychainFact]
    public void Stores_replaces_and_deletes_a_secret()
    {
        Assert.True(_store.IsAvailable);
        Assert.Null(_store.Get(_key));

        _store.Set(_key, "BorgStudio test", "first");
        Assert.Equal("first", _store.Get(_key));

        _store.Set(_key, "BorgStudio test", "pässwörd with ünïcödé 🔐");
        Assert.Equal("pässwörd with ünïcödé 🔐", _store.Get(_key));

        _store.Delete(_key);
        Assert.Null(_store.Get(_key));
    }

    [KeychainFact]
    public void Deleting_a_missing_secret_is_fine()
    {
        _store.Delete(_key);
        Assert.Null(_store.Get(_key));
    }
}
