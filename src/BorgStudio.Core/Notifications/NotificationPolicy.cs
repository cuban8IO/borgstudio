using System.Text.Json;
using BorgStudio.Core.Backups;

namespace BorgStudio.Core.Notifications;

/// <summary>What to report about a run: its event, and whether it only repeats a problem already reported.</summary>
public sealed record NotificationDecision(NotificationEvent Event, bool IsRepeat);

/// <summary>
/// Turns a finished run into its notification event and remembers each job's open problem (in notifications/state.json),
/// so a problem that comes back unchanged can be left out and the first good run after it is reported as
/// <see cref="NotificationEvent.Recovered"/>. Shared by the window and scheduled runs in other processes.
/// </summary>
public sealed class NotificationPolicy(string stateFile)
{
    public static NotificationPolicy ForDataDirectory(string dataDirectory) =>
        new(Path.Combine(dataDirectory, "notifications", "state.json"));

    private static readonly object Gate = new();

    /// <summary>
    /// The event for <paramref name="run"/>, or <c>null</c> for nothing to report (stopped by the user). Updates the
    /// job's open problem either way.
    /// </summary>
    public NotificationDecision? Decide(Guid jobId, BackupRunSummary run)
    {
        if (run.Outcome == BackupOutcome.Cancelled)
            return null;

        lock (Gate)
        {
            var problems = Load();
            var key = jobId.ToString("N");
            var open = problems.GetValueOrDefault(key);

            if (run.Outcome == BackupOutcome.Succeeded)
            {
                if (open is null)
                    return new NotificationDecision(NotificationEvent.Succeeded, false);
                problems.Remove(key);
                Save(problems);
                return new NotificationDecision(NotificationEvent.Recovered, false);
            }

            var problem = $"{run.Outcome}|{run.SkipReason}|{run.Message}";
            if (problem != open)
            {
                problems[key] = problem;
                Save(problems);
            }
            var notificationEvent = run.Outcome switch
            {
                BackupOutcome.Warnings => NotificationEvent.Warnings,
                BackupOutcome.Skipped => NotificationEvent.Skipped,
                _ => NotificationEvent.Failed,
            };
            return new NotificationDecision(notificationEvent, problem == open);
        }
    }

    /// <summary>
    /// The event for a repository's storage level, or <c>null</c> if there is nothing to report: back to normal, or better
    /// than before (full → almost full). A level that stays is a repeat.
    /// </summary>
    public NotificationDecision? DecideStorage(Guid repositoryId, Storage.StorageLevel level)
    {
        lock (Gate)
        {
            var states = Load();
            var key = $"storage-{repositoryId:N}";
            var previous = states.GetValueOrDefault(key);
            if (level == Storage.StorageLevel.Ok)
            {
                if (states.Remove(key))
                    Save(states);
                return null;
            }

            var current = level.ToString();
            if (current != previous)
            {
                states[key] = current;
                Save(states);
            }
            if (level == Storage.StorageLevel.Low && previous == nameof(Storage.StorageLevel.Full))
                return null;
            return new NotificationDecision(
                level == Storage.StorageLevel.Full ? NotificationEvent.StorageFull : NotificationEvent.StorageLow, current == previous);
        }
    }

    /// <summary>The job was deleted.</summary>
    public void Forget(Guid jobId)
    {
        lock (Gate)
        {
            var problems = Load();
            if (problems.Remove(jobId.ToString("N")))
                Save(problems);
        }
    }

    private Dictionary<string, string> Load()
    {
        try
        {
            return File.Exists(stateFile)
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(stateFile)) ?? []
                : [];
        }
        catch (Exception exception) when (exception is JsonException or IOException)
        {
            return [];
        }
    }

    private void Save(Dictionary<string, string> problems)
    {
        try
        {
            AtomicFile.WriteAllText(stateFile, JsonSerializer.Serialize(problems));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Then a repeat may be reported once more – no reason to fail a backup.
        }
    }
}
