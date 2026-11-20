using System.Text.Json;
using BorgStudio.Plugins;

namespace BorgStudio.Core.Borg;

/// <summary>Who holds a repository's lock, as far as its lock files tell.</summary>
/// <param name="Host">The computer's name (borg's host id without its node id).</param>
/// <param name="ProcessId">borg's process id on that computer.</param>
/// <param name="Exclusive">An exclusive lock (writing); otherwise a shared one (reading).</param>
public sealed record LockHolder(string Host, int ProcessId, bool Exclusive);

/// <summary>A repository's lock: who holds it and since when.</summary>
public sealed record RepositoryLockInfo(IReadOnlyList<LockHolder> Holders, DateTimeOffset? Since)
{
    /// <summary>
    /// Reads the lock files of a repository in a local folder (lock.roster: <c>{"exclusive": [["host@node", pid, thread]],
    /// "shared": [...]}</c>); <c>null</c> if there is no lock or it cannot be read (e.g. a repository on a server).
    /// </summary>
    public static RepositoryLockInfo? Read(string repositoryFolder)
    {
        var roster = Path.Combine(repositoryFolder, "lock.roster");
        try
        {
            if (!File.Exists(roster))
                return null;
            using var document = JsonDocument.Parse(File.ReadAllText(roster));
            var holders = new List<LockHolder>();
            foreach (var (name, exclusive) in new[] { ("exclusive", true), ("shared", false) })
            {
                if (!document.RootElement.TryGetProperty(name, out var entries) || entries.ValueKind != JsonValueKind.Array)
                    continue;
                foreach (var entry in entries.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.Array || entry.GetArrayLength() < 2 || entry[0].GetString() is not { } host)
                        continue;
                    var at = host.LastIndexOf('@');
                    holders.Add(new LockHolder(at > 0 ? host[..at] : host, entry[1].TryGetInt32(out var pid) ? pid : 0, exclusive));
                }
            }
            return holders.Count == 0 ? null : new RepositoryLockInfo(holders, File.GetLastWriteTime(roster));
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether <paramref name="host"/> is this computer (borg in WSL reports the same name, possibly with its domain).
    /// borg clears a lock of this computer whose process is gone by itself – so one that stays belongs to a running process.
    /// </summary>
    public static bool IsThisComputer(string host) =>
        string.Equals(host.Split('.')[0], Environment.MachineName.Split('.')[0], StringComparison.OrdinalIgnoreCase);
}

public sealed partial class BorgClient
{
    private static readonly TimeSpan BreakLockTimeout = TimeSpan.FromMinutes(2);

    /// <summary>
    /// <c>borg break-lock</c>: removes the repository's lock (and the local cache's), whoever holds it. Only for a lock
    /// left behind (a crash, a connection that broke off) – a borg that still works on the repository could damage it.
    /// Needs no passphrase.
    /// </summary>
    public async Task<BorgResult<bool>> BreakLockAsync(BorgInstallation borg, RepositoryLocation location, SshAccess? ssh = null,
        CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(borg, location, ssh, ["break-lock"], [], [], null, BreakLockTimeout, cancellationToken);
        return result.Succeeded ? BorgResult<bool>.Success(true) : BorgResult<bool>.Failure(result.Error!);
    }
}
