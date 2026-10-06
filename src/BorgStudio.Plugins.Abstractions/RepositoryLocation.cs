namespace BorgStudio.Plugins;

/// <summary>Where a borg repository is and what borg needs to reach it.</summary>
/// <param name="Url">
/// Repository as borg expects it: an absolute path for local repositories,
/// <c>ssh://user@host:port/path</c> for remote ones.
/// </param>
public sealed record RepositoryLocation(string Url)
{
    /// <summary>Extra arguments for every borg command on this repository, e.g. <c>--remote-path=borg-1.4</c>.</summary>
    public IReadOnlyList<string> BorgArguments { get; init; } = [];
}
