using System.Text.Json;
using System.Text.Json.Serialization;
using BorgStudio.Core.Repositories;

namespace BorgStudio.Core.Checks;

/// <summary>How thoroughly <c>borg check</c> looks.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CheckDepth>))]
public enum CheckDepth
{
    /// <summary>The repository's segments and the archives' metadata (over SSH the server reads the segments itself).</summary>
    Normal,

    /// <summary>Also downloads and decrypts all data (<c>--verify-data</c>) – as much traffic as the repository is big.</summary>
    Full,

    /// <summary>Repairs what it can (<c>--repair</c>); may lose damaged data.</summary>
    Repair,
}

[JsonConverter(typeof(JsonStringEnumConverter<CheckOutcome>))]
public enum CheckOutcome
{
    /// <summary>No damage found.</summary>
    Ok,

    /// <summary>borg found damage.</summary>
    ProblemsFound,

    /// <summary>A repair ran (it found and fixed what it could; a check afterwards tells whether all is well).</summary>
    Repaired,

    /// <summary>The check could not run (e.g. no connection, wrong passphrase).</summary>
    Failed,

    /// <summary>Stopped by the user.</summary>
    Cancelled,
}

/// <summary>One check of a repository.</summary>
/// <param name="Messages">What borg reported (errors and warnings), for the log.</param>
/// <param name="Error">Why the check could not run (<see cref="CheckOutcome.Failed"/>).</param>
public sealed record CheckRecord(DateTimeOffset Started, TimeSpan Duration, CheckDepth Depth, CheckOutcome Outcome)
{
    public IReadOnlyList<string> Messages { get; init; } = [];

    public string? Error { get; init; }

    /// <summary>The check ran to its end and tells the repository's state (a repair is followed by a check that does).</summary>
    [JsonIgnore]
    public bool IsVerdict => Outcome is CheckOutcome.Ok or CheckOutcome.ProblemsFound;
}

/// <summary>
/// The checks of each repository, newest first, in checks/&lt;repository&gt;.json in the data folder (the last
/// <see cref="MaximumRecords"/>). Shared by the window and scheduled runs in other processes.
/// </summary>
public sealed class RepositoryCheckStore(string directory)
{
    public const int MaximumRecords = 50;

    private static readonly object Gate = new();

    public static RepositoryCheckStore ForDataDirectory(string dataDirectory) => new(Path.Combine(dataDirectory, "checks"));

    public IReadOnlyList<CheckRecord> Get(Guid repositoryId)
    {
        lock (Gate)
            return Load(repositoryId);
    }

    /// <summary>The last check that ran to its end, i.e. the repository's known state; <c>null</c> if never checked.</summary>
    public CheckRecord? LastVerdict(Guid repositoryId) => Get(repositoryId).FirstOrDefault(record => record.IsVerdict);

    public void Add(Guid repositoryId, CheckRecord record)
    {
        lock (Gate)
        {
            List<CheckRecord> records = [record, .. Load(repositoryId)];
            try
            {
                AtomicFile.WriteAllText(FilePath(repositoryId),
                    JsonSerializer.Serialize(records.OrderByDescending(entry => entry.Started).Take(MaximumRecords), RepositoryStore.JsonOptions));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // The check itself is done; only its record is missing.
            }
        }
    }

    public void Remove(Guid repositoryId)
    {
        lock (Gate)
        {
            try
            {
                File.Delete(FilePath(repositoryId));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private string FilePath(Guid repositoryId) => Path.Combine(directory, $"{repositoryId:N}.json");

    private List<CheckRecord> Load(Guid repositoryId)
    {
        var path = FilePath(repositoryId);
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<List<CheckRecord>>(File.ReadAllText(path), RepositoryStore.JsonOptions) ?? []
                : [];
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
