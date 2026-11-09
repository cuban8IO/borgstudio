using BorgStudio.Core.Settings;
using BorgStudio.Tests.TestSupport;

namespace BorgStudio.Tests.Settings;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly TemporaryDirectory _directory = new();

    public void Dispose() => _directory.Dispose();

    [Fact]
    public void Without_a_file_the_defaults_apply()
    {
        var settings = new SettingsStore(_directory.Combine("settings.json")).Load();

        Assert.Equal(15, settings.VaultAutoLockMinutes);
    }

    [Fact]
    public void Saved_settings_are_loaded_again()
    {
        var store = new SettingsStore(_directory.Combine("sub", "settings.json"));

        store.Save(new AppSettings { VaultAutoLockMinutes = 0 });

        Assert.Equal(0, store.Load().VaultAutoLockMinutes);
    }

    [Fact]
    public void A_broken_file_means_defaults()
    {
        var path = _directory.Combine("settings.json");
        File.WriteAllText(path, "{ broken");

        Assert.Equal(AppSettings.DefaultVaultAutoLockMinutes, new SettingsStore(path).Load().VaultAutoLockMinutes);
    }
}
