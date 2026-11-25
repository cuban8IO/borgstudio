namespace BorgStudio.Plugins;

/// <summary>
/// Optional for an <see cref="IRepositoryProvider"/> whose service wants to know what BorgStudio did with a repository
/// (e.g. a server keeping statistics). After every backup, check, prune and compact BorgStudio queues a
/// <see cref="RunReport"/> and hands it over; what cannot be delivered now is tried again later, in order.
/// </summary>
public interface IRunReportReceiver
{
    /// <summary>Delivers one report.</summary>
    /// <exception cref="RepositoryUnavailableException">Not now (service unreachable, no credentials yet): kept and tried again later.</exception>
    /// <exception cref="RunReportRejectedException">The service will never take this report (e.g. the repository is gone there): dropped.</exception>
    Task SendReportAsync(RunReportRequest request, CancellationToken cancellationToken);
}

/// <summary>A report to deliver.</summary>
/// <param name="Values">The repository's provider values (keyed by <see cref="ProviderField.Key"/>).</param>
/// <param name="Report">The run.</param>
public sealed record RunReportRequest(IReadOnlyDictionary<string, string> Values, RunReport Report);

/// <summary>The service refuses a report for good; it is not sent again.</summary>
public sealed class RunReportRejectedException(string message) : Exception(message);

/// <summary>What ran.</summary>
public enum RunReportKind
{
    /// <summary><c>borg create</c>.</summary>
    Backup,

    /// <summary><c>borg check</c> (or a repair).</summary>
    Check,

    /// <summary><c>borg prune</c>.</summary>
    Prune,

    /// <summary><c>borg compact</c>.</summary>
    Compact,
}

/// <summary>How it ended.</summary>
public enum RunReportOutcome
{
    /// <summary>Done without problems.</summary>
    Succeeded,

    /// <summary>Done, with warnings (e.g. unreadable files, a repair).</summary>
    Warnings,

    /// <summary>Did not work (or the check found damage).</summary>
    Failed,

    /// <summary>Stopped by the user.</summary>
    Cancelled,

    /// <summary>Did not start.</summary>
    Skipped,
}

/// <summary>One run and what borg said about it; sizes in bytes, <c>null</c> where borg gave no figure.</summary>
public sealed record RunReport(RunReportKind Kind, RunReportOutcome Outcome, DateTimeOffset StartedAt, TimeSpan Duration)
{
    /// <summary>The archive a backup created.</summary>
    public string? ArchiveName { get; init; }

    /// <summary>The new archive's size as on disk.</summary>
    public long? OriginalSize { get; init; }

    /// <summary>The new archive's size, compressed.</summary>
    public long? CompressedSize { get; init; }

    /// <summary>What the new archive added to the repository.</summary>
    public long? DeduplicatedSize { get; init; }

    /// <summary>Files in the new archive.</summary>
    public long? Files { get; init; }

    /// <summary>Archives in the repository afterwards.</summary>
    public int? Archives { get; init; }

    /// <summary>All archives together, as on disk.</summary>
    public long? TotalSize { get; init; }

    /// <summary>All archives together, compressed.</summary>
    public long? TotalCompressedSize { get; init; }

    /// <summary>What the repository takes up (each chunk once, compressed).</summary>
    public long? UniqueCompressedSize { get; init; }

    /// <summary>Why it failed, or the first warning.</summary>
    public string? Message { get; init; }
}
