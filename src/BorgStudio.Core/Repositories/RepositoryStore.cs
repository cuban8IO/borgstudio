using System.Text.Json;
using System.Text.Json.Serialization;

namespace BorgStudio.Core.Repositories;

/// <summary>Result of <see cref="RepositoryStore.Load"/>.</summary>
/// <param name="BrokenFileBackup">
/// Set when repositories.json could not be read: it was moved to this path (nothing is lost) and an empty list is used.
/// </param>
public sealed record RepositoryStoreLoadResult(IReadOnlyList<RepositoryConfig> Repositories, string? BrokenFileBackup = null);

/// <summary>Keeps the list of repositories in a JSON file (atomically replaced on every save).</summary>
public sealed class RepositoryStore(string filePath)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower) },
    };

    public static RepositoryStore CreateDefault() => new(Path.Combine(AppPaths.DataDirectory, "repositories.json"));

    public string FilePath { get; } = filePath;

    public RepositoryStoreLoadResult Load()
    {
        if (!File.Exists(FilePath))
            return new RepositoryStoreLoadResult([]);

        try
        {
            var file = JsonSerializer.Deserialize<StoreFile>(File.ReadAllText(FilePath), JsonOptions);
            return new RepositoryStoreLoadResult(file?.Repositories ?? []);
        }
        catch (JsonException)
        {
            var backup = $"{FilePath}.broken-{DateTime.Now:yyyyMMdd-HHmmss}";
            File.Move(FilePath, backup);
            return new RepositoryStoreLoadResult([], backup);
        }
    }

    public void Save(IEnumerable<RepositoryConfig> repositories)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);

        // Write next to the target and swap, so a crash never leaves a half-written file behind.
        var temporary = FilePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new StoreFile { Repositories = [.. repositories] }, JsonOptions));
        File.Move(temporary, FilePath, overwrite: true);
    }

    private sealed class StoreFile
    {
        public int Version { get; init; } = 1;

        public List<RepositoryConfig> Repositories { get; init; } = [];
    }
}
