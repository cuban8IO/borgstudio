using BorgStudio.Plugins;

namespace BorgStudio.Core.Borg;

public sealed partial class BorgClient
{
    private static readonly TimeSpan ArchiveChangeTimeout = TimeSpan.FromMinutes(30);

    /// <summary><c>borg rename</c>: gives <paramref name="archive"/> the name <paramref name="newName"/> (which must not exist yet).</summary>
    public async Task<BorgResult<bool>> RenameArchiveAsync(BorgInstallation borg, RepositoryLocation location, string archive, string newName,
        string? passphrase, SshAccess? ssh = null, CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(borg, location, ssh, ["rename"], [], [newName], passphrase, ArchiveChangeTimeout, cancellationToken, archive);
        return result.Succeeded ? BorgResult<bool>.Success(true) : BorgResult<bool>.Failure(result.Error!);
    }

    /// <summary>
    /// <c>borg delete</c>: deletes the archives (at once). Their space is freed only by <see cref="CompactAsync"/>.
    /// </summary>
    public async Task<BorgResult<bool>> DeleteArchivesAsync(BorgInstallation borg, RepositoryLocation location, IReadOnlyList<string> archives,
        string? passphrase, SshAccess? ssh = null, CancellationToken cancellationToken = default)
    {
        if (archives.Count == 0)
            return BorgResult<bool>.Success(true);
        var result = await RunAsync(borg, location, ssh, ["delete"], [], [.. archives.Skip(1)], passphrase, CreateTimeout, cancellationToken,
            archives[0]);
        return result.Succeeded ? BorgResult<bool>.Success(true) : BorgResult<bool>.Failure(result.Error!);
    }

    /// <summary>
    /// <c>borg recreate --comment</c>: sets the archive's comment (empty: none). Only its metadata is written anew, which is
    /// quick – but the archive gets a new id.
    /// </summary>
    public async Task<BorgResult<bool>> SetArchiveCommentAsync(BorgInstallation borg, RepositoryLocation location, string archive, string comment,
        string? passphrase, SshAccess? ssh = null, CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(borg, location, ssh, ["recreate"], ["--comment", comment], [], passphrase, ArchiveChangeTimeout,
            cancellationToken, archive);
        return result.Succeeded ? BorgResult<bool>.Success(true) : BorgResult<bool>.Failure(result.Error!);
    }
}
