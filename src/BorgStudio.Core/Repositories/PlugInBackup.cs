namespace BorgStudio.Core.Repositories;

/// <summary>
/// Backing up when the repository's drive is attached (USB drives, BorgStudio in the background): after asking, all jobs of
/// the repository run one after the other.
/// </summary>
public sealed record PlugInBackup
{
    public const int DefaultMinimumHours = 12;

    public bool Enabled { get; init; }

    /// <summary>Not asked again while a job of the repository backed up successfully within this many hours.</summary>
    public int MinimumHours { get; init; } = DefaultMinimumHours;
}
