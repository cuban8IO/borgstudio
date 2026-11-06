using System.Text.Json;
using BorgStudio.Core.Repositories;

namespace BorgStudio.Core.Backups;

/// <summary>What a backup job leaves out.</summary>
public sealed record BackupExclusions
{
    /// <summary>Temporary files and caches (Windows Temp/INetCache, ~/.cache, ~/Library/Caches).</summary>
    public bool CachesAndTemp { get; init; } = true;

    /// <summary>Build output and dependencies of software projects (node_modules, bin/Debug, obj, .venv, …).</summary>
    public bool DevelopmentFolders { get; init; }

    /// <summary>Recycle bins ($RECYCLE.BIN, .Trash, ~/.local/share/Trash).</summary>
    public bool RecycleBin { get; init; } = true;

    /// <summary>Folders marked with a CACHEDIR.TAG file (borg's <c>--exclude-caches</c>).</summary>
    public bool CacheDirTag { get; init; } = true;

    /// <summary>File extensions to leave out, e.g. ".iso" (case-insensitive).</summary>
    public List<string> Extensions { get; init; } = [];

    /// <summary>Folders to leave out (paths on this computer).</summary>
    public List<string> Folders { get; init; } = [];

    /// <summary>Further patterns in borg syntax (see <c>borg help patterns</c>).</summary>
    public List<string> Patterns { get; init; } = [];
}

public enum BackupOutcome
{
    Succeeded,

    /// <summary>borg finished with warnings (e.g. files that could not be read).</summary>
    Warnings,

    Failed,

    /// <summary>Stopped by the user; borg left a checkpoint archive.</summary>
    Cancelled,
}

/// <summary>The last run of a job, kept to show it and to estimate the next run.</summary>
public sealed record BackupRunSummary
{
    public required DateTimeOffset Started { get; init; }

    public required TimeSpan Duration { get; init; }

    public required BackupOutcome Outcome { get; init; }

    public string? ArchiveName { get; init; }

    public long OriginalSize { get; init; }

    public long CompressedSize { get; init; }

    public long DeduplicatedSize { get; init; }

    public long Files { get; init; }

    /// <summary>Why it failed, or the first warning.</summary>
    public string? Message { get; init; }
}

/// <summary>A backup job: what to back up from where into which repository.</summary>
public sealed record BackupJob
{
    public const string DefaultCompression = "zstd,3";

    public required Guid Id { get; init; }

    public required string Name { get; init; }

    public required Guid RepositoryId { get; init; }

    /// <summary>Folders to back up (paths on this computer).</summary>
    public List<string> Sources { get; init; } = [];

    public BackupExclusions Exclusions { get; init; } = new();

    /// <summary>borg compression spec, e.g. "zstd,3", "lz4" or "none".</summary>
    public string Compression { get; init; } = DefaultCompression;

    public BackupRunSummary? LastRun { get; init; }
}

/// <summary>Keeps the backup jobs in jobs.json (atomically replaced on every save).</summary>
public sealed class BackupJobStore(string filePath)
{
    public static BackupJobStore CreateDefault() => new(Path.Combine(AppPaths.DataDirectory, "jobs.json"));

    public string FilePath { get; } = filePath;

    /// <summary>The saved jobs; an unreadable file is moved aside (like repositories.json) and none are returned.</summary>
    public IReadOnlyList<BackupJob> Load()
    {
        if (!File.Exists(FilePath))
            return [];

        try
        {
            return JsonSerializer.Deserialize<StoreFile>(File.ReadAllText(FilePath), RepositoryStore.JsonOptions)?.Jobs ?? [];
        }
        catch (JsonException)
        {
            File.Move(FilePath, $"{FilePath}.broken-{DateTime.Now:yyyyMMdd-HHmmss}");
            return [];
        }
    }

    public void Save(IEnumerable<BackupJob> jobs)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temporary = FilePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new StoreFile { Jobs = [.. jobs] }, RepositoryStore.JsonOptions));
        File.Move(temporary, FilePath, overwrite: true);
    }

    private sealed class StoreFile
    {
        public int Version { get; init; } = 1;

        public List<BackupJob> Jobs { get; init; } = [];
    }
}
