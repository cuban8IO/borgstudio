namespace BorgStudio.Core.Repositories;

/// <summary>How much a repository's backups may take of the connection and of this computer.</summary>
public sealed record TransferLimits
{
    /// <summary>Upload to a server at most this fast (MB/s); <c>null</c>: unlimited. borg can only limit uploads to a server.</summary>
    public double? UploadMegabytesPerSecond { get; init; }

    /// <summary>The upload limit applies only to runs starting between <see cref="From"/> and <see cref="To"/>.</summary>
    public bool OnlyBetween { get; init; }

    public TimeOnly From { get; init; } = new(8, 0);

    public TimeOnly To { get; init; } = new(18, 0);

    /// <summary>borg runs with low priority (nice): other programs go first with the processor and the disk.</summary>
    public bool LowPriority { get; init; }

    /// <summary>What applies to a run starting at <paramref name="now"/> (<paramref name="server"/>: the repository is on a server).</summary>
    public BorgThrottle ThrottleAt(DateTime now, bool server)
    {
        int? upload = server && UploadMegabytesPerSecond is > 0 and var megabytes && (!OnlyBetween || IsWithin(TimeOnly.FromDateTime(now)))
            ? Math.Max(1, (int)Math.Round(megabytes * 1024))
            : null;
        return new BorgThrottle(upload, LowPriority);
    }

    /// <summary>Within the window; one that ends before it starts runs over midnight (e.g. 22:00–06:00).</summary>
    private bool IsWithin(TimeOnly time) => From <= To ? time >= From && time < To : time >= From || time < To;
}

/// <summary>What a borg call is held back by.</summary>
/// <param name="UploadKibibytesPerSecond">borg's <c>--upload-ratelimit</c>; <c>null</c>: unlimited.</param>
/// <param name="LowPriority">borg runs under <c>nice</c>.</param>
public sealed record BorgThrottle(int? UploadKibibytesPerSecond, bool LowPriority)
{
    public static readonly BorgThrottle None = new(null, false);
}
