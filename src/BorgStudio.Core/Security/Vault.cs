using System.Security.Cryptography;
using System.Text.Json;
using BorgStudio.Core.Ssh;

namespace BorgStudio.Core.Security;

public enum VaultState
{
    /// <summary>No PIN set up yet: there is no vault file.</summary>
    NotSetUp,

    Locked,

    Unlocked,
}

/// <summary>The vault is locked (or not set up): unlock it first.</summary>
public sealed class VaultLockedException() : InvalidOperationException("The vault is locked.");

/// <summary>
/// BorgStudio's own encrypted store for secrets (repository passphrases, SSH keys), protected by a PIN: the key is derived from
/// the PIN with Argon2id, the contents are encrypted with AES-256-GCM (see <see cref="SecretBox"/>). Without the PIN
/// nothing in the file can be read. While unlocked, the key and the contents are kept in memory; every change rewrites
/// the file with a fresh nonce.
/// </summary>
public sealed class Vault
{
    public const int MinimumPinLength = 6;

    private const string Format = "borgstudio-vault";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly Lock _gate = new();
    private readonly KdfParameters _parameters;
    private readonly TimeProvider _time;
    private byte[]? _key;
    private byte[]? _salt;
    private KdfParameters? _keyParameters;
    private Contents? _contents;

    /// <param name="parameters">Argon2id cost for new vaults and PIN changes; existing files keep their own.</param>
    public Vault(string filePath, KdfParameters? parameters = null, TimeProvider? time = null)
    {
        FilePath = filePath;
        _parameters = parameters ?? KdfParameters.Default;
        _time = time ?? TimeProvider.System;
        LastActivity = _time.GetUtcNow();
    }

    public static Vault CreateDefault() => new(Path.Combine(AppPaths.DataDirectory, "vault.json"));

    public string FilePath { get; }

    public VaultState State
    {
        get
        {
            lock (_gate)
                return _key is not null ? VaultState.Unlocked : File.Exists(FilePath) ? VaultState.Locked : VaultState.NotSetUp;
        }
    }

    /// <summary>Raised after setting up, unlocking, locking and resetting – on the thread that did it.</summary>
    public event EventHandler? StateChanged;

    /// <summary>Last use of the app (see <see cref="Touch"/>), for <see cref="LockIfIdle"/>.</summary>
    public DateTimeOffset LastActivity { get; private set; }

    public static bool IsValidPin(string pin) => pin.Trim().Length >= MinimumPinLength;

    /// <summary>Creates an empty vault protected by <paramref name="pin"/> and leaves it unlocked. Slow (key derivation).</summary>
    public void SetUp(string pin)
    {
        if (!IsValidPin(pin))
            throw new ArgumentException($"The PIN needs at least {MinimumPinLength} characters.", nameof(pin));

        var salt = SecretBox.NewSalt();
        var key = SecretBox.DeriveKey(pin, salt, _parameters);
        lock (_gate)
        {
            if (File.Exists(FilePath))
                throw new InvalidOperationException("The vault is already set up.");
            _key = key;
            _salt = salt;
            _keyParameters = _parameters;
            _contents = new Contents();
            Save();
        }

        Touch();
        OnStateChanged();
    }

    /// <summary>Opens the vault; <c>false</c> if the PIN is wrong. Slow (key derivation).</summary>
    /// <exception cref="SecretBoxException">The file is damaged.</exception>
    public bool Unlock(string pin)
    {
        SecretEnvelope envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<SecretEnvelope>(File.ReadAllText(FilePath), JsonOptions)
                ?? throw new SecretBoxException("The vault file is empty.");
        }
        catch (FileNotFoundException)
        {
            throw new VaultLockedException();
        }
        catch (JsonException)
        {
            throw new SecretBoxException("The vault file is damaged.");
        }

        if (envelope.Format != Format)
            throw new SecretBoxException("This is not a BorgStudio vault.");

        byte[] plaintext, key;
        try
        {
            (plaintext, key) = SecretBox.Open(envelope, pin);
        }
        catch (SecretBoxException)
        {
            return false;
        }

        Contents contents;
        try
        {
            contents = JsonSerializer.Deserialize<Contents>(plaintext, JsonOptions) ?? new Contents();
        }
        catch (JsonException)
        {
            throw new SecretBoxException("The vault contents are damaged.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }

        lock (_gate)
        {
            _key = key;
            _salt = Convert.FromBase64String(envelope.Salt);
            _keyParameters = envelope.Parameters;
            _contents = contents;
        }

        Touch();
        OnStateChanged();
        return true;
    }

    /// <summary>Forgets the key and the decrypted contents.</summary>
    public void Lock()
    {
        lock (_gate)
        {
            if (_key is null)
                return;
            CryptographicOperations.ZeroMemory(_key);
            _key = null;
            _salt = null;
            _contents = null;
        }

        OnStateChanged();
    }

    /// <summary>Re-encrypts the vault with a key from <paramref name="newPin"/>; <c>false</c> if <paramref name="currentPin"/> is wrong.</summary>
    public bool ChangePin(string currentPin, string newPin)
    {
        if (!IsValidPin(newPin))
            throw new ArgumentException($"The PIN needs at least {MinimumPinLength} characters.", nameof(newPin));
        if (!Unlock(currentPin))
            return false;

        var salt = SecretBox.NewSalt();
        var key = SecretBox.DeriveKey(newPin, salt, _parameters);
        lock (_gate)
        {
            CryptographicOperations.ZeroMemory(_key);
            _key = key;
            _salt = salt;
            _keyParameters = _parameters;
            Save();
        }

        return true;
    }

    /// <summary>Deletes the vault and everything in it (forgotten PIN). Afterwards it can be set up again.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            if (_key is not null)
                CryptographicOperations.ZeroMemory(_key);
            _key = null;
            _salt = null;
            _contents = null;
            File.Delete(FilePath);
        }

        OnStateChanged();
    }

    /// <summary>Notes that the app is being used (keeps the vault from locking itself).</summary>
    public void Touch() => LastActivity = _time.GetUtcNow();

    /// <summary>Locks the vault if the app was not used for <paramref name="timeout"/>; <c>true</c> if it did.</summary>
    public bool LockIfIdle(TimeSpan timeout)
    {
        if (State != VaultState.Unlocked || _time.GetUtcNow() - LastActivity < timeout)
            return false;
        Lock();
        return true;
    }

    // --- Passphrases ---

    /// <exception cref="VaultLockedException" />
    public string? GetPassphrase(Guid repositoryId)
    {
        lock (_gate)
            return Unlocked().Passphrases.GetValueOrDefault(Id(repositoryId));
    }

    /// <exception cref="VaultLockedException" />
    public void SetPassphrase(Guid repositoryId, string passphrase)
    {
        lock (_gate)
        {
            Unlocked().Passphrases[Id(repositoryId)] = passphrase;
            Save();
        }
    }

    /// <exception cref="VaultLockedException" />
    public void RemovePassphrase(Guid repositoryId)
    {
        lock (_gate)
        {
            if (Unlocked().Passphrases.Remove(Id(repositoryId)))
                Save();
        }
    }

    /// <summary>Drops passphrases of repositories that no longer exist.</summary>
    /// <exception cref="VaultLockedException" />
    public void RemovePassphrasesExcept(IEnumerable<Guid> repositoryIds)
    {
        var keep = repositoryIds.Select(Id).ToHashSet();
        lock (_gate)
        {
            var passphrases = Unlocked().Passphrases;
            var unused = passphrases.Keys.Where(id => !keep.Contains(id)).ToList();
            foreach (var id in unused)
                passphrases.Remove(id);
            if (unused.Count > 0)
                Save();
        }
    }

    // --- SSH keys ---

    /// <summary>All keys, sorted by name.</summary>
    /// <exception cref="VaultLockedException" />
    public IReadOnlyList<SshKeyEntry> Keys
    {
        get
        {
            lock (_gate)
                return [.. Unlocked().Keys.OrderBy(key => key.Name, StringComparer.CurrentCultureIgnoreCase)];
        }
    }

    /// <exception cref="VaultLockedException" />
    public SshKeyEntry? GetKey(Guid id)
    {
        lock (_gate)
            return Unlocked().Keys.FirstOrDefault(key => key.Id == id);
    }

    /// <summary>Adds a key, or replaces the one with the same id.</summary>
    /// <exception cref="VaultLockedException" />
    public void SetKey(SshKeyEntry key)
    {
        lock (_gate)
        {
            var keys = Unlocked().Keys;
            var index = keys.FindIndex(existing => existing.Id == key.Id);
            if (index >= 0)
                keys[index] = key;
            else
                keys.Add(key);
            Save();
        }
    }

    /// <exception cref="VaultLockedException" />
    public void RemoveKey(Guid id)
    {
        lock (_gate)
        {
            if (Unlocked().Keys.RemoveAll(key => key.Id == id) > 0)
                Save();
        }
    }

    private static string Id(Guid repositoryId) => repositoryId.ToString("N");

    private Contents Unlocked() => _contents ?? throw new VaultLockedException();

    /// <summary>Writes the encrypted contents next to the file and swaps, so a crash never leaves half a vault.</summary>
    private void Save()
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(_contents, JsonOptions);
        var envelope = SecretBox.Seal(Format, plaintext, _key!, _salt!, _keyParameters!);
        CryptographicOperations.ZeroMemory(plaintext);

        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temporary = FilePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(envelope, JsonOptions));
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temporary, FilePath, overwrite: true);
    }

    private void OnStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>What is encrypted inside the vault file.</summary>
    private sealed class Contents
    {
        /// <summary>Repository id ("N" format) → passphrase.</summary>
        public Dictionary<string, string> Passphrases { get; init; } = [];

        public List<SshKeyEntry> Keys { get; init; } = [];
    }
}
