using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using BorgStudio.Core.Borg;
using BorgStudio.Core.Plugins;
using BorgStudio.Core.Repositories;
using BorgStudio.Plugins;

namespace BorgStudio.Core.Storage;

/// <summary>
/// Measures how full a repository's storage is: the local disk; on a server <c>df</c> over SSH (only possible with a key
/// that is not restricted to <c>borg serve</c>); otherwise the repository's size from borg against the capacity entered
/// for it.
/// </summary>
/// <param name="plugins">Where the repository's provider is found – it may know the account's quota (<see cref="IStorageQuotaSource"/>).</param>
public sealed partial class StorageProbe(BorgClient borg, TimeProvider? time = null, PluginCatalog? plugins = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>Asks the disk directly (tests replace it).</summary>
    internal Func<string, (long Total, long Free)?> DiskSpace { get; init; } = Disk.Space;

    /// <summary>The usage now; <c>null</c> if nothing could be found out (e.g. the server is not reachable).</summary>
    public async Task<StorageUsage?> MeasureAsync(RepositoryConfig repository, BorgInstallation installation, RepositoryLocation location,
        string? passphrase, SshAccess? ssh, CancellationToken cancellationToken = default) =>
        await MeasureDirectAsync(repository, installation, location, ssh, cancellationToken)
        ?? (await MeasureWithBorgAsync(repository, installation, location, passphrase, ssh, cancellationToken)).Value;

    /// <summary>The disk, or <c>df</c> on the server – no passphrase needed; <c>null</c> if neither can be asked.</summary>
    public async Task<StorageUsage?> MeasureDirectAsync(RepositoryConfig repository, BorgInstallation installation, RepositoryLocation location,
        SshAccess? ssh, CancellationToken cancellationToken = default)
    {
        if (await QuotaAsync(repository, installation, location, ssh, cancellationToken) is { } quota)
            return new StorageUsage
            {
                Measured = _time.GetUtcNow(), Source = StorageSource.Server, Total = quota.TotalBytes,
                Free = Math.Max(0, quota.TotalBytes - quota.UsedBytes),
            };
        if (location.Ssh is null)
        {
            if (DiskSpace(location.Url) is { } disk)
                return new StorageUsage { Measured = _time.GetUtcNow(), Source = StorageSource.Disk, Total = disk.Total, Free = disk.Free };
        }
        else if (ssh is not null && !repository.SshKeyRestricted
                 && await ServerSpaceAsync(installation, location, location.Ssh, ssh, cancellationToken) is { } server)
        {
            return new StorageUsage { Measured = _time.GetUtcNow(), Source = StorageSource.Server, Total = server.Total, Free = server.Free };
        }
        return null;
    }

    /// <summary>The repository's size from borg, against the capacity entered for it (if any).</summary>
    public async Task<BorgResult<StorageUsage>> MeasureWithBorgAsync(RepositoryConfig repository, BorgInstallation installation,
        RepositoryLocation location, string? passphrase, SshAccess? ssh, CancellationToken cancellationToken = default)
    {
        var info = await borg.InfoAsync(installation, location, passphrase, ssh, cancellationToken);
        if (!info.Succeeded)
            return BorgResult<StorageUsage>.Failure(info.Error!);
        var stats = info.Value!.Stats;
        if (info.Value.UniqueCompressedSize is not { } size)
            return BorgResult<StorageUsage>.Failure(new BorgError(BorgErrorKind.Other, "borg info reported no repository size."));
        return BorgResult<StorageUsage>.Success(repository.StorageCapacity is { } capacity and > 0
            ? new StorageUsage
            {
                Measured = _time.GetUtcNow(),
                Source = StorageSource.Capacity,
                Total = capacity,
                Free = Math.Max(0, capacity - size),
                RepositorySize = size,
                Repository = stats,
            }
            : new StorageUsage { Measured = _time.GetUtcNow(), Source = StorageSource.RepositoryOnly, RepositorySize = size, Repository = stats });
    }

    /// <summary>The quota the provider knows; <c>null</c> if it knows none (or failed).</summary>
    private async Task<StorageQuota?> QuotaAsync(RepositoryConfig repository, BorgInstallation installation, RepositoryLocation location,
        SshAccess? ssh, CancellationToken cancellationToken)
    {
        if (plugins?.FindRepositoryProvider(repository.ProviderId) is not IStorageQuotaSource source)
            return null;
        Func<string, CancellationToken, Task<string?>>? run = null;
        if (location.Ssh is not null && ssh is not null && !repository.SshKeyRestricted)
            run = async (command, token) =>
                await borg.RunSshCommandAsync(installation, location, ssh, command, token) is { ExitCode: 0 } result ? result.StandardOutput : null;
        try
        {
            return await source.GetQuotaAsync(new StorageQuotaRequest(repository.ProviderValues) { RunCommandAsync = run }, cancellationToken)
                is { TotalBytes: > 0 } quota ? quota : null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A plugin's failure: measured otherwise.
            return null;
        }
    }

    private async Task<(long Total, long Free)?> ServerSpaceAsync(BorgInstallation installation, RepositoryLocation location,
        SshEndpoint endpoint, SshAccess ssh, CancellationToken cancellationToken)
    {
        var path = endpoint.RepositoryPath.StartsWith("~/", StringComparison.Ordinal) ? endpoint.RepositoryPath[2..] : endpoint.RepositoryPath;
        if (path.Length == 0)
            path = ".";
        // Restricted shells of storage services may not understand quotes: only where needed.
        var argument = PlainPath().IsMatch(path) ? path : $"'{path.Replace("'", "'\\''")}'";
        foreach (var command in new[] { $"df -Pk {argument}", $"df {argument}" })
        {
            var result = await borg.RunSshCommandAsync(installation, location, ssh, command, cancellationToken);
            if (result is { ExitCode: 0 } && ParseDf(result.StandardOutput) is { } space)
                return space;
        }
        return null;
    }

    /// <summary>
    /// The last line of <c>df</c>: "filesystem blocks used available capacity% mount", in the block size its header names
    /// (1K-blocks, 1024-blocks, 512-blocks; 1K without header).
    /// </summary>
    internal static (long Total, long Free)? ParseDf(string output)
    {
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 0)
            return null;
        var blockSize = lines[0] is var header && BlockSize().Match(header) is { Success: true } match
            ? long.Parse(match.Groups["size"].Value, CultureInfo.InvariantCulture) * (match.Groups["k"].Success ? 1024 : 1)
            : 1024;

        var columns = lines[^1].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index + 3 < columns.Length; index++)
        {
            if (long.TryParse(columns[index], NumberStyles.None, CultureInfo.InvariantCulture, out var total)
                && long.TryParse(columns[index + 1], NumberStyles.None, CultureInfo.InvariantCulture, out _)
                && long.TryParse(columns[index + 2], NumberStyles.None, CultureInfo.InvariantCulture, out var available)
                && columns[index + 3].EndsWith('%')
                && total > 0)
                return (total * blockSize, available * blockSize);
        }
        return null;
    }

    [GeneratedRegex(@"^[A-Za-z0-9._/-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex PlainPath();

    [GeneratedRegex(@"(?<size>\d+)(?<k>K)?-blocks", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex BlockSize();

    /// <summary>Free space of the disk a local path is on.</summary>
    internal static class Disk
    {
        public static (long Total, long Free)? Space(string path)
        {
            try
            {
                // The repository folder may be gone (disk not connected): its nearest existing parent tells nothing either.
                if (!Directory.Exists(path))
                    return null;
                if (OperatingSystem.IsWindows())
                    return GetDiskFreeSpaceEx(Path.GetFullPath(path), out var available, out var total, out _) && total > 0
                        ? ((long)total, (long)available)
                        : null;
                var drive = new DriveInfo(Path.GetFullPath(path));
                return drive.TotalSize > 0 ? (drive.TotalSize, drive.AvailableFreeSpace) : null;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return null;
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetDiskFreeSpaceEx(string directory, out ulong freeForCaller, out ulong total, out ulong totalFree);
    }
}
