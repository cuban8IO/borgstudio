namespace BorgStudio.Core.Borg;

/// <summary>What went wrong in a borg command, as far as the UI needs to tell the user.</summary>
public enum BorgErrorKind
{
    PassphraseWrong,
    RepositoryNotFound,
    NotARepository,

    /// <summary>The target already holds a repository or other files.</summary>
    RepositoryExists,

    /// <summary>Another borg process holds the repository lock.</summary>
    Locked,

    ConnectionFailed,

    /// <summary>ssh refused the server: its host key does not match the confirmed one (or is unknown).</summary>
    SshHostKeyFailed,

    /// <summary>The server refused the SSH key.</summary>
    SshAuthenticationFailed,

    Timeout,

    /// <summary>borg (or wsl.exe) could not be started.</summary>
    NotStartable,

    /// <summary>Stopped on request (borg was interrupted).</summary>
    Cancelled,

    /// <summary>A path that cannot be handed to borg (e.g. a network share for borg inside WSL).</summary>
    UnsupportedPath,

    /// <summary>FUSE refused <c>allow_other</c>: <c>user_allow_other</c> is missing in /etc/fuse.conf.</summary>
    MountNotAllowed,

    /// <summary>borg cannot mount here (no FUSE, e.g. borg on Windows itself or without llfuse/pyfuse3).</summary>
    MountUnavailable,

    Other,
}

/// <param name="Message">borg's own message (English), shown as detail.</param>
public sealed record BorgError(BorgErrorKind Kind, string? Message = null);

public sealed record BorgResult<T>(T? Value, BorgError? Error)
{
    public bool Succeeded => Error is null;

    public static BorgResult<T> Success(T value) => new(value, null);

    public static BorgResult<T> Failure(BorgError error) => new(default, error);
}

/// <summary>Encryption modes offered when creating a repository (borg 1.x names).</summary>
public enum BorgEncryption
{
    /// <summary>Key stored in the repository, protected by the passphrase (borg's recommendation).</summary>
    RepokeyBlake2,

    /// <summary>Key stored only on this computer – it must be backed up separately.</summary>
    KeyfileBlake2,

    None,
}

/// <summary>From <c>borg info --json</c>.</summary>
/// <param name="EncryptionMode">e.g. "repokey-blake2", "keyfile-blake2", "none".</param>
public sealed record BorgRepositoryInfo(string Id, string EncryptionMode, DateTimeOffset? LastModified);

/// <summary>A progress update of <c>borg create</c> (sizes so far).</summary>
/// <param name="DeduplicatedSize">What actually has to go into the repository (new data, compressed).</param>
/// <param name="Path">The file borg is working on.</param>
public sealed record BorgCreateProgress(long OriginalSize, long CompressedSize, long DeduplicatedSize, long Files, string? Path, bool Finished = false);

/// <summary>The outcome of <c>borg create</c>.</summary>
/// <param name="Warnings">What borg warned about, e.g. files it could not read.</param>
public sealed record BorgCreateResult(string ArchiveName, long OriginalSize, long CompressedSize, long DeduplicatedSize, long Files,
    TimeSpan Duration, IReadOnlyList<string> Warnings);

/// <summary>An archive in a repository.</summary>
/// <param name="Duration">How long creating it took; <c>null</c> until details are loaded.</param>
/// <param name="Stats">Its sizes; <c>null</c> until details are loaded.</param>
public sealed record BorgArchive(string Name, string Id, DateTimeOffset Start, TimeSpan? Duration = null, BorgArchiveStats? Stats = null);

/// <param name="DeduplicatedSize">What only this archive needs in the repository (its share of the repository).</param>
public sealed record BorgArchiveStats(long OriginalSize, long CompressedSize, long DeduplicatedSize, long Files);
/// <summary>An archive as <c>borg prune --list</c> reports it: kept (and by which rule) or deleted.</summary>
/// <param name="Rule">borg's rule name ("daily", "weekly", "within", "secondly" for --keep-last, …); <c>null</c> for
/// deleted archives and kept checkpoints.</param>
/// <param name="RuleNumber">Which archive of the rule it is (1 = newest).</param>
public sealed record BorgPruneEntry(string Name, string Id, DateTimeOffset Start, bool Kept, string? Rule = null, int RuleNumber = 0)
{
    public bool IsCheckpoint => Kept && Rule is null;
}

/// <summary>The outcome of <c>borg prune</c> (or what it would do, with <c>--dry-run</c>), newest archive first.</summary>
public sealed record BorgPruneResult(IReadOnlyList<BorgPruneEntry> Archives)
{
    public IReadOnlyList<BorgPruneEntry> Pruned => [.. Archives.Where(archive => !archive.Kept)];
}

/// <summary>The outcome of <c>borg compact</c>.</summary>
/// <param name="FreedBytes">What it freed, as far as borg says (rounded); <c>null</c> if it didn't say.</param>
public sealed record BorgCompactResult(long? FreedBytes);

/// <summary>A progress update borg logs with <c>--progress --log-json</c>.</summary>
/// <param name="MessageId">What it is about, e.g. "extract", "prune", "repository.compact_segments", "archive.calc_stats".</param>
/// <param name="Fraction">0..1 as borg shows it.</param>
/// <param name="Info">Extra detail, e.g. the file being extracted.</param>
/// <param name="Current">How far in borg's unit (e.g. bytes when extracting), if it says.</param>
public sealed record BorgProgressPercent(string? MessageId, double Fraction, bool Finished, string? Info, double? Current = null, double? Total = null);

/// <summary>Progress of <c>borg extract</c>.</summary>
/// <param name="Preparing">borg is still working out the total size (it reads the archive's metadata for that).</param>
/// <param name="Path">The file being extracted, as borg stores it.</param>
public sealed record BorgExtractProgress(long Done, long Total, string? Path, bool Preparing = false);

/// <summary>The outcome of <c>borg extract</c>.</summary>
/// <param name="Warnings">What borg warned about, e.g. files it could not write.</param>
public sealed record BorgExtractResult(IReadOnlyList<string> Warnings);
