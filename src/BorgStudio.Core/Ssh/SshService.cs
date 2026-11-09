using System.Net.Sockets;
using System.Text;
using BorgStudio.Plugins;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace BorgStudio.Core.Ssh;

public enum SshFailureKind
{
    ConnectionFailed,
    AuthenticationFailed,

    /// <summary>The server presented a different host key than the trusted one.</summary>
    HostKeyChanged,

    CommandFailed,
}

public sealed class SshOperationException(SshFailureKind kind, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public SshFailureKind Kind { get; } = kind;
}

/// <summary>The few things BorgStudio does over SSH itself (borg uses the system's ssh client).</summary>
public interface ISshService
{
    /// <summary>The server's host key; no login happens.</summary>
    /// <exception cref="SshOperationException">The server could not be reached.</exception>
    Task<SshHostKey> GetHostKeyAsync(string host, int port, CancellationToken cancellationToken = default);

    /// <summary>
    /// Logs in with a password – but only if the server presents <paramref name="trustedHostKey"/> – and runs
    /// <paramref name="command"/>, optionally feeding it <paramref name="standardInput"/>. The password is used
    /// for this call only.
    /// </summary>
    /// <exception cref="SshOperationException">Connecting, logging in or the command failed.</exception>
    Task RunWithPasswordAsync(SshEndpoint endpoint, string password, SshHostKey trustedHostKey, string command,
        string? standardInput = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Logs in with a password like <see cref="RunWithPasswordAsync"/> and appends <paramref name="line"/> to
    /// <c>~/.ssh/authorized_keys</c> over SFTP (nothing happens if the line is already there).
    /// </summary>
    /// <exception cref="SshOperationException">Connecting, logging in or writing the file failed.</exception>
    Task AppendAuthorizedKeyAsync(SshEndpoint endpoint, string password, SshHostKey trustedHostKey, string line,
        CancellationToken cancellationToken = default);
}

/// <summary><see cref="ISshService"/> with SSH.NET.</summary>
public sealed class SshNetService : ISshService
{
    private const string SshDirectory = ".ssh";
    private const string AuthorizedKeysFile = ".ssh/authorized_keys";

    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(20);

    public async Task<SshHostKey> GetHostKeyAsync(string host, int port, CancellationToken cancellationToken = default)
    {
        SshHostKey? received = null;
        var info = new ConnectionInfo(host, port, "borgstudio", new NoneAuthenticationMethod("borgstudio")) { Timeout = ConnectTimeout };
        using var client = new SshClient(info);
        client.HostKeyReceived += (_, e) =>
        {
            received = new SshHostKey(e.HostKey);
            e.CanTrust = false; // We only wanted the key; refusing it ends the handshake.
        };

        try
        {
            await client.ConnectAsync(cancellationToken);
        }
        catch (Exception exception) when (received is not null && exception is not OperationCanceledException)
        {
            // Expected: the connection fails because we refused the key on purpose.
        }
        catch (Exception exception) when (IsConnectionProblem(exception))
        {
            throw new SshOperationException(SshFailureKind.ConnectionFailed, exception.Message, exception);
        }

        return received ?? throw new SshOperationException(SshFailureKind.ConnectionFailed, $"No host key received from {host}:{port}.");
    }

    public async Task RunWithPasswordAsync(SshEndpoint endpoint, string password, SshHostKey trustedHostKey, string command,
        string? standardInput = null, CancellationToken cancellationToken = default)
    {
        using var client = new SshClient(PasswordLogin(endpoint, password));
        await ConnectAsync(client, trustedHostKey, cancellationToken);

        using var sshCommand = client.CreateCommand(command);
        var execution = sshCommand.ExecuteAsync(cancellationToken);
        if (standardInput is not null)
        {
            // Closing the stream sends end-of-file, so commands reading their input (install-ssh-key) finish.
            await using var input = sshCommand.CreateInputStream();
            await input.WriteAsync(new UTF8Encoding(false).GetBytes(standardInput), cancellationToken);
        }
        await execution;
        client.Disconnect();

        if (sshCommand.ExitStatus != 0)
        {
            var error = sshCommand.Error.Trim();
            throw new SshOperationException(SshFailureKind.CommandFailed, error.Length > 0 ? error : sshCommand.Result.Trim());
        }
    }

    public async Task AppendAuthorizedKeyAsync(SshEndpoint endpoint, string password, SshHostKey trustedHostKey, string line,
        CancellationToken cancellationToken = default)
    {
        if (line.Contains('\n') || line.Contains('\r'))
            throw new ArgumentException("authorized_keys line must not contain line breaks.", nameof(line));

        using var client = new SftpClient(PasswordLogin(endpoint, password));
        await ConnectAsync(client, trustedHostKey, cancellationToken);

        try
        {
            // Relative paths start in the login directory, i.e. the user's home.
            if (!await client.ExistsAsync(SshDirectory, cancellationToken))
            {
                await client.CreateDirectoryAsync(SshDirectory, cancellationToken);
                TryChangePermissions(client, SshDirectory, 700);
            }

            var existing = await client.ExistsAsync(AuthorizedKeysFile, cancellationToken)
                ? client.ReadAllText(AuthorizedKeysFile)
                : null;
            if (existing is not null && existing.Split('\n').Any(entry => entry.TrimEnd('\r') == line))
                return;

            // Append only: whatever else is in the file stays exactly as it is.
            var separator = existing is { Length: > 0 } && !existing.EndsWith('\n') ? "\n" : "";
            client.AppendAllText(AuthorizedKeysFile, separator + line + "\n", new UTF8Encoding(false));
            if (existing is null)
                TryChangePermissions(client, AuthorizedKeysFile, 600);
        }
        catch (Exception exception) when (exception is SshException or IOException)
        {
            throw new SshOperationException(SshFailureKind.CommandFailed, exception.Message, exception);
        }
        finally
        {
            client.Disconnect();
        }
    }

    /// <summary>Password and keyboard-interactive login (servers offer either) with the same password.</summary>
    private static ConnectionInfo PasswordLogin(SshEndpoint endpoint, string password)
    {
        var keyboardInteractive = new KeyboardInteractiveAuthenticationMethod(endpoint.User);
        keyboardInteractive.AuthenticationPrompt += (_, e) =>
        {
            foreach (var prompt in e.Prompts)
                prompt.Response = password;
        };
        return new ConnectionInfo(endpoint.Host, endpoint.Port, endpoint.User,
            new PasswordAuthenticationMethod(endpoint.User, password), keyboardInteractive) { Timeout = ConnectTimeout };
    }

    /// <summary>Connects – but only to a server presenting <paramref name="trustedHostKey"/>.</summary>
    private static async Task ConnectAsync(BaseClient client, SshHostKey trustedHostKey, CancellationToken cancellationToken)
    {
        var hostKeyChanged = false;
        client.HostKeyReceived += (_, e) =>
        {
            e.CanTrust = new SshHostKey(e.HostKey).SameAs(trustedHostKey);
            hostKeyChanged = !e.CanTrust;
        };

        try
        {
            await client.ConnectAsync(cancellationToken);
        }
        catch (SshAuthenticationException exception)
        {
            throw new SshOperationException(SshFailureKind.AuthenticationFailed, exception.Message, exception);
        }
        catch (Exception exception) when (hostKeyChanged)
        {
            throw new SshOperationException(SshFailureKind.HostKeyChanged, exception.Message, exception);
        }
        catch (Exception exception) when (IsConnectionProblem(exception))
        {
            throw new SshOperationException(SshFailureKind.ConnectionFailed, exception.Message, exception);
        }
    }

    /// <summary>
    /// Private permissions where the server allows it (<paramref name="octalMode"/> as SSH.NET takes it: 700 means 0700).
    /// OpenSSH also accepts the defaults, which aren't writable for others.
    /// </summary>
    private static void TryChangePermissions(SftpClient client, string path, short octalMode)
    {
        try
        {
            client.ChangePermissions(path, octalMode);
        }
        catch (SshException)
        {
            // Some storage services don't support chmod; the file still works.
        }
    }

    private static bool IsConnectionProblem(Exception exception) =>
        exception is SocketException or SshConnectionException or SshOperationTimeoutException or ProxyException
            or SshException or TimeoutException;
}

public enum HostTrust
{
    Trusted,

    /// <summary>The user did not confirm the new host key.</summary>
    Rejected,

    /// <summary>The server presents a different key than the one stored – possibly an attack. Never accepted automatically.</summary>
    Changed,
}

/// <summary>
/// Trust on first use with confirmation: new host keys must be confirmed, changed ones are refused –
/// unless the provider published the key's fingerprint.
/// </summary>
public sealed class SshHostTrust(ISshService ssh, KnownHostsFile knownHosts)
{
    public KnownHostsFile KnownHosts { get; } = knownHosts;

    public bool IsKnown(string host, int port) => KnownHosts.Find(host, port).Count > 0;

    /// <summary>Fetches the host key and checks it against known_hosts; asks <paramref name="confirm"/> for new ones.</summary>
    /// <exception cref="SshOperationException">The server could not be reached.</exception>
    public Task<(HostTrust Trust, SshHostKey Key)> EnsureTrustedAsync(
        string host, int port, Func<SshHostKey, Task<bool>> confirm, CancellationToken cancellationToken = default) =>
        EnsureTrustedAsync(host, port, [], (key, _) => confirm(key), cancellationToken);

    /// <summary>Fetches the host key and checks it against known_hosts and the provider's published fingerprints.</summary>
    /// <param name="publishedFingerprints">
    /// Fingerprints the provider publishes for its servers: a key matching one of them is stored without asking,
    /// even if a different key was stored before (the provider changed its keys).
    /// </param>
    /// <param name="confirm">
    /// Asks the user about a new key. The flag is <c>true</c> when the provider publishes fingerprints and this key
    /// matches none of them – a reason to warn.
    /// </param>
    /// <exception cref="SshOperationException">The server could not be reached.</exception>
    public async Task<(HostTrust Trust, SshHostKey Key)> EnsureTrustedAsync(
        string host, int port, IReadOnlyCollection<string> publishedFingerprints, Func<SshHostKey, bool, Task<bool>> confirm,
        CancellationToken cancellationToken = default)
    {
        var presented = await ssh.GetHostKeyAsync(host, port, cancellationToken);
        var known = KnownHosts.Find(host, port);

        if (known.Any(key => key.SameAs(presented)))
            return (HostTrust.Trusted, presented);

        if (publishedFingerprints.Contains(presented.Fingerprint, StringComparer.Ordinal))
        {
            KnownHosts.Add(host, port, presented);
            return (HostTrust.Trusted, presented);
        }

        if (known.Any(key => key.Type == presented.Type))
            return (HostTrust.Changed, presented);
        if (!await confirm(presented, publishedFingerprints.Count > 0))
            return (HostTrust.Rejected, presented);

        KnownHosts.Add(host, port, presented);
        return (HostTrust.Trusted, presented);
    }
}
