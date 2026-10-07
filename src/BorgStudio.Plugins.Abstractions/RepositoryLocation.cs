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

    /// <summary>
    /// SSH access details for repositories reached over SSH; BorgStudio uses them to verify the server's host key
    /// and to set up the login key. <c>null</c> for local repositories.
    /// </summary>
    public SshEndpoint? Ssh { get; init; }
}

/// <summary>An SSH server holding a repository.</summary>
/// <param name="Host">Host name or IP address of the server.</param>
/// <param name="Port">SSH port, usually 22.</param>
/// <param name="User">Login name on the server.</param>
/// <param name="RepositoryPath">
/// The repository path as the server sees it: absolute (<c>/srv/borg/repo</c>) or relative to the user's
/// home directory (<c>backups/repo</c>).
/// </param>
public sealed record SshEndpoint(string Host, int Port, string User, string RepositoryPath);
