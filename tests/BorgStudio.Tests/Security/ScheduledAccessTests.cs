using BorgStudio.Core.Security;
using BorgStudio.Tests.TestSupport;

namespace BorgStudio.Tests.Security;

/// <summary>The key that lets scheduled runs open the PIN-protected vault.</summary>
public sealed class ScheduledAccessTests : IDisposable
{
    private const string Pin = "123456";

    private readonly TemporaryDirectory _directory = new();
    private readonly Vault _vault;

    public ScheduledAccessTests() => _vault = new Vault(_directory.Combine("vault.json"), TestServices.FastKdf);

    public void Dispose() => _directory.Dispose();

    private ScheduledAccess Access(FakeSecretStore keychain) => new(_vault, keychain, _directory.Combine("scheduled-access.key"));

    [Fact]
    public void Without_a_pin_nothing_is_needed()
    {
        var access = Access(new FakeSecretStore());
        Assert.True(access.IsEnabled);
        Assert.False(access.TryUnlock());
    }

    [Fact]
    public void The_key_in_the_keychain_opens_the_locked_vault()
    {
        var keychain = new FakeSecretStore();
        var access = Access(keychain);
        _vault.SetUp(Pin);
        _vault.SetPassphrase(Guid.Empty, "secret");
        Assert.False(access.IsEnabled);

        access.Enable();
        Assert.True(access.IsEnabled);
        Assert.True(keychain.Secrets.ContainsKey(ScheduledAccess.SecretKey));
        Assert.False(File.Exists(access.FallbackFile));

        _vault.Lock();
        Assert.True(access.TryUnlock());
        Assert.Equal(VaultState.Unlocked, _vault.State);
        Assert.Equal("secret", _vault.GetPassphrase(Guid.Empty));
    }

    [Fact]
    public void A_new_pin_takes_the_key_along()
    {
        var keychain = new FakeSecretStore();
        var access = Access(keychain);
        _vault.SetUp(Pin);
        access.Enable();
        var before = keychain.Secrets[ScheduledAccess.SecretKey];

        Assert.True(_vault.ChangePin(Pin, "abcdefgh"));

        Assert.NotEqual(before, keychain.Secrets[ScheduledAccess.SecretKey]);
        _vault.Lock();
        Assert.True(access.TryUnlock());
        // The old key no longer fits.
        _vault.Lock();
        Assert.False(_vault.UnlockWithKey(Convert.FromBase64String(before)));
    }

    [Fact]
    public void Without_scheduled_runs_a_new_pin_stores_nothing()
    {
        var keychain = new FakeSecretStore();
        _ = Access(keychain);
        _vault.SetUp(Pin);
        Assert.True(_vault.ChangePin(Pin, "abcdefgh"));
        Assert.Empty(keychain.Secrets);
    }

    [Fact]
    public void Resetting_the_vault_removes_the_key()
    {
        var keychain = new FakeSecretStore();
        var access = Access(keychain);
        _vault.SetUp(Pin);
        access.Enable();

        _vault.Reset();

        Assert.Empty(keychain.Secrets);
        Assert.True(access.IsEnabled); // no PIN any more: nothing needed
    }

    [Fact]
    public void Without_a_keychain_the_key_goes_into_a_private_file()
    {
        var access = Access(new FakeSecretStore(available: false));
        Assert.True(access.UsesFile);
        _vault.SetUp(Pin);

        access.Enable();

        Assert.True(File.Exists(access.FallbackFile));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(access.FallbackFile));
        _vault.Lock();
        Assert.True(access.TryUnlock());

        access.Disable();
        Assert.False(File.Exists(access.FallbackFile));
        _vault.Lock();
        Assert.False(access.TryUnlock());
    }

    [Fact]
    public void Exporting_needs_an_open_vault_and_a_wrong_key_does_not_open_it()
    {
        _vault.SetUp(Pin);
        var key = _vault.ExportKey();
        _vault.Lock();
        Assert.Throws<VaultLockedException>(() => _vault.ExportKey());

        Assert.False(_vault.UnlockWithKey(new byte[key.Length]));
        Assert.Equal(VaultState.Locked, _vault.State);
        Assert.True(_vault.UnlockWithKey(key));
    }
}
