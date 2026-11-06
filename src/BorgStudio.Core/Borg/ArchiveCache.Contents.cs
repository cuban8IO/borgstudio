using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BorgStudio.Core.Security;

namespace BorgStudio.Core.Borg;

/// <summary>
/// The contents (file lists) of archives that were opened, in <c>contents/&lt;repository&gt;/&lt;archive id&gt;</c>:
/// gzip-compressed, sealed with the vault's key when a PIN is set up (<c>.vault</c>), plain otherwise (<c>.gz</c>).
/// Valid forever, since archives never change; dropped with the archive.
/// </summary>
public sealed partial class ArchiveCache
{
    private const string ContentsFormat = "borgstudio-archive-contents";
    private const int ContentsVersion = 1;

    /// <summary>The cached contents of an archive; <c>null</c> if not cached or not readable (e.g. vault locked).</summary>
    public ArchiveTree? LoadContents(Guid repositoryId, string archiveId)
    {
        if (!IsArchiveId(archiveId))
            return null;
        try
        {
            var state = vault.State;
            var sealedFile = ContentsFile(repositoryId, archiveId, sealedFile: true);
            if (state == VaultState.Unlocked && File.Exists(sealedFile))
            {
                var envelope = JsonSerializer.Deserialize<SecretEnvelope>(File.ReadAllText(sealedFile), JsonOptions);
                return envelope is null || vault.Open(envelope) is not { } compressed ? null : ReadContents(compressed);
            }
            var plainFile = ContentsFile(repositoryId, archiveId, sealedFile: false);
            if (state != VaultState.Locked && File.Exists(plainFile))
                return ReadContents(File.ReadAllBytes(plainFile));
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException
                                              or VaultLockedException or InvalidDataException or InvalidOperationException
                                              or IndexOutOfRangeException or FormatException)
        {
            // A cache only: borg is asked again.
        }
        return null;
    }

    /// <summary>Remembers the contents of an archive. Does nothing while the vault is locked.</summary>
    public void SaveContents(Guid repositoryId, string archiveId, ArchiveTree tree)
    {
        var state = vault.State;
        if (state == VaultState.Locked || !IsArchiveId(archiveId))
            return;
        try
        {
            var compressed = WriteContents(tree);
            System.IO.Directory.CreateDirectory(ContentsDirectory(repositoryId));
            if (state == VaultState.Unlocked)
            {
                Write(ContentsFile(repositoryId, archiveId, sealedFile: true),
                    JsonSerializer.SerializeToUtf8Bytes(vault.Seal(ContentsFormat, compressed), JsonOptions));
                TryDelete(ContentsFile(repositoryId, archiveId, sealedFile: false));
            }
            else
            {
                Write(ContentsFile(repositoryId, archiveId, sealedFile: false), compressed);
                TryDelete(ContentsFile(repositoryId, archiveId, sealedFile: true));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or VaultLockedException)
        {
            // Not cached this time.
        }
    }

    /// <summary>Drops the contents of archives that no longer exist (deleted, e.g. by prune).</summary>
    public void KeepContents(Guid repositoryId, IEnumerable<string> archiveIds)
    {
        var folder = ContentsDirectory(repositoryId);
        if (!System.IO.Directory.Exists(folder))
            return;
        var keep = archiveIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var file in System.IO.Directory.EnumerateFiles(folder))
            if (!keep.Contains(Path.GetFileNameWithoutExtension(file)))
                TryDelete(file);
    }

    private void RemoveContentsExcept(HashSet<string> repositoryIds)
    {
        if (!System.IO.Directory.Exists(ContentsRoot))
            return;
        foreach (var folder in System.IO.Directory.EnumerateDirectories(ContentsRoot))
            if (!repositoryIds.Contains(Path.GetFileName(folder)))
                TryDeleteDirectory(folder);
    }

    /// <summary>Like the archive lists: encrypted once the vault is open, dropped when it was reset.</summary>
    private void SecureContents()
    {
        if (!System.IO.Directory.Exists(ContentsRoot))
            return;
        if (vault.State == VaultState.NotSetUp)
        {
            foreach (var file in ContentsFiles("*.vault"))
                TryDelete(file);
            return;
        }
        if (vault.State != VaultState.Unlocked)
            return;
        foreach (var file in ContentsFiles("*.gz"))
        {
            var archiveId = Path.GetFileNameWithoutExtension(file);
            if (Guid.TryParse(Path.GetFileName(Path.GetDirectoryName(file)), out var repositoryId)
                && LoadContents(repositoryId, archiveId) is { } tree)
                SaveContents(repositoryId, archiveId, tree);
            else
                TryDelete(file);
        }
    }

    private string ContentsRoot => Path.Combine(Directory, "contents");

    private string ContentsDirectory(Guid repositoryId) => Path.Combine(ContentsRoot, repositoryId.ToString("N"));

    private string ContentsFile(Guid repositoryId, string archiveId, bool sealedFile) =>
        Path.Combine(ContentsDirectory(repositoryId), archiveId + (sealedFile ? ".vault" : ".gz"));

    private IEnumerable<string> ContentsFiles(string pattern) =>
        System.IO.Directory.Exists(ContentsRoot)
            ? [.. System.IO.Directory.EnumerateFiles(ContentsRoot, pattern, SearchOption.AllDirectories)]
            : [];

    private static bool IsArchiveId(string archiveId) => ArchiveIdPattern().IsMatch(archiveId);

    /// <summary>A header line, then one JSON array per entry (parents before children), gzip-compressed.</summary>
    private static byte[] WriteContents(ArchiveTree tree)
    {
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.Fastest, leaveOpen: true))
        using (var writer = new StreamWriter(gzip, new UTF8Encoding(false)))
        {
            writer.WriteLine(JsonSerializer.Serialize(new ContentsHeader(ContentsFormat, ContentsVersion), JsonOptions));
            foreach (var node in tree.All())
            {
                if (node.IsImplicit)
                    continue; // comes back with what is inside it
                writer.WriteLine(JsonSerializer.Serialize(new object?[]
                {
                    (int)node.Kind, node.BorgPath, node.IsDirectory ? 0 : node.Size,
                    node.Modified == DateTimeOffset.MinValue ? null : node.Modified.ToUnixTimeMilliseconds(), node.LinkTarget,
                }));
            }
        }
        return buffer.ToArray();
    }

    private static ArchiveTree? ReadContents(byte[] compressed)
    {
        using var reader = new StreamReader(new GZipStream(new MemoryStream(compressed), CompressionMode.Decompress), Encoding.UTF8);
        var header = reader.ReadLine() is { } first ? JsonSerializer.Deserialize<ContentsHeader>(first, JsonOptions) : null;
        if (header is not { Format: ContentsFormat, Version: ContentsVersion })
            return null;

        var tree = new ArchiveTree();
        while (reader.ReadLine() is { } line)
        {
            using var entry = JsonDocument.Parse(line);
            var values = entry.RootElement;
            var modified = values[3].ValueKind == JsonValueKind.Number
                ? DateTimeOffset.FromUnixTimeMilliseconds(values[3].GetInt64()).ToLocalTime()
                : DateTimeOffset.MinValue;
            tree.Add(new BorgItem(values[1].GetString()!, (ArchiveEntryKind)values[0].GetInt32(), values[2].GetInt64(), modified,
                values[4].ValueKind == JsonValueKind.String ? values[4].GetString() : null));
        }
        return tree.Complete();
    }

    [GeneratedRegex("^[0-9a-fA-F]{8,128}$", RegexOptions.CultureInvariant)]
    private static partial Regex ArchiveIdPattern();

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (System.IO.Directory.Exists(path))
                System.IO.Directory.Delete(path, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private sealed record ContentsHeader(string Format, int Version);
}
