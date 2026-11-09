using System.Text.Json;
using BorgStudio.Core.Security;

namespace BorgStudio.Core.Borg;

/// <summary>
/// The archives last seen per repository, with the details borg took long to work out (<c>borg info</c> reads all
/// metadata of an archive for its own share). Archives never change, so the details stay valid until archives are
/// deleted. One file per repository in <see cref="Directory"/>: encrypted with the vault's key when a PIN is set up
/// (<c>&lt;id&gt;.vault</c>), plain JSON otherwise (<c>&lt;id&gt;.json</c>). The contents of opened archives are kept
/// alongside (see ArchiveCache.Contents.cs).
/// </summary>
public sealed partial class ArchiveCache(string directory, Vault vault)
{
    private const string Format = "borgstudio-archive-cache";
    private const int Version = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static ArchiveCache CreateDefault(Vault vault) => new(Path.Combine(AppPaths.DataDirectory, "archives"), vault);

    public string Directory { get; } = directory;

    /// <summary>The archives last seen, newest first; <c>null</c> if nothing (readable) is cached.</summary>
    public IReadOnlyList<BorgArchive>? Load(Guid repositoryId)
    {
        try
        {
            var state = vault.State;
            if (state == VaultState.Unlocked && File.Exists(SealedFile(repositoryId)))
            {
                var envelope = JsonSerializer.Deserialize<SecretEnvelope>(File.ReadAllText(SealedFile(repositoryId)), JsonOptions);
                return envelope is null || vault.Open(envelope) is not { } plaintext ? null : Read(plaintext);
            }
            // Written before the PIN was set up: still good, it is encrypted on the next save.
            if (state != VaultState.Locked && File.Exists(PlainFile(repositoryId)))
                return Read(File.ReadAllBytes(PlainFile(repositoryId)));
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException or VaultLockedException)
        {
            // A cache only: start over.
        }
        return null;
    }

    /// <summary>Remembers the archives of a repository. Does nothing while the vault is locked.</summary>
    public void Save(Guid repositoryId, IReadOnlyList<BorgArchive> archives)
    {
        var state = vault.State;
        if (state == VaultState.Locked)
            return;

        var plaintext = JsonSerializer.SerializeToUtf8Bytes(new Contents(Version, [.. archives]), JsonOptions);
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            if (state == VaultState.Unlocked)
            {
                Write(SealedFile(repositoryId), JsonSerializer.SerializeToUtf8Bytes(vault.Seal(Format, plaintext), JsonOptions));
                File.Delete(PlainFile(repositoryId));
            }
            else
            {
                Write(PlainFile(repositoryId), plaintext);
                File.Delete(SealedFile(repositoryId));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or VaultLockedException)
        {
            // Not cached this time.
        }
    }

    public void Remove(Guid repositoryId)
    {
        TryDelete(PlainFile(repositoryId));
        TryDelete(SealedFile(repositoryId));
        TryDeleteDirectory(ContentsDirectory(repositoryId));
    }

    /// <summary>Drops the caches of repositories that no longer exist.</summary>
    public void RemoveAllExcept(IEnumerable<Guid> repositoryIds)
    {
        if (!System.IO.Directory.Exists(Directory))
            return;
        var keep = repositoryIds.Select(id => id.ToString("N")).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var file in System.IO.Directory.EnumerateFiles(Directory))
            if (!keep.Contains(Path.GetFileNameWithoutExtension(file)))
                TryDelete(file);
        RemoveContentsExcept(keep);
    }

    /// <summary>
    /// After the vault changed: with the vault open, plain caches get encrypted; without a vault (reset), the encrypted
    /// ones can no longer be read and are deleted.
    /// </summary>
    public void Secure()
    {
        if (!System.IO.Directory.Exists(Directory))
            return;
        switch (vault.State)
        {
            case VaultState.Unlocked:
                foreach (var file in System.IO.Directory.EnumerateFiles(Directory, "*.json"))
                    if (Guid.TryParse(Path.GetFileNameWithoutExtension(file), out var id) && Load(id) is { } archives)
                        Save(id, archives);
                    else
                        TryDelete(file);
                SecureContents();
                break;
            case VaultState.NotSetUp:
                foreach (var file in System.IO.Directory.EnumerateFiles(Directory, "*.vault"))
                    TryDelete(file);
                SecureContents();
                break;
        }
    }

    private string PlainFile(Guid repositoryId) => Path.Combine(Directory, repositoryId.ToString("N") + ".json");

    private string SealedFile(Guid repositoryId) => Path.Combine(Directory, repositoryId.ToString("N") + ".vault");

    private static IReadOnlyList<BorgArchive>? Read(byte[] json)
    {
        var contents = JsonSerializer.Deserialize<Contents>(json, JsonOptions);
        return contents?.Version == Version ? contents.Archives : null;
    }

    /// <summary>Writes next to the file and swaps, so a crash never leaves half a cache.</summary>
    private static void Write(string path, byte[] data)
    {
        var temporary = path + ".tmp";
        File.WriteAllBytes(temporary, data);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temporary, path, overwrite: true);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private sealed record Contents(int Version, List<BorgArchive> Archives);
}
