using System.Text.Json;
using BorgStudio.Core.Backups;
using BorgStudio.Core.Checks;
using BorgStudio.Core.Plugins;
using BorgStudio.Plugins;

namespace BorgStudio.Core.Repositories;

/// <summary>
/// Reports of finished runs for providers that want them (<see cref="IRunReportReceiver"/>, e.g. BorgStudio Server). A
/// report is first written to a file of its own in <c>reports/</c> (so a run never waits for the network and nothing is
/// lost while the service is unreachable or the vault is locked), then <see cref="SendAsync"/> hands the waiting ones
/// over, oldest first. Shared by the window and scheduled runs in other processes.
/// </summary>
public sealed class RunReports(string directory, PluginCatalog plugins, RepositoryStore repositories)
{
    /// <summary>Reports kept while they cannot be delivered; the oldest go first.</summary>
    public const int Limit = 1000;

    private const string LockFile = ".sending";

    public static RunReports ForDataDirectory(string dataDirectory, PluginCatalog plugins, RepositoryStore repositories) =>
        new(Path.Combine(dataDirectory, "reports"), plugins, repositories);

    public string Directory => directory;

    /// <summary>The reports still waiting to be delivered (all repositories).</summary>
    public int Pending => Files().Count;

    /// <summary>Queues a report if the repository's provider takes reports; <c>false</c> if it does not (nothing to do).</summary>
    public bool Add(Guid repositoryId, RunReport report)
    {
        if (Receiver(repositoryId) is null)
            return false;
        try
        {
            System.IO.Directory.CreateDirectory(directory);
            // The name sorts by time; the GUID keeps reports of the same moment apart.
            var name = $"{DateTimeOffset.UtcNow:yyyyMMddHHmmssfffffff}-{Guid.NewGuid():N}.json";
            AtomicFile.WriteAllText(Path.Combine(directory, name), JsonSerializer.Serialize(new Entry(repositoryId, report), RepositoryStore.JsonOptions));
            foreach (var old in Files().SkipLast(Limit))
                TryDelete(old);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The run is done; only its report is missing.
            return false;
        }
    }

    /// <summary>
    /// Delivers the waiting reports, oldest first. A repository whose service is not reachable keeps its reports (in
    /// order) for the next time; the others go on. Returns how many were delivered. Another process sending at the same
    /// time: nothing happens here.
    /// </summary>
    public async Task<int> SendAsync(CancellationToken cancellationToken = default)
    {
        if (!System.IO.Directory.Exists(directory))
            return 0;
        FileStream? gate;
        try
        {
            gate = new FileStream(Path.Combine(directory, LockFile), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
                1, FileOptions.DeleteOnClose);
        }
        catch (IOException)
        {
            return 0;
        }

        using (gate)
        {
            var sent = 0;
            var postponed = new HashSet<Guid>();
            foreach (var file in Files())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Read(file) is not { } entry)
                {
                    TryDelete(file);
                    continue;
                }
                if (postponed.Contains(entry.RepositoryId))
                    continue;
                if (Receiver(entry.RepositoryId) is not { } target)
                {
                    // The repository is gone or no longer at such a service.
                    TryDelete(file);
                    continue;
                }

                try
                {
                    await target.Receiver.SendReportAsync(new RunReportRequest(target.Repository.ProviderValues, entry.Report), cancellationToken);
                    TryDelete(file);
                    sent++;
                }
                catch (RunReportRejectedException)
                {
                    TryDelete(file);
                }
                catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    // Unreachable, no credentials now, or a plugin's failure: later, in the same order.
                    postponed.Add(entry.RepositoryId);
                }
            }
            return sent;
        }
    }

    private (RepositoryConfig Repository, IRunReportReceiver Receiver)? Receiver(Guid repositoryId) =>
        repositories.Load().Repositories.FirstOrDefault(repository => repository.Id == repositoryId) is { } repository
        && plugins.FindRepositoryProvider(repository.ProviderId) is IRunReportReceiver receiver
            ? (repository, receiver)
            : null;

    private List<string> Files()
    {
        try
        {
            return System.IO.Directory.Exists(directory)
                ? [.. System.IO.Directory.EnumerateFiles(directory, "*.json").Order(StringComparer.Ordinal)]
                : [];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static Entry? Read(string file)
    {
        try
        {
            return JsonSerializer.Deserialize<Entry>(File.ReadAllText(file), RepositoryStore.JsonOptions);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private sealed record Entry(Guid RepositoryId, RunReport Report);

    // --- What the runs become ---

    /// <summary>A backup (not one that never started) and the repository's figures afterwards.</summary>
    public static RunReport? FromBackup(BackupRunSummary run, Storage.RepositoryStats? repository) =>
        run.Outcome == BackupOutcome.Skipped
            ? null
            : new RunReport(RunReportKind.Backup, Outcome(run.Outcome), run.Started, run.Duration)
            {
                ArchiveName = run.ArchiveName,
                OriginalSize = run.OriginalSize,
                CompressedSize = run.CompressedSize > 0 ? run.CompressedSize : null,
                DeduplicatedSize = run.DeduplicatedSize,
                Files = run.Files,
                TotalSize = repository?.TotalSize,
                TotalCompressedSize = repository?.TotalCompressedSize,
                UniqueCompressedSize = repository?.UniqueCompressedSize,
                Message = run.Message,
            };

    public static RunReport FromCheck(CheckRecord check) =>
        new(RunReportKind.Check, check.Outcome switch
        {
            CheckOutcome.Ok => RunReportOutcome.Succeeded,
            CheckOutcome.Repaired => RunReportOutcome.Warnings,
            CheckOutcome.Cancelled => RunReportOutcome.Cancelled,
            _ => RunReportOutcome.Failed,
        }, check.Started, check.Duration)
        {
            Message = check.Error ?? check.Messages.FirstOrDefault(),
        };

    /// <summary>A prune or compact by itself (<paramref name="error"/>: why it failed).</summary>
    public static RunReport Cleanup(RunReportKind kind, DateTimeOffset started, TimeSpan duration, bool cancelled, string? error) =>
        new(kind, cancelled ? RunReportOutcome.Cancelled : error is null ? RunReportOutcome.Succeeded : RunReportOutcome.Failed, started, duration)
        {
            Message = error,
        };

    private static RunReportOutcome Outcome(BackupOutcome outcome) => outcome switch
    {
        BackupOutcome.Succeeded => RunReportOutcome.Succeeded,
        BackupOutcome.Warnings => RunReportOutcome.Warnings,
        BackupOutcome.Cancelled => RunReportOutcome.Cancelled,
        BackupOutcome.Skipped => RunReportOutcome.Skipped,
        _ => RunReportOutcome.Failed,
    };
}
