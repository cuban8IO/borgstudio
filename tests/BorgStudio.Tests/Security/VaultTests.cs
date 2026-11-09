using BorgStudio.Core.Security;
using BorgStudio.Tests.TestSupport;

namespace BorgStudio.Tests.Security;

public sealed class VaultTests : IDisposable
{
    private const string Pin = "123456";

    private readonly TemporaryDirectory _directory = new();
    private readonly ManualTime _time = new();

    public void Dispose() => _directory.Dispose();

    private string FilePath => _directory.Combine("vault.json");

    private Vault NewVault(KdfParameters? kdf = null) => new(FilePath, kdf ?? TestServices.FastKdf, _time);

    [Fact]
    public void Setting_up_creates_an_unlocked_vault()
    {
        var vault = NewVault();
        Assert.Equal(VaultState.NotSetUp, vault.State);
        var changes = 0;
        vault.StateChanged += (_, _) => changes++;

        vault.SetUp(Pin);

        Assert.Equal(VaultState.Unlocked, vault.State);
        Assert.True(File.Exists(FilePath));
        Assert.Equal(1, changes);
        Assert.Throws<InvalidOperationException>(() => NewVault().SetUp(Pin));
    }

    [Fact]
    public void Passphrases_survive_a_restart_and_are_not_readable_in_the_file()
    {
        var id = Guid.NewGuid();
        var vault = NewVault();
        vault.SetUp(Pin);
        vault.SetPassphrase(id, "my secret passphrase");

        Assert.DoesNotContain("my secret passphrase", File.ReadAllText(FilePath));
        Assert.DoesNotContain(id.ToString("N"), File.ReadAllText(FilePath));

        var restarted = NewVault();
        Assert.Equal(VaultState.Locked, restarted.State);
        Assert.Throws<VaultLockedException>(() => restarted.GetPassphrase(id));
        Assert.False(restarted.Unlock("654321"));
        Assert.Equal(VaultState.Locked, restarted.State);
        Assert.True(restarted.Unlock(Pin));
        Assert.Equal("my secret passphrase", restarted.GetPassphrase(id));
        Assert.Null(restarted.GetPassphrase(Guid.NewGuid()));
    }

    [Fact]
    public void Locking_forgets_the_contents()
    {
        var id = Guid.NewGuid();
        var vault = NewVault();
        vault.SetUp(Pin);
        vault.SetPassphrase(id, "secret");

        vault.Lock();

        Assert.Equal(VaultState.Locked, vault.State);
        Assert.Throws<VaultLockedException>(() => vault.GetPassphrase(id));
        Assert.Throws<VaultLockedException>(() => vault.SetPassphrase(id, "other"));
    }

    [Fact]
    public void The_pin_must_be_long_enough()
    {
        Assert.False(Vault.IsValidPin("12345"));
        Assert.False(Vault.IsValidPin("  1234  "));
        Assert.True(Vault.IsValidPin("abc123"));
        Assert.Throws<ArgumentException>(() => NewVault().SetUp("12345"));
        Assert.False(File.Exists(FilePath));
    }

    [Fact]
    public void Changing_the_pin_needs_the_current_one()
    {
        var id = Guid.NewGuid();
        var vault = NewVault();
        vault.SetUp(Pin);
        vault.SetPassphrase(id, "secret");
        vault.Lock();

        Assert.False(vault.ChangePin("000000", "abcdefgh"));
        Assert.True(vault.ChangePin(Pin, "abcdefgh"));

        var restarted = NewVault();
        Assert.False(restarted.Unlock(Pin));
        Assert.True(restarted.Unlock("abcdefgh"));
        Assert.Equal("secret", restarted.GetPassphrase(id));
    }

    [Fact]
    public void A_vault_keeps_the_cost_it_was_created_with()
    {
        // Created with one cost, then used by an app whose default cost is different.
        var id = Guid.NewGuid();
        NewVault(new KdfParameters(64, 1, 1)).SetUp(Pin);

        var other = NewVault(new KdfParameters(128, 2, 1));
        Assert.True(other.Unlock(Pin));
        other.SetPassphrase(id, "secret");

        var again = NewVault(new KdfParameters(256, 1, 1));
        Assert.True(again.Unlock(Pin));
        Assert.Equal("secret", again.GetPassphrase(id));
    }

    [Fact]
    public void Resetting_deletes_everything()
    {
        var vault = NewVault();
        vault.SetUp(Pin);
        vault.SetPassphrase(Guid.NewGuid(), "secret");

        vault.Reset();

        Assert.Equal(VaultState.NotSetUp, vault.State);
        Assert.False(File.Exists(FilePath));
        vault.SetUp("new-pin-1");
        Assert.Equal(VaultState.Unlocked, vault.State);
    }

    [Fact]
    public void Locks_itself_after_the_idle_time()
    {
        var vault = NewVault();
        vault.SetUp(Pin);

        _time.Advance(TimeSpan.FromMinutes(10));
        Assert.False(vault.LockIfIdle(TimeSpan.FromMinutes(15)));

        vault.Touch();
        _time.Advance(TimeSpan.FromMinutes(14));
        Assert.False(vault.LockIfIdle(TimeSpan.FromMinutes(15)));
        Assert.Equal(VaultState.Unlocked, vault.State);

        _time.Advance(TimeSpan.FromMinutes(1));
        Assert.True(vault.LockIfIdle(TimeSpan.FromMinutes(15)));
        Assert.Equal(VaultState.Locked, vault.State);
    }

    [Fact]
    public void Passphrases_of_removed_repositories_can_be_dropped()
    {
        var (keep, drop) = (Guid.NewGuid(), Guid.NewGuid());
        var vault = NewVault();
        vault.SetUp(Pin);
        vault.SetPassphrase(keep, "keep");
        vault.SetPassphrase(drop, "drop");

        vault.RemovePassphrasesExcept([keep]);
        vault.RemovePassphrase(Guid.NewGuid());

        Assert.Equal("keep", vault.GetPassphrase(keep));
        Assert.Null(vault.GetPassphrase(drop));
    }

    [Fact]
    public void A_damaged_file_is_reported()
    {
        File.WriteAllText(FilePath, "{ not json");

        var vault = NewVault();

        Assert.Equal(VaultState.Locked, vault.State);
        Assert.Throws<SecretBoxException>(() => vault.Unlock(Pin));
    }
}
