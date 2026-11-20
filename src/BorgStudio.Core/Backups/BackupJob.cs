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

    /// <summary>Not started (see <see cref="BackupRunSummary.SkipReason"/>); nothing changed in the repository.</summary>
    Skipped,
}

/// <summary>Why a run did not start (a run started by hand is skipped; a scheduled one counts as failed, except <see cref="PassphraseAsked"/>).</summary>
public enum BackupSkipReason
{
    /// <summary>Another BorgStudio run (e.g. a scheduled one) is working on the same repository.</summary>
    RepositoryBusy,

    /// <summary>The repository asks for its passphrase every time: nobody is there to type it.</summary>
    PassphraseAsked,

    /// <summary>The passphrase should be stored but is not (e.g. after resetting the vault).</summary>
    PassphraseMissing,

    /// <summary>The vault is protected by a PIN and scheduled runs were not allowed to open it.</summary>
    ScheduledAccessMissing,

    /// <summary>The repository's folder is not there (e.g. the USB disk is not connected).</summary>
    TargetMissing,

    /// <summary>No supported borg was found.</summary>
    BorgMissing,

    /// <summary>The job's repository is no longer set up (or its provider plugin is gone).</summary>
    RepositoryMissing,

    /// <summary>The SSH key for the server is missing.</summary>
    SshKeyMissing,

    /// <summary>The server's host key was never confirmed: that needs the window.</summary>
    HostUnknown,
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

    /// <summary>Why it was <see cref="BackupOutcome.Skipped"/>.</summary>
    public BackupSkipReason? SkipReason { get; init; }

    /// <summary>The backup failed because the repository's disk or server is full.</summary>
    public bool OutOfSpace { get; init; }

    /// <summary>The backup failed because borg found the repository locked (by another borg, or a lock left behind).</summary>
    public bool RepositoryLocked { get; init; }

    /// <summary>The clean-up after the backup (prune + compact); <c>null</c> if the job has none or the backup failed.</summary>
    public CleanupSummary? Cleanup { get; init; }
}

/// <summary>The outcome of cleaning up a job's archives: <c>borg prune</c>, then <c>borg compact</c> if anything was deleted.</summary>
public sealed record CleanupSummary
{
    /// <summary>How many archives prune deleted.</summary>
    public int PrunedArchives { get; init; }

    /// <summary>What compact freed (borg's rounded figure); <c>null</c> if compact didn't run or failed.</summary>
    public long? FreedBytes { get; init; }

    /// <summary>Stopped by the user.</summary>
    public bool Cancelled { get; init; }

    /// <summary>Why prune or compact failed.</summary>
    public string? Error { get; init; }
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

    /// <summary>Which of the job's archives to keep after each backup; <c>null</c>: keep all (no clean-up).</summary>
    public RetentionPolicy? Retention { get; init; }

    /// <summary>When the job runs by itself; none: only by hand.</summary>
    public List<BackupSchedule> Schedules { get; init; } = [];

    /// <summary>A scheduled time missed while the computer was off is made up for once, at the next start.</summary>
    public bool CatchUp { get; init; } = true;

    public bool IsScheduled => Schedules.Any(schedule => schedule.IsValid);

    /// <summary>The job's runs are reported through the notification channels (see the app's notification settings).</summary>
    public bool Notify { get; init; } = true;

    /// <summary>Report the job as overdue after this many days without a successful backup; 0: never.</summary>
    public int OverdueDays { get; init; }

    /// <summary>The incoming webhook may start the job (see <see cref="Trigger.TriggerServer"/>).</summary>
    public bool WebhookTrigger { get; init; }

    /// <summary>How the job's newest archive is restored on trial with the repository's automatic checks.</summary>
    public Checks.RestoreTestMode RestoreTest { get; init; } = Checks.RestoreTestMode.Off;

    /// <summary>How many files a sample restores (<see cref="Checks.RestoreTestMode.Read"/>, <see cref="Checks.RestoreTestMode.Compare"/>).</summary>
    public int RestoreTestFiles { get; init; } = 20;

    public BackupRunSummary? LastRun { get; init; }
}

/// <summary>Keeps the backup jobs in jobs.json (atomically replaced on every save).</summary>
public sealed class BackupJobStore(string filePath)
{
    public static BackupJobStore CreateDefault() => new(Path.Combine(AppPaths.DataDirectory, "jobs.json"));

    public string FilePath { get; } = filePath;

    /// <summary>The folder of jobs.json; the runs' history and the repository locks live below it.</summary>
    public string DataDirectory => Path.GetDirectoryName(Path.GetFullPath(FilePath))!;

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

    /// <summary>
    /// Saves <paramref name="jobs"/>. A run in another process (a scheduled backup) may have recorded a newer last run
    /// since they were loaded: that one is kept, so the app does not overwrite it with what it still had in memory.
    /// </summary>
    public void Save(IEnumerable<BackupJob> jobs)
    {
        lock (Gate)
        {
            var saved = Load().ToDictionary(job => job.Id);
            Write([.. jobs.Select(job => saved.GetValueOrDefault(job.Id)?.LastRun is { } newer
                                         && (job.LastRun is null || newer.Started > job.LastRun.Started)
                ? job with { LastRun = newer }
                : job)]);
        }
    }

    /// <summary>Records a finished run for one job, leaving everything else as it is on disk.</summary>
    public void UpdateLastRun(Guid jobId, BackupRunSummary run)
    {
        lock (Gate)
            Write([.. Load().Select(job => job.Id == jobId ? job with { LastRun = run } : job)]);
    }

    private static readonly object Gate = new();

    private void Write(List<BackupJob> jobs)
    {
        AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(new StoreFile { Jobs = jobs }, RepositoryStore.JsonOptions));
    }

    private sealed class StoreFile
    {
        public int Version { get; init; } = 1;

        public List<BackupJob> Jobs { get; init; } = [];
    }
}
