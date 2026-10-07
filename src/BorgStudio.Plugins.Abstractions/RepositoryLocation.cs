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
public sealed record SshEndpoint(string Host, int Port, string User, string RepositoryPath)
{
    /// <summary>How BorgStudio adds a login key to the server's <c>~/.ssh/authorized_keys</c>.</summary>
    public SshKeyInstallation KeyInstallation { get; init; } = SshKeyInstallation.Shell;

    /// <summary>
    /// Optional server command that installs a public key read from standard input, e.g. <c>install-ssh-key</c>
    /// on a Hetzner Storage Box. Used for keys that are not restricted to borg; restricted keys always go through
    /// <see cref="KeyInstallation"/>.
    /// </summary>
    public string? InstallKeyCommand { get; init; }

    /// <summary>
    /// Host key fingerprints the provider publishes for its servers (<c>SHA256:…</c>, as <c>ssh-keygen -l</c> shows
    /// them). A server presenting one of these keys is trusted without asking; any other key is only accepted after
    /// the user confirmed it despite a warning. Empty: the user confirms every new server.
    /// </summary>
    public IReadOnlyList<string> PublishedHostKeyFingerprints { get; init; } = [];
}

/// <summary>How a login key gets into <c>~/.ssh/authorized_keys</c> on an SSH server.</summary>
public enum SshKeyInstallation
{
    /// <summary>With a POSIX shell command – for servers with a regular shell.</summary>
    Shell,

    /// <summary>Over SFTP – for servers whose shell is restricted (no redirections), e.g. storage services.</summary>
    Sftp,
}
