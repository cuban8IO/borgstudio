namespace BorgStudio.Plugins;

/// <summary>
/// Optional for an <see cref="IRepositoryProvider"/> whose service tells how much space an account has and uses (a quota).
/// BorgStudio asks it before measuring the disk on the server; the answer shows in the storage overview and drives the
/// "almost full" notification.
/// </summary>
public interface IStorageQuotaSource
{
    /// <summary>The account's quota; <c>null</c> if it cannot be found out now (BorgStudio then measures otherwise).</summary>
    Task<StorageQuota?> GetQuotaAsync(StorageQuotaRequest request, CancellationToken cancellationToken);
}

/// <summary>What a quota is asked with.</summary>
/// <param name="Values">The values the user entered (keyed by <see cref="ProviderField.Key"/>).</param>
public sealed record StorageQuotaRequest(IReadOnlyDictionary<string, string> Values)
{
    /// <summary>
    /// Runs a command on the repository's SSH server with its login key and returns standard output; <c>null</c> if it
    /// could not run or failed – always so for keys restricted to <c>borg serve</c>. <c>null</c> itself for repositories
    /// not reached over SSH.
    /// </summary>
    public Func<string, CancellationToken, Task<string?>>? RunCommandAsync { get; init; }

    /// <summary>The values of <see cref="ProviderFieldKind.Secret"/> fields that are available now (e.g. an API token).</summary>
    public IReadOnlyDictionary<string, string> Secrets { get; init; } = new Dictionary<string, string>();
}

/// <summary>An account's space.</summary>
/// <param name="UsedBytes">Taken up now.</param>
/// <param name="TotalBytes">What the account may take up.</param>
public sealed record StorageQuota(long UsedBytes, long TotalBytes);
