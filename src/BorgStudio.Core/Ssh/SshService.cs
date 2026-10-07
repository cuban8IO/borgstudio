using System.Net.Sockets;
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
    /// <paramref name="command"/>. The password is used for this call only.
    /// </summary>
    /// <exception cref="SshOperationException">Connecting, logging in or the command failed.</exception>
    Task RunWithPasswordAsync(SshEndpoint endpoint, string password, SshHostKey trustedHostKey, string command,
        CancellationToken cancellationToken = default);
}

/// <summary><see cref="ISshService"/> with SSH.NET.</summary>
public sealed class SshNetService : ISshService
{
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
        CancellationToken cancellationToken = default)
    {
        var keyboardInteractive = new KeyboardInteractiveAuthenticationMethod(endpoint.User);
        keyboardInteractive.AuthenticationPrompt += (_, e) =>
        {
            foreach (var prompt in e.Prompts)
                prompt.Response = password;
        };
        var info = new ConnectionInfo(endpoint.Host, endpoint.Port, endpoint.User,
            new PasswordAuthenticationMethod(endpoint.User, password), keyboardInteractive) { Timeout = ConnectTimeout };

        using var client = new SshClient(info);
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

        using var sshCommand = client.CreateCommand(command);
        await sshCommand.ExecuteAsync(cancellationToken);
        client.Disconnect();
        if (sshCommand.ExitStatus != 0)
            throw new SshOperationException(SshFailureKind.CommandFailed, sshCommand.Error.Trim());
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

/// <summary>Trust on first use with confirmation: new host keys must be confirmed, changed ones are refused.</summary>
public sealed class SshHostTrust(ISshService ssh, KnownHostsFile knownHosts)
{
    public KnownHostsFile KnownHosts { get; } = knownHosts;

    public bool IsKnown(string host, int port) => KnownHosts.Find(host, port).Count > 0;

    /// <summary>Fetches the host key and checks it against known_hosts; asks <paramref name="confirm"/> for new ones.</summary>
    /// <exception cref="SshOperationException">The server could not be reached.</exception>
    public async Task<(HostTrust Trust, SshHostKey Key)> EnsureTrustedAsync(
        string host, int port, Func<SshHostKey, Task<bool>> confirm, CancellationToken cancellationToken = default)
    {
        var presented = await ssh.GetHostKeyAsync(host, port, cancellationToken);
        var known = KnownHosts.Find(host, port);

        if (known.Any(key => key.SameAs(presented)))
            return (HostTrust.Trusted, presented);
        if (known.Any(key => key.Type == presented.Type))
            return (HostTrust.Changed, presented);
        if (!await confirm(presented))
            return (HostTrust.Rejected, presented);

        KnownHosts.Add(host, port, presented);
        return (HostTrust.Trusted, presented);
    }
}
