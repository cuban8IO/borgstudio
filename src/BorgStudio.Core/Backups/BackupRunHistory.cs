using System.Text.Json;
using BorgStudio.Core.Repositories;

namespace BorgStudio.Core.Backups;

/// <summary>The last runs of each job (newest first), in history/&lt;job&gt;.json next to jobs.json.</summary>
public sealed class BackupRunHistory(string directory)
{
    /// <summary>Runs kept per job.</summary>
    public const int Limit = 100;

    public string Directory => directory;

    private string FileOf(Guid jobId) => Path.Combine(directory, $"{jobId:N}.json");

    public IReadOnlyList<BackupRunSummary> Get(Guid jobId)
    {
        var file = FileOf(jobId);
        if (!File.Exists(file))
            return [];
        try
        {
            return JsonSerializer.Deserialize<List<BackupRunSummary>>(File.ReadAllText(file), RepositoryStore.JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public void Add(Guid jobId, BackupRunSummary run)
    {
        var runs = new List<BackupRunSummary> { run };
        runs.AddRange(Get(jobId).Take(Limit - 1));
        AtomicFile.WriteAllText(FileOf(jobId), JsonSerializer.Serialize(runs, RepositoryStore.JsonOptions));
    }

    /// <summary>The job was deleted.</summary>
    public void Remove(Guid jobId)
    {
        var file = FileOf(jobId);
        if (File.Exists(file))
            File.Delete(file);
    }
}
