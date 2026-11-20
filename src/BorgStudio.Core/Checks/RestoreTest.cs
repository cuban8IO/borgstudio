using System.Text.Json;
using System.Text.Json.Serialization;
using BorgStudio.Core.Borg;
using BorgStudio.Core.Repositories;
using BorgStudio.Plugins;

namespace BorgStudio.Core.Checks;

/// <summary>How a job's newest archive is restored on trial.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<RestoreTestMode>))]
public enum RestoreTestMode
{
    Off,

    /// <summary>A few files picked at random are restored into a temporary folder: they must come back whole.</summary>
    Read,

    /// <summary>As <see cref="Read"/>; files unchanged at the source since are also compared byte by byte.</summary>
    Compare,

    /// <summary>The whole archive is read and decrypted (<c>borg extract --dry-run</c>), nothing is written.</summary>
    WholeArchive,
}

/// <summary>When a source file counts as unchanged since the backup (then it is compared byte by byte).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<RestoreTestComparison>))]
public enum RestoreTestComparison
{
    /// <summary>Same size and same modification time.</summary>
    SizeAndTime,

    /// <summary>Same size (finds more to compare, but also reports files changed since at the same size).</summary>
    SizeOnly,
}

/// <summary>App-wide settings of the restore tests (each job chooses its <see cref="RestoreTestMode"/>).</summary>
public sealed record RestoreTestSettings
{
    /// <summary>Restore tests run at all (with the automatic checks, for the jobs that have one).</summary>
    public bool Enabled { get; init; }

    /// <summary>A sample takes at most this much altogether.</summary>
    public long MaxTotalBytes { get; init; } = 1024L * 1024 * 1024;

    /// <summary>Larger files are not picked (e.g. a disk image).</summary>
    public long MaxFileBytes { get; init; } = 100L * 1024 * 1024;

    public RestoreTestComparison Comparison { get; init; } = RestoreTestComparison.SizeAndTime;
}

[JsonConverter(typeof(JsonStringEnumConverter<RestoreTestOutcome>))]
public enum RestoreTestOutcome
{
    /// <summary>Everything came back (and what could be compared was identical).</summary>
    Ok,

    /// <summary>A file did not come back whole, differed from its unchanged source, or borg could not read the archive.</summary>
    Failed,

    /// <summary>The test could not run (no backup yet, no connection, …) – it tells nothing about the archive.</summary>
    NotRun,

    Cancelled,
}

[JsonConverter(typeof(JsonStringEnumConverter<RestoredFileResult>))]
public enum RestoredFileResult
{
    /// <summary>Restored and byte for byte the same as the unchanged source.</summary>
    Identical,

    /// <summary>Restored whole; the source has changed since (or is gone), so it was not compared.</summary>
    Restored,

    /// <summary>Restored, but different from the source that did not change.</summary>
    Different,

    /// <summary>Not restored, or not at its size.</summary>
    Missing,
}

/// <param name="Path">The path in the archive.</param>
public sealed record RestoredFile(string Path, long Size, RestoredFileResult Result);

/// <summary>One restore test of a job.</summary>
public sealed record RestoreTestRecord(DateTimeOffset Started, TimeSpan Duration, RestoreTestMode Mode, RestoreTestOutcome Outcome)
{
    public string? Archive { get; init; }

    public IReadOnlyList<RestoredFile> Files { get; init; } = [];

    /// <summary>Why it could not run or failed as a whole (borg's error).</summary>
    public string? Error { get; init; }

    /// <summary>Run with an automatic check (not with the button).</summary>
    public bool Automatic { get; init; }

    [JsonIgnore]
    public int Compared => Files.Count(file => file.Result is RestoredFileResult.Identical or RestoredFileResult.Different);

    [JsonIgnore]
    public IEnumerable<RestoredFile> Problems => Files.Where(file => file.Result is RestoredFileResult.Different or RestoredFileResult.Missing);

    [JsonIgnore]
    public long Bytes => Files.Sum(file => file.Size);
}

/// <summary>The restore tests of each job, newest first, in restore-tests/&lt;job&gt;.json (the last <see cref="MaximumRecords"/>).</summary>
public sealed class RestoreTestStore(string directory)
{
    public const int MaximumRecords = 50;

    private static readonly object Gate = new();

    public static RestoreTestStore ForDataDirectory(string dataDirectory) => new(Path.Combine(dataDirectory, "restore-tests"));

    public IReadOnlyList<RestoreTestRecord> Get(Guid jobId)
    {
        lock (Gate)
            return Load(jobId);
    }

    public void Add(Guid jobId, RestoreTestRecord record)
    {
        lock (Gate)
        {
            List<RestoreTestRecord> records = [record, .. Load(jobId)];
            try
            {
                AtomicFile.WriteAllText(FilePath(jobId),
                    JsonSerializer.Serialize(records.OrderByDescending(entry => entry.Started).Take(MaximumRecords), RepositoryStore.JsonOptions));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    public void Remove(Guid jobId)
    {
        lock (Gate)
        {
            try
            {
                File.Delete(FilePath(jobId));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private string FilePath(Guid jobId) => Path.Combine(directory, $"{jobId:N}.json");

    private List<RestoreTestRecord> Load(Guid jobId)
    {
        var path = FilePath(jobId);
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<List<RestoreTestRecord>>(File.ReadAllText(path), RepositoryStore.JsonOptions) ?? []
                : [];
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}

/// <summary>Picks the files of a sample: at random, regular files only, within the size limits.</summary>
public sealed class RestoreSample(int count, long maxFileBytes, long maxTotalBytes, Random? random = null)
{
    private readonly Random _random = random ?? Random.Shared;

    // Reservoir sampling: every eligible file has the same chance, whatever the archive's size. A few more than wanted are
    // kept, so files that would go over the total limit can be left out.
    private readonly List<BorgItem> _reservoir = [];
    private long _seen;

    private int Capacity => Math.Max(1, count) * 3;

    public void Offer(BorgItem item)
    {
        if (item.Kind != ArchiveEntryKind.File || item.Size <= 0 || item.Size > maxFileBytes)
            return;
        _seen++;
        if (_reservoir.Count < Capacity)
        {
            _reservoir.Add(item);
            return;
        }
        var slot = _random.NextInt64(_seen);
        if (slot < Capacity)
            _reservoir[(int)slot] = item;
    }

    /// <summary>Up to <c>count</c> files, together at most <c>maxTotalBytes</c>.</summary>
    public IReadOnlyList<BorgItem> Pick()
    {
        var picked = new List<BorgItem>();
        long total = 0;
        foreach (var item in _reservoir.OrderBy(_ => _random.Next()))
        {
            if (picked.Count >= count)
                break;
            if (total + item.Size > maxTotalBytes)
                continue;
            picked.Add(item);
            total += item.Size;
        }
        return picked;
    }
}

/// <summary>Restores a job's archive on trial (see <see cref="RestoreTestMode"/>) and judges what came back.</summary>
/// <param name="describe">Turns a borg error into the text kept with the test (localized by the app).</param>
public sealed class RestoreTester(BorgClient client, Func<BorgError, string>? describe = null)
{
    /// <summary>How far apart modification times may be and still count as the same (FAT keeps two seconds).</summary>
    public static readonly TimeSpan TimeTolerance = TimeSpan.FromSeconds(2);

    /// <param name="temporaryRoot">Where the sample is restored to (a folder of its own below it, removed afterwards).</param>
    /// <param name="progress">0..1 while restoring.</param>
    public async Task<RestoreTestRecord> RunAsync(BorgInstallation borg, RepositoryLocation location, string archive, string? passphrase,
        SshAccess? ssh, RestoreTestMode mode, int files, RestoreTestSettings settings, string temporaryRoot,
        IProgress<double>? progress = null, Random? random = null, CancellationToken cancellationToken = default, BorgThrottle? throttle = null)
    {
        var started = DateTimeOffset.Now;
        RestoreTestRecord Record(RestoreTestOutcome outcome) => new(started, DateTimeOffset.Now - started, mode, outcome) { Archive = archive };
        var extractProgress = progress is null ? null : new Relay(update =>
        {
            if (update is { Total: > 0 } known)
                progress.Report((double)known.Done / known.Total);
        });

        if (mode == RestoreTestMode.WholeArchive)
        {
            var read = await client.ExtractAsync(borg, location, archive, [], temporaryRoot, 0, passphrase, ssh, extractProgress, cancellationToken,
                dryRun: true, throttle: throttle);
            return read switch
            {
                { Value.Warnings.Count: > 0 } => Record(RestoreTestOutcome.Failed) with { Error = string.Join("\n", read.Value.Warnings.Take(20)) },
                { Succeeded: true } => Record(RestoreTestOutcome.Ok),
                _ => Failure(Record, read.Error!),
            };
        }

        var sample = new RestoreSample(files, settings.MaxFileBytes, settings.MaxTotalBytes, random);
        var listed = await client.ListItemsAsync(borg, location, archive, passphrase, sample.Offer, ssh, cancellationToken);
        if (!listed.Succeeded)
            return Failure(Record, listed.Error!);
        var picked = sample.Pick();
        if (picked.Count == 0)
            return Record(RestoreTestOutcome.NotRun) with { Error = "No files within the size limits." };

        var target = Path.Combine(temporaryRoot, "borgstudio-restore-test-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(target);
        try
        {
            var restored = await client.ExtractAsync(borg, location, archive, [.. picked.Select(item => item.Path)], target, 0, passphrase, ssh,
                extractProgress, cancellationToken, throttle: throttle);
            if (!restored.Succeeded && restored.Error!.Kind != BorgErrorKind.Other)
                return Failure(Record, restored.Error);

            var results = picked.Select(item => Judge(borg, item, target, mode, settings.Comparison)).ToList();
            var failed = !restored.Succeeded || results.Any(file => file.Result is RestoredFileResult.Different or RestoredFileResult.Missing);
            return Record(failed ? RestoreTestOutcome.Failed : RestoreTestOutcome.Ok) with
            {
                Files = results,
                Error = restored.Succeeded ? null : restored.Error!.Message,
            };
        }
        finally
        {
            try
            {
                Directory.Delete(target, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Left for the system's clean-up of temporary files.
            }
        }
    }

    /// <summary>Whether a restored file came back whole, and – if its source is unchanged – identical.</summary>
    internal static RestoredFile Judge(BorgInstallation borg, BorgItem item, string target, RestoreTestMode mode, RestoreTestComparison comparison)
    {
        var restored = new FileInfo(Path.Combine(target, item.Path.Replace('/', Path.DirectorySeparatorChar)));
        if (!restored.Exists || restored.Length != item.Size)
            return new RestoredFile(item.Path, item.Size, RestoredFileResult.Missing);
        if (mode != RestoreTestMode.Compare || BorgClient.LocalPath(borg, item.Path) is not { } sourcePath)
            return new RestoredFile(item.Path, item.Size, RestoredFileResult.Restored);

        var source = new FileInfo(sourcePath);
        var unchanged = source.Exists && source.Length == item.Size
            && (comparison == RestoreTestComparison.SizeOnly
                || (item.Modified != DateTimeOffset.MinValue && (source.LastWriteTimeUtc - item.Modified.UtcDateTime).Duration() <= TimeTolerance));
        if (!unchanged)
            return new RestoredFile(item.Path, item.Size, RestoredFileResult.Restored);
        return new RestoredFile(item.Path, item.Size, SameContent(restored.FullName, source.FullName) switch
        {
            true => RestoredFileResult.Identical,
            false => RestoredFileResult.Different,
            // The source is locked or gone meanwhile: it cannot be compared, which is no failure of the backup.
            null => RestoredFileResult.Restored,
        });
    }

    private static bool? SameContent(string first, string second)
    {
        try
        {
            using var a = File.OpenRead(first);
            using var b = File.OpenRead(second);
            var bufferA = new byte[81920];
            var bufferB = new byte[81920];
            while (true)
            {
                var read = a.ReadAtLeast(bufferA, bufferA.Length, throwOnEndOfStream: false);
                var readB = b.ReadAtLeast(bufferB, bufferB.Length, throwOnEndOfStream: false);
                if (read != readB || !bufferA.AsSpan(0, read).SequenceEqual(bufferB.AsSpan(0, readB)))
                    return false;
                if (read == 0)
                    return true;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>borg could not read what it should (a damaged archive) – or could not run at all (no connection, …).</summary>
    private RestoreTestRecord Failure(Func<RestoreTestOutcome, RestoreTestRecord> record, BorgError error) => error.Kind switch
    {
        BorgErrorKind.Cancelled => record(RestoreTestOutcome.Cancelled),
        BorgErrorKind.Other => record(RestoreTestOutcome.Failed) with { Error = Describe(error) },
        _ => record(RestoreTestOutcome.NotRun) with { Error = Describe(error) },
    };

    private string Describe(BorgError error) => describe?.Invoke(error) ?? error.Message ?? error.Kind.ToString();

    private sealed class Relay(Action<BorgExtractProgress> report) : IProgress<BorgExtractProgress>
    {
        public void Report(BorgExtractProgress value) => report(value);
    }
}
