using System.Text.Json;
using BorgStudio.Core.Repositories;
using BorgStudio.Core.Security;
using BorgStudio.Core.Settings;
using BorgStudio.Core.Ssh;

namespace BorgStudio.Core.Transfer;

/// <summary>A borg repository key from <c>borg key export</c>.</summary>
/// <param name="Keyfile">The repository uses a keyfile (the key exists only on the computer, not in the repository).</param>
public sealed record ExportedBorgKey(string Key, bool Keyfile);

/// <summary>Everything BorgStudio knows, to set it up again elsewhere. Only ever stored encrypted (see <see cref="ExportFile"/>).</summary>
public sealed record ExportContents
{
    public int Version { get; init; } = 1;

    public DateTimeOffset Created { get; init; }

    public string? AppVersion { get; init; }

    /// <summary>Name of the computer the export comes from (shown when importing).</summary>
    public string? Machine { get; init; }

    public List<RepositoryConfig> Repositories { get; init; } = [];

    /// <summary>Repository id ("N" format) → passphrase, for repositories that keep it in the vault or the keychain.</summary>
    public Dictionary<string, string> Passphrases { get; init; } = [];

    public List<SshKeyEntry> SshKeys { get; init; } = [];

    /// <summary>BorgStudio's known_hosts file.</summary>
    public string KnownHosts { get; init; } = "";

    public AppSettings Settings { get; init; } = new();

    /// <summary>Repository id ("N" format) → its borg key.</summary>
    public Dictionary<string, ExportedBorgKey> BorgKeys { get; init; } = [];

    public string? PassphraseFor(Guid repositoryId) => Passphrases.GetValueOrDefault(repositoryId.ToString("N"));

    public ExportedBorgKey? BorgKeyFor(Guid repositoryId) => BorgKeys.GetValueOrDefault(repositoryId.ToString("N"));
}

/// <summary>The file is not a BorgStudio export, or one from a newer version.</summary>
public sealed class ExportFileException(string message) : Exception(message);

/// <summary>
/// An export file: <see cref="ExportContents"/> as JSON, encrypted like the vault (Argon2id + AES-256-GCM, see
/// <see cref="SecretBox"/>) but with a password of its own – the file may travel, so it needs more than a PIN.
/// </summary>
public static class ExportFile
{
    public const string Extension = "borgstudio";

    public const int MinimumPasswordLength = 12;

    private const string Format = "borgstudio-export";

    private static readonly JsonSerializerOptions EnvelopeOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static bool IsValidPassword(string password) => password.Length >= MinimumPasswordLength;

    /// <summary>Encrypts <paramref name="contents"/> with <paramref name="password"/> and writes the file. Slow (key derivation).</summary>
    public static void Write(string path, ExportContents contents, string password, KdfParameters? parameters = null)
    {
        if (!IsValidPassword(password))
            throw new ArgumentException($"The password needs at least {MinimumPasswordLength} characters.", nameof(password));

        var plaintext = JsonSerializer.SerializeToUtf8Bytes(contents, RepositoryStore.JsonOptions);
        try
        {
            var envelope = SecretBox.Seal(Format, plaintext, password, parameters ?? KdfParameters.Default);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.WriteAllText(path, JsonSerializer.Serialize(envelope, EnvelopeOptions));
        }
        finally
        {
            Array.Clear(plaintext);
        }
    }

    /// <summary>Reads and decrypts an export file. Slow (key derivation).</summary>
    /// <exception cref="SecretBoxException">Wrong password, or the file was changed.</exception>
    /// <exception cref="ExportFileException">Not a BorgStudio export, or from a newer version.</exception>
    public static ExportContents Read(string path, string password)
    {
        SecretEnvelope envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<SecretEnvelope>(File.ReadAllText(path), EnvelopeOptions)
                ?? throw new ExportFileException("The file is empty.");
        }
        catch (JsonException)
        {
            throw new ExportFileException("This is not a BorgStudio export.");
        }

        if (envelope.Format != Format)
            throw new ExportFileException("This is not a BorgStudio export.");

        var (plaintext, key) = SecretBox.Open(envelope, password);
        Array.Clear(key);
        try
        {
            var contents = JsonSerializer.Deserialize<ExportContents>(plaintext, RepositoryStore.JsonOptions)
                ?? throw new ExportFileException("The export is empty.");
            if (contents.Version > 1)
                throw new ExportFileException("The export comes from a newer BorgStudio version.");
            return contents;
        }
        catch (JsonException)
        {
            throw new ExportFileException("The export contents are damaged.");
        }
        finally
        {
            Array.Clear(plaintext);
        }
    }
}

public enum ImportMode
{
    /// <summary>Add what is new; existing entries are replaced only where chosen.</summary>
    Merge,

    /// <summary>The export replaces everything.</summary>
    Replace,
}

public static class ImportMerge
{
    /// <summary>
    /// The resulting list: with <see cref="ImportMode.Replace"/> just <paramref name="imported"/>; when merging, the
    /// current entries (those with an id in <paramref name="replace"/> swapped in place for the imported version)
    /// followed by the imported entries that are new.
    /// </summary>
    public static List<T> Merge<T>(IReadOnlyList<T> current, IReadOnlyList<T> imported, Func<T, Guid> id, ImportMode mode,
        IReadOnlySet<Guid> replace)
    {
        if (mode == ImportMode.Replace)
            return [.. imported];

        var importedById = imported.ToDictionary(id);
        var currentIds = current.Select(id).ToHashSet();
        return
        [
            .. current.Select(entry => replace.Contains(id(entry)) && importedById.TryGetValue(id(entry), out var newer) ? newer : entry),
            .. imported.Where(entry => !currentIds.Contains(id(entry))),
        ];
    }
}
