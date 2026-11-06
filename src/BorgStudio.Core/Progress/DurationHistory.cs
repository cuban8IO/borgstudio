using System.Text.Json;

namespace BorgStudio.Core.Progress;

/// <summary>
/// How long operations took last time, per repository (e.g. listing its archives), kept in durations.json. borg
/// doesn't report progress for everything; this lets the app still show roughly how long the wait will be.
/// </summary>
public sealed class DurationHistory(string filePath)
{
    private readonly Lock _lock = new();
    private Dictionary<string, double>? _seconds;

    public static DurationHistory CreateDefault() => new(Path.Combine(AppPaths.DataDirectory, "durations.json"));

    public string FilePath { get; } = filePath;

    /// <summary>The key for an operation on a repository, e.g. "&lt;id&gt;/list".</summary>
    public static string Key(Guid repositoryId, string operation) => Key(repositoryId.ToString("D"), operation);

    /// <summary>The key for an operation on something else, e.g. adding a repository with a provider.</summary>
    public static string Key(string scope, string operation) => $"{scope}/{operation}";

    public TimeSpan? Get(string key)
    {
        lock (_lock)
            return Load().TryGetValue(key, out var seconds) ? TimeSpan.FromSeconds(seconds) : null;
    }

    /// <summary>Remembers a duration; mixed with the previous one, so a single outlier doesn't count fully.</summary>
    public void Record(string key, TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
            return;
        lock (_lock)
        {
            var all = Load();
            all[key] = all.TryGetValue(key, out var previous) ? (previous + duration.TotalSeconds) / 2 : duration.TotalSeconds;
            Save(all);
        }
    }

    /// <summary>Forgets everything about a repository (it was removed).</summary>
    public void Remove(Guid repositoryId)
    {
        var prefix = $"{repositoryId:D}/";
        lock (_lock)
        {
            var all = Load();
            if (all.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)).ToList() is { Count: > 0 } keys)
            {
                foreach (var key in keys)
                    all.Remove(key);
                Save(all);
            }
        }
    }

    private Dictionary<string, double> Load()
    {
        if (_seconds is not null)
            return _seconds;
        try
        {
            _seconds = File.Exists(FilePath)
                ? JsonSerializer.Deserialize<Dictionary<string, double>>(File.ReadAllText(FilePath)) ?? []
                : [];
        }
        catch (Exception exception) when (exception is JsonException or IOException)
        {
            // Only estimates: start afresh.
            _seconds = [];
        }
        return _seconds;
    }

    private void Save(Dictionary<string, double> all)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var temporary = FilePath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(
                all.OrderBy(entry => entry.Key, StringComparer.Ordinal)
                    .ToDictionary(entry => entry.Key, entry => Math.Round(entry.Value, 1, MidpointRounding.AwayFromZero))));
            File.Move(temporary, FilePath, overwrite: true);
        }
        catch (IOException)
        {
            // Not worth bothering the user about.
        }
    }
}
