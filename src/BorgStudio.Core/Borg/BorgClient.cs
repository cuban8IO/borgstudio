using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using BorgStudio.Core.Processes;
using BorgStudio.Core.Ssh;
using BorgStudio.Plugins;

namespace BorgStudio.Core.Borg;

/// <summary>How borg logs in to an SSH repository.</summary>
/// <param name="PrivateKey">The private key itself (from the vault). It is written to a file only for the duration of
/// one borg call, next to <paramref name="KnownHostsFile"/> or inside WSL, and deleted afterwards.</param>
/// <param name="KnownHostsFile">BorgStudio's known_hosts with the confirmed host key.</param>
public sealed record SshAccess(string PrivateKey, string KnownHostsFile)
{
    // Never print the key.
    public override string ToString() => $"SshAccess {{ KnownHostsFile = {KnownHostsFile} }}";
}

/// <summary>The files ssh uses during one borg call (paths as ssh sees them: inside WSL when borg runs there).</summary>
internal sealed record SshFiles(string PrivateKeyFile, string KnownHostsFile);

/// <summary>
/// Runs borg 1.x commands on a repository – natively or, on Windows, inside WSL – without ever
/// letting borg ask on a terminal: the passphrase comes from the environment, confirmations are pre-answered,
/// and ssh runs in batch mode with a strictly checked host key.
/// </summary>
public sealed partial class BorgClient(IProcessRunner processRunner, string? wslExecutable = null)
{
    // The first access to a big repository builds borg's cache, which can take a while.
    private static readonly TimeSpan InfoTimeout = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan InitTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan KeyExportTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan CopyTimeout = TimeSpan.FromSeconds(30);

    // Large first backups take hours; the user can always cancel.
    private static readonly TimeSpan CreateTimeout = TimeSpan.FromDays(3);

    /// <summary>Where SSH files are copied to inside WSL (ssh there rejects keys with Windows permissions).</summary>
    private const string WslSshDirectory = "~/.borgstudio/ssh";

    private const string KeyFilePrefix = "key-";

    /// <summary>Runs borg ("$0") with its arguments after telling its process id (exec keeps the id).</summary>
    private const string PidWrapper = "echo \"" + PidMarker + "$$\" >&2; exec \"$0\" \"$@\"";

    private const string PidMarker = "BORGSTUDIO_PID ";

    /// <summary>How long an interrupted borg gets to stop cleanly (e.g. to write a checkpoint) before it is killed.</summary>
    public static TimeSpan InterruptGrace { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Sends SIGINT to borg (inside WSL if it runs there); kills it if that is not possible or not enough.</summary>
    private async Task InterruptAsync(string? pid, bool wsl, CancellationTokenSource hardStop)
    {
        try
        {
            if (pid is null || !int.TryParse(pid, out _))
            {
                hardStop.Cancel();
                return;
            }

            ProcessResult? sent;
            try
            {
                sent = wsl
                    ? await processRunner.RunAsync(wslExecutable!, ["-e", "kill", "-INT", pid], CopyTimeout)
                    : await processRunner.RunAsync("kill", ["-INT", pid], CopyTimeout);
            }
            catch (TimeoutException)
            {
                sent = null;
            }

            if (sent is { ExitCode: 0 })
                hardStop.CancelAfter(InterruptGrace);
            else
                hardStop.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // borg has exited in the meantime.
        }
    }

    private static readonly string[] ForwardedVariables =
    [
        "BORG_PASSPHRASE", "BORG_DISPLAY_PASSPHRASE", "BORG_RSH",
        "BORG_RELOCATED_REPO_ACCESS_IS_OK", "BORG_UNKNOWN_UNENCRYPTED_REPO_ACCESS_IS_OK",
    ];

    public static BorgClient CreateDefault() => new(
        new ProcessRunner(),
        OperatingSystem.IsWindows() ? Path.Combine(Environment.SystemDirectory, "wsl.exe") : null);

    public async Task<BorgResult<BorgRepositoryInfo>> InfoAsync(
        BorgInstallation borg, RepositoryLocation location, string? passphrase, SshAccess? ssh = null,
        CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(borg, location, ssh, ["info"], ["--json"], [], passphrase, InfoTimeout, cancellationToken);
        if (!result.Succeeded)
            return BorgResult<BorgRepositoryInfo>.Failure(result.Error!);

        try
        {
            return BorgResult<BorgRepositoryInfo>.Success(ParseInfo(result.Value!));
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return BorgResult<BorgRepositoryInfo>.Failure(new BorgError(BorgErrorKind.Other, exception.Message));
        }
    }

    public async Task<BorgResult<bool>> InitAsync(
        BorgInstallation borg, RepositoryLocation location, BorgEncryption encryption, string? passphrase,
        SshAccess? ssh = null, CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(borg, location, ssh, ["init"],
            [$"--encryption={EncryptionName(encryption)}", "--make-parent-dirs"], [], passphrase, InitTimeout, cancellationToken);
        return result.Succeeded ? BorgResult<bool>.Success(true) : BorgResult<bool>.Failure(result.Error!);
    }

    /// <summary>Writes the repository key to <paramref name="targetFile"/> (a path on this computer).</summary>
    public async Task<BorgResult<bool>> ExportKeyAsync(
        BorgInstallation borg, RepositoryLocation location, string targetFile, SshAccess? ssh = null,
        CancellationToken cancellationToken = default)
    {
        var target = borg.Runtime == BorgRuntime.Wsl ? ToWslPath(targetFile) : targetFile;
        if (target is null)
            return BorgResult<bool>.Failure(new BorgError(BorgErrorKind.UnsupportedPath, targetFile));

        var result = await RunAsync(borg, location, ssh, ["key", "export"], [], [target], null, KeyExportTimeout, cancellationToken);
        return result.Succeeded ? BorgResult<bool>.Success(true) : BorgResult<bool>.Failure(result.Error!);
    }

    /// <summary>
    /// <c>borg create</c>: backs up <paramref name="sources"/> (paths on this computer) into a new archive, reporting
    /// progress as it goes. Cancelling interrupts borg cleanly (SIGINT), so it leaves a checkpoint archive the next run
    /// builds on. Files borg could not read make the result succeed with <see cref="BorgCreateResult.Warnings"/>.
    /// </summary>
    /// <param name="archive">Archive name; borg placeholders such as {hostname} and {now} are allowed.</param>
    /// <param name="options">E.g. compression and the exclusions (<see cref="Backups.BackupPatterns"/>).</param>
    public async Task<BorgResult<BorgCreateResult>> CreateAsync(
        BorgInstallation borg, RepositoryLocation location, string archive, IReadOnlyList<string> sources,
        IReadOnlyList<string> options, string? passphrase, SshAccess? ssh = null,
        IProgress<BorgCreateProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var paths = new List<string>();
        foreach (var source in sources)
        {
            if (BorgPath(borg, source) is not { } path)
                return BorgResult<BorgCreateResult>.Failure(new BorgError(BorgErrorKind.UnsupportedPath, source));
            paths.Add(path);
        }

        var warnings = new List<string>();
        void OnLine(string line)
        {
            if (ParseProgress(line) is { } update)
                progress?.Report(update);
            else if (ParseWarning(line) is { } warning)
                warnings.Add(warning);
        }

        var result = await RunAsync(borg, location, ssh, ["create"], ["--progress", "--json", .. options], paths,
            passphrase, CreateTimeout, cancellationToken, archive, OnLine, interruptible: true);
        if (!result.Succeeded)
            return BorgResult<BorgCreateResult>.Failure(result.Error!);

        try
        {
            return BorgResult<BorgCreateResult>.Success(ParseCreate(result.Value!) with { Warnings = warnings });
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return BorgResult<BorgCreateResult>.Failure(new BorgError(BorgErrorKind.Other, exception.Message));
        }
    }

    /// <summary>All archives of the repository with name and start time (<c>borg list --json</c>, fast), newest first.</summary>
    public async Task<BorgResult<IReadOnlyList<BorgArchive>>> ListArchivesAsync(
        BorgInstallation borg, RepositoryLocation location, string? passphrase, SshAccess? ssh = null,
        CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(borg, location, ssh, ["list"], ["--json"], [], passphrase, InfoTimeout, cancellationToken);
        return ParseArchives(result);
    }

    /// <summary>
    /// The newest <paramref name="last"/> archives with duration and sizes (<c>borg info --json --last N</c>). borg
    /// works through every one of them, so this takes a while for large archives.
    /// </summary>
    public async Task<BorgResult<IReadOnlyList<BorgArchive>>> ArchiveDetailsAsync(
        BorgInstallation borg, RepositoryLocation location, int last, string? passphrase, SshAccess? ssh = null,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        // borg reports, archive by archive, how far it is with the statistics; each one ends with "finished".
        var done = 0;
        void OnLine(string line)
        {
            if (ParseProgressPercent(line) is not { MessageId: "archive.calc_stats" } update)
                return;
            if (update.Finished)
                done++;
            progress?.Report(Math.Min(1, (done + (update.Finished ? 0 : update.Fraction)) / Math.Max(1, last)));
        }

        var result = await RunAsync(borg, location, ssh, ["info"],
            ["--json", "--progress", "--last", last.ToString(CultureInfo.InvariantCulture)], [], passphrase, InfoTimeout, cancellationToken,
            standardErrorLine: OnLine);
        return ParseArchives(result);
    }

    /// <summary>
    /// <c>borg extract</c>: restores <paramref name="paths"/> (borg paths, e.g. "mnt/c/Users/me/Documents"; none = the
    /// whole archive) into <paramref name="targetDirectory"/> (a folder on this computer, which must exist), dropping the
    /// first <paramref name="stripComponents"/> path segments. Existing files are overwritten. borg first works out the
    /// total size (<see cref="BorgExtractProgress.Preparing"/>), then reports bytes and the current file. Cancelling
    /// interrupts borg cleanly; what was restored so far stays.
    /// </summary>
    public async Task<BorgResult<BorgExtractResult>> ExtractAsync(
        BorgInstallation borg, RepositoryLocation location, string archive, IReadOnlyList<string> paths, string targetDirectory,
        int stripComponents, string? passphrase, SshAccess? ssh = null, IProgress<BorgExtractProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (borg.Runtime == BorgRuntime.Wsl && ToWslPath(targetDirectory) is null)
            return BorgResult<BorgExtractResult>.Failure(new BorgError(BorgErrorKind.UnsupportedPath, targetDirectory));

        var warnings = new List<string>();
        void OnLine(string line)
        {
            if (ParseProgressPercent(line) is { MessageId: "extract" } update)
            {
                if (update is { Current: { } done, Total: { } total and > 0 })
                    progress?.Report(new BorgExtractProgress((long)done, (long)total, update.Info));
            }
            else if (line.Contains("\"extract\"", StringComparison.Ordinal) && line.Contains("Calculating", StringComparison.Ordinal))
                progress?.Report(new BorgExtractProgress(0, 0, null, Preparing: true));
            else if (ParseWarning(line) is { } warning)
                warnings.Add(warning);
        }

        List<string> options = ["--progress"];
        if (stripComponents > 0)
            options.AddRange(["--strip-components", stripComponents.ToString(CultureInfo.InvariantCulture)]);
        // "pp:" – path prefixes, taken literally (names may contain *, [ or ?).
        var result = await RunAsync(borg, location, ssh, ["extract"], options, [.. paths.Select(path => "pp:" + path)], passphrase,
            CreateTimeout, cancellationToken, archive, OnLine, interruptible: true, workingDirectory: targetDirectory);
        return result.Succeeded
            ? BorgResult<BorgExtractResult>.Success(new BorgExtractResult(warnings))
            : BorgResult<BorgExtractResult>.Failure(result.Error!);
    }

    /// <summary>
    /// The contents of an archive (<c>borg list --json-lines</c>) as a folder tree, read line by line – archives can
    /// have millions of entries. borg reports no progress here; <paramref name="entriesRead"/> tells how many entries
    /// were read so far (every 1000 and at the end).
    /// </summary>
    public async Task<BorgResult<ArchiveTree>> ListContentsAsync(
        BorgInstallation borg, RepositoryLocation location, string archive, string? passphrase, SshAccess? ssh = null,
        IProgress<long>? entriesRead = null, CancellationToken cancellationToken = default)
    {
        var tree = new ArchiveTree();
        void OnLine(string line)
        {
            if (BorgItem.Parse(line) is not { } item)
                return;
            tree.Add(item);
            if (tree.EntryCount % 1000 == 0)
                entriesRead?.Report(tree.EntryCount);
        }

        var result = await RunAsync(borg, location, ssh, ["list"], ["--json-lines"], [], passphrase, CreateTimeout, cancellationToken,
            archive, standardOutputLine: OnLine);
        if (!result.Succeeded)
            return BorgResult<ArchiveTree>.Failure(result.Error!);
        entriesRead?.Report(tree.EntryCount);
        return BorgResult<ArchiveTree>.Success(tree.Complete());
    }

    /// <summary>
    /// <c>borg prune</c>: deletes the archives matching <paramref name="archiveGlob"/> that none of the keep rules
    /// keeps. With <paramref name="dryRun"/> it only tells what it would do. Deleting can be cancelled cleanly (borg
    /// commits only at the end). The space is freed only by <see cref="CompactAsync"/>.
    /// </summary>
    /// <param name="archiveGlob">Which archives to look at, e.g. "Documents-{hostname}-*" (borg placeholders allowed).</param>
    /// <param name="keepOptions">The rules (<see cref="Backups.RetentionPolicy.Arguments"/>); at least one.</param>
    public async Task<BorgResult<BorgPruneResult>> PruneAsync(
        BorgInstallation borg, RepositoryLocation location, string archiveGlob, IReadOnlyList<string> keepOptions,
        bool dryRun, string? passphrase, SshAccess? ssh = null, IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var archives = new List<BorgPruneEntry>();
        void OnLine(string line)
        {
            if (ParsePruneLine(line) is { } entry)
                archives.Add(entry);
            else if (ParseProgressPercent(line) is { MessageId: "prune" } update)
                progress?.Report(update.Finished ? 1 : update.Fraction);
        }

        List<string> options = ["--list", "--progress", "--glob-archives", archiveGlob, .. keepOptions];
        if (dryRun)
            options.Insert(0, "--dry-run");
        var result = await RunAsync(borg, location, ssh, ["prune"], options, [], passphrase,
            dryRun ? InfoTimeout : CreateTimeout, cancellationToken, standardErrorLine: OnLine, interruptible: !dryRun);
        if (!result.Succeeded)
            return BorgResult<BorgPruneResult>.Failure(result.Error!);

        // borg lists newest first already; sort anyway, the preview shows them in that order.
        return BorgResult<BorgPruneResult>.Success(new BorgPruneResult(
            [.. archives.Select((archive, index) => (archive, index))
                .OrderByDescending(entry => entry.archive.Start).ThenBy(entry => entry.index).Select(entry => entry.archive)]));
    }

    /// <summary>
    /// <c>borg compact</c>: frees the space of deleted archives in the repository, reporting progress (0..1). Needs no
    /// passphrase (it works on the repository's segments only). Can be cancelled cleanly.
    /// </summary>
    public async Task<BorgResult<BorgCompactResult>> CompactAsync(
        BorgInstallation borg, RepositoryLocation location, SshAccess? ssh = null, IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        long? freed = null;
        void OnLine(string line)
        {
            if (ParseProgressPercent(line) is { MessageId: "repository.compact_segments" } update)
                progress?.Report(update.Finished ? 1 : update.Fraction);
            else if (ParseCompactFreed(line) is { } bytes)
                freed = bytes;
        }

        // --info: borg tells how much it freed only at info level.
        var result = await RunAsync(borg, location, ssh, ["compact"], ["--info", "--progress"], [], null, CreateTimeout,
            cancellationToken, standardErrorLine: OnLine, interruptible: true);
        return result.Succeeded
            ? BorgResult<BorgCompactResult>.Success(new BorgCompactResult(freed))
            : BorgResult<BorgCompactResult>.Failure(result.Error!);
    }

    /// <summary>
    /// A line of <c>borg prune --list --log-json</c> about one archive, e.g. "Keeping archive (rule: daily #1):  name
    /// Fri, 2026-11-06 21:06:16 [id]" or "Would prune: …"/"Pruning archive (1/2): …"; <c>null</c> for any other line.
    /// </summary>
    internal static BorgPruneEntry? ParsePruneLine(string line)
    {
        if (!line.Contains("borg.output.list", StringComparison.Ordinal))
            return null;
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (!root.TryGetProperty("message", out var text) || text.GetString() is not { } message)
                return null;
            var match = PruneLine().Match(message.Trim());
            if (!match.Success)
                return null;

            var start = DateTimeOffset.TryParseExact(match.Groups["time"].Value, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal, out var parsed) ? parsed : DateTimeOffset.MinValue;
            var kept = match.Groups["keep"].Success;
            var rule = match.Groups["rule"].Success ? match.Groups["rule"].Value : null;
            var number = match.Groups["number"].Success ? int.Parse(match.Groups["number"].Value, CultureInfo.InvariantCulture) : 0;
            return new BorgPruneEntry(match.Groups["name"].Value, match.Groups["id"].Value, start, kept, rule, number);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// A <c>progress_percent</c> line of <c>--progress --log-json</c> (compact, prune, info, extract, …); <c>null</c> for
    /// any other line. The fraction is the percentage borg shows itself, or current/total if the message has none.
    /// </summary>
    internal static BorgProgressPercent? ParseProgressPercent(string line)
    {
        if (!line.Contains("\"progress_percent\"", StringComparison.Ordinal))
            return null;
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (!root.TryGetProperty("type", out var type) || type.GetString() != "progress_percent")
                return null;
            var messageId = root.TryGetProperty("msgid", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null;
            if (root.TryGetProperty("finished", out var finished) && finished.ValueKind == JsonValueKind.True)
                return new BorgProgressPercent(messageId, 1, true, null);

            var message = root.TryGetProperty("message", out var text) && text.ValueKind == JsonValueKind.String ? text.GetString() : null;
            double? fraction = message is not null && PercentInMessage().Match(message) is { Success: true } match
                && double.TryParse(match.Groups["percent"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var percent)
                ? percent / 100
                : root.TryGetProperty("current", out var current) && root.TryGetProperty("total", out var total)
                  && current.ValueKind == JsonValueKind.Number && total.ValueKind == JsonValueKind.Number && total.GetDouble() > 0
                    ? current.GetDouble() / total.GetDouble()
                    : null;
            if (fraction is null)
                return null;
            var info = root.TryGetProperty("info", out var details) && details.ValueKind == JsonValueKind.Array && details.GetArrayLength() > 0
                ? details[0].GetString()
                : null;
            double? Number(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;
            return new BorgProgressPercent(messageId, Math.Clamp(fraction.Value, 0, 1), false, info, Number("current"), Number("total"));
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>"compaction freed about 5.35 kB repository space." in bytes; <c>null</c> for any other line.</summary>
    internal static long? ParseCompactFreed(string line)
    {
        if (!line.Contains("compaction freed", StringComparison.Ordinal))
            return null;
        try
        {
            using var document = JsonDocument.Parse(line);
            if (!document.RootElement.TryGetProperty("message", out var text) || text.GetString() is not { } message)
                return null;
            var match = CompactFreed().Match(message);
            if (!match.Success
                || !double.TryParse(match.Groups["value"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                return null;
            // borg uses decimal units (kB = 1000 B).
            var power = "BkMGTPEZY".IndexOf(match.Groups["unit"].Value[0], StringComparison.Ordinal);
            return (long)Math.Max(0, Math.Round(value * Math.Pow(1000, Math.Max(0, power))));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static BorgResult<IReadOnlyList<BorgArchive>> ParseArchives(BorgResult<string> result)
    {
        if (!result.Succeeded)
            return BorgResult<IReadOnlyList<BorgArchive>>.Failure(result.Error!);
        try
        {
            return BorgResult<IReadOnlyList<BorgArchive>>.Success(ParseArchives(result.Value!));
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return BorgResult<IReadOnlyList<BorgArchive>>.Failure(new BorgError(BorgErrorKind.Other, exception.Message));
        }
    }

    /// <summary>The "archives" of <c>borg list --json</c> or <c>borg info --json</c>, newest first.</summary>
    internal static IReadOnlyList<BorgArchive> ParseArchives(string json)
    {
        using var document = JsonDocument.Parse(json);
        var archives = new List<BorgArchive>();
        foreach (var archive in document.RootElement.GetProperty("archives").EnumerateArray())
        {
            // borg 1.x writes local time without offset.
            var start = archive.TryGetProperty("start", out var started)
                && DateTimeOffset.TryParse(started.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed)
                ? parsed
                : DateTimeOffset.MinValue;
            TimeSpan? duration = archive.TryGetProperty("duration", out var seconds) && seconds.ValueKind == JsonValueKind.Number
                ? TimeSpan.FromSeconds(seconds.GetDouble())
                : null;
            BorgArchiveStats? stats = archive.TryGetProperty("stats", out var values)
                ? new BorgArchiveStats(values.GetProperty("original_size").GetInt64(), values.GetProperty("compressed_size").GetInt64(),
                    values.GetProperty("deduplicated_size").GetInt64(), values.GetProperty("nfiles").GetInt64())
                : null;
            archives.Add(new BorgArchive(archive.GetProperty("name").GetString()!, archive.GetProperty("id").GetString()!, start, duration, stats));
        }
        // borg lists oldest first; borg 1.2 gives "list" times to the second only, so equal times keep borg's order.
        return [.. archives.Select((archive, index) => (archive, index))
            .OrderByDescending(entry => entry.archive.Start).ThenByDescending(entry => entry.index).Select(entry => entry.archive)];
    }

    /// <summary>A path on this computer as borg sees it: unchanged, or "/mnt/c/…" inside WSL (<c>null</c> if WSL cannot reach it).</summary>
    public static string? BorgPath(BorgInstallation borg, string path) =>
        borg.Runtime == BorgRuntime.Wsl ? ToWslPath(path) : path;

    /// <summary>A progress line of <c>borg create --progress --log-json</c>; <c>null</c> for any other line.</summary>
    internal static BorgCreateProgress? ParseProgress(string line)
    {
        if (!line.Contains("\"archive_progress\"", StringComparison.Ordinal))
            return null;
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.GetProperty("type").GetString() != "archive_progress")
                return null;
            long Number(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt64() : 0;
            var finished = root.TryGetProperty("finished", out var done) && done.ValueKind == JsonValueKind.True;
            var path = root.TryGetProperty("path", out var current) && current.ValueKind == JsonValueKind.String ? current.GetString() : null;
            return new BorgCreateProgress(Number("original_size"), Number("compressed_size"), Number("deduplicated_size"),
                Number("nfiles"), path, finished);
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>A warning borg logged (e.g. a file it could not read); <c>null</c> for any other line.</summary>
    internal static string? ParseWarning(string line)
    {
        if (!line.Contains("\"WARNING\"", StringComparison.Ordinal))
            return null;
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            return root.TryGetProperty("type", out var type) && type.GetString() == "log_message"
                && root.TryGetProperty("levelname", out var level) && level.GetString() == "WARNING"
                && root.TryGetProperty("message", out var message)
                ? message.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The JSON <c>borg create --json</c> prints at the end.</summary>
    internal static BorgCreateResult ParseCreate(string json)
    {
        using var document = JsonDocument.Parse(json);
        var archive = document.RootElement.GetProperty("archive");
        var stats = archive.GetProperty("stats");
        return new BorgCreateResult(
            archive.GetProperty("name").GetString()!,
            stats.GetProperty("original_size").GetInt64(),
            stats.GetProperty("compressed_size").GetInt64(),
            stats.GetProperty("deduplicated_size").GetInt64(),
            stats.GetProperty("nfiles").GetInt64(),
            TimeSpan.FromSeconds(archive.TryGetProperty("duration", out var duration) ? duration.GetDouble() : 0),
            []);
    }

    /// <summary>
    /// Puts a keyfile key (as <c>borg key export</c> wrote it: "BORG_KEY &lt;repository id&gt;" plus the key) into the
    /// key folder of this borg – inside WSL if borg runs there – where borg finds it by the repository id. Needs no
    /// access to the repository.
    /// </summary>
    public async Task<BorgResult<bool>> InstallKeyfileAsync(BorgInstallation borg, string repositoryId, string key,
        CancellationToken cancellationToken = default)
    {
        var wsl = borg.Runtime == BorgRuntime.Wsl;
        if ((wsl && wslExecutable is null) || (!wsl && OperatingSystem.IsWindows()))
            return BorgResult<bool>.Failure(new BorgError(BorgErrorKind.NotStartable, "sh"));
        if (!SafeRepositoryId().IsMatch(repositoryId))
            return BorgResult<bool>.Failure(new BorgError(BorgErrorKind.Other, "invalid repository id"));

        // borg's own lookup order for the key folder.
        var script = "umask 077 && d=\"${BORG_KEYS_DIR:-${BORG_CONFIG_DIR:-${XDG_CONFIG_HOME:-$HOME/.config}/borg}/keys}\""
            + $" && mkdir -p \"$d\" && cat > \"$d/borgstudio-{repositoryId[..Math.Min(16, repositoryId.Length)]}\"";
        ProcessResult? result;
        try
        {
            result = wsl
                ? await processRunner.RunAsync(wslExecutable!, ["-e", "sh", "-c", script], CopyTimeout,
                    standardInput: key.EndsWith('\n') ? key : key + "\n", cancellationToken: cancellationToken)
                : await processRunner.RunAsync("sh", ["-c", script], CopyTimeout,
                    standardInput: key.EndsWith('\n') ? key : key + "\n", cancellationToken: cancellationToken);
        }
        catch (TimeoutException)
        {
            return BorgResult<bool>.Failure(new BorgError(BorgErrorKind.Timeout));
        }

        return result is { ExitCode: 0 }
            ? BorgResult<bool>.Success(true)
            : BorgResult<bool>.Failure(new BorgError(BorgErrorKind.Other, result?.StandardError.Trim() ?? "sh"));
    }

    public static string EncryptionName(BorgEncryption encryption) => encryption switch
    {
        BorgEncryption.RepokeyBlake2 => "repokey-blake2",
        BorgEncryption.KeyfileBlake2 => "keyfile-blake2",
        BorgEncryption.None => "none",
        _ => throw new ArgumentOutOfRangeException(nameof(encryption)),
    };

    /// <summary>
    /// Windows path as seen from WSL ("C:\Backups\repo" -> "/mnt/c/Backups/repo"); <c>null</c> for paths WSL cannot
    /// reach this way (network shares).
    /// </summary>
    public static string? ToWslPath(string windowsPath)
    {
        var match = DrivePath().Match(windowsPath);
        if (!match.Success)
            return null;

        var drive = char.ToLowerInvariant(match.Groups["drive"].Value[0]);
        var rest = match.Groups["rest"].Value.Replace('\\', '/').TrimEnd('/');
        return rest.Length == 0 ? $"/mnt/{drive}" : $"/mnt/{drive}/{rest}";
    }

    /// <summary>
    /// The ssh command line for BORG_RSH (borg splits it like a POSIX shell): key only, no prompts,
    /// host key checked against BorgStudio's known_hosts.
    /// </summary>
    public static string SshCommand(string privateKeyFile, string knownHostsFile) =>
        "ssh -o BatchMode=yes -o IdentitiesOnly=yes -o StrictHostKeyChecking=yes"
        + $" -o UserKnownHostsFile={ShellQuote(knownHostsFile)} -i {ShellQuote(privateKeyFile)}";

    /// <summary>POSIX shell single quoting (keeps Windows backslashes as they are).</summary>
    public static string ShellQuote(string value) => "'" + value.Replace("'", "'\"'\"'") + "'";

    /// <summary>
    /// <c>borg &lt;command&gt; --log-json &lt;provider arguments&gt; &lt;options&gt; &lt;repository&gt;[::&lt;archive&gt;] &lt;trailing&gt;</c>;
    /// returns standard output on success.
    /// </summary>
    /// <param name="standardErrorLine">borg's JSON log lines as they arrive (progress).</param>
    /// <param name="interruptible">
    /// Cancelling interrupts borg like Ctrl+C (SIGINT), so it can stop cleanly, instead of killing it; it is killed only
    /// if it doesn't stop within <see cref="InterruptGrace"/>.
    /// </param>
    private async Task<BorgResult<string>> RunAsync(
        BorgInstallation borg, RepositoryLocation location, SshAccess? ssh,
        IReadOnlyList<string> command, IReadOnlyList<string> options, IReadOnlyList<string> trailing,
        string? passphrase, TimeSpan timeout, CancellationToken cancellationToken,
        string? archive = null, Action<string>? standardErrorLine = null, bool interruptible = false,
        Action<string>? standardOutputLine = null, string? workingDirectory = null)
    {
        var wsl = borg.Runtime == BorgRuntime.Wsl;
        if (wsl && wslExecutable is null)
            return BorgResult<string>.Failure(new BorgError(BorgErrorKind.NotStartable, "wsl.exe"));

        var repository = location.Url;
        if (wsl && !IsUrl(repository))
        {
            if (ToWslPath(repository) is not { } translated)
                return BorgResult<string>.Failure(new BorgError(BorgErrorKind.UnsupportedPath, location.Url));
            repository = translated;
        }
        if (archive is not null)
            repository += "::" + archive;

        List<string> arguments = [.. command, "--log-json", .. location.BorgArguments, .. options, repository, .. trailing];

        var environment = new Dictionary<string, string?>
        {
            // Always set, so borg never prompts for it: an encrypted repository with an empty passphrase fails fast.
            ["BORG_PASSPHRASE"] = passphrase ?? "",
            ["BORG_DISPLAY_PASSPHRASE"] = "no",
            // The user picked the location: accept a moved repository (e.g. an external disk with a new drive letter)
            // and unencrypted repositories instead of asking on a terminal the GUI doesn't have.
            ["BORG_RELOCATED_REPO_ACCESS_IS_OK"] = "yes",
            ["BORG_UNKNOWN_UNENCRYPTED_REPO_ACCESS_IS_OK"] = "yes",
        };

        SshFiles? sshFiles = null;
        if (location.Ssh is not null && ssh is not null)
        {
            sshFiles = wsl ? await CopySshFilesIntoWslAsync(ssh, cancellationToken) : WriteSshFiles(ssh);
            if (sshFiles is null)
                return BorgResult<string>.Failure(new BorgError(BorgErrorKind.NotStartable, "wsl.exe (copying the SSH key)"));
            environment["BORG_RSH"] = SshCommand(sshFiles.PrivateKeyFile, sshFiles.KnownHostsFile);
        }

        try
        {
            return await RunBorgAsync(borg, location, arguments, environment, sshFiles, timeout, cancellationToken,
                standardErrorLine, interruptible, standardOutputLine, workingDirectory);
        }
        finally
        {
            if (sshFiles is not null)
                await DeleteKeyFileAsync(sshFiles, wsl);
        }
    }

    private async Task<BorgResult<string>> RunBorgAsync(BorgInstallation borg, RepositoryLocation location,
        List<string> arguments, Dictionary<string, string?> environment, SshFiles? sshFiles, TimeSpan timeout,
        CancellationToken cancellationToken, Action<string>? standardErrorLine, bool interruptible,
        Action<string>? standardOutputLine = null, string? workingDirectory = null)
    {
        var wsl = borg.Runtime == BorgRuntime.Wsl;
        // Signals need a POSIX system: natively on Linux/macOS, or inside WSL.
        interruptible &= wsl || !OperatingSystem.IsWindows();

        string fileName;
        if (wsl)
        {
            fileName = wslExecutable!;
            arguments.InsertRange(0, interruptible ? ["-e", "sh", "-c", PidWrapper, borg.Path] : ["-e", borg.Path]);
            // WSL only passes on the Windows environment variables listed in WSLENV.
            var existing = Environment.GetEnvironmentVariable("WSLENV");
            environment["WSLENV"] = string.Join(':', new[] { existing }.Where(value => !string.IsNullOrEmpty(value)).Concat(ForwardedVariables));
        }
        else if (interruptible)
        {
            fileName = "sh";
            arguments.InsertRange(0, ["-c", PidWrapper, borg.Path]);
        }
        else
        {
            fileName = borg.Path;
        }

        // Cancelling an interruptible call: SIGINT first, a hard stop only after the grace period.
        using var hardStop = interruptible
            ? new CancellationTokenSource()
            : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        string? pid = null;
        using var onCancel = interruptible
            ? cancellationToken.Register(() => _ = InterruptAsync(pid, wsl, hardStop))
            : default;

        void OnLine(string line)
        {
            if (interruptible && pid is null && line.StartsWith(PidMarker, StringComparison.Ordinal))
            {
                pid = line[PidMarker.Length..].Trim();
                return;
            }
            standardErrorLine?.Invoke(line);
        }

        ProcessResult? result;
        try
        {
            result = await processRunner.RunAsync(fileName, arguments, timeout, environment,
                cancellationToken: hardStop.Token,
                standardErrorLine: interruptible || standardErrorLine is not null ? OnLine : null,
                standardOutputLine: standardOutputLine, workingDirectory: workingDirectory);
        }
        catch (TimeoutException)
        {
            return BorgResult<string>.Failure(new BorgError(BorgErrorKind.Timeout));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return BorgResult<string>.Failure(new BorgError(BorgErrorKind.Cancelled));
        }

        if (result is not null && cancellationToken.IsCancellationRequested)
            return BorgResult<string>.Failure(new BorgError(BorgErrorKind.Cancelled, result.StandardOutput));

        if (result is null)
            return BorgResult<string>.Failure(new BorgError(BorgErrorKind.NotStartable, fileName));

        // 0 = success, 1 = success with warnings, everything else is an error.
        if (result.ExitCode is 0 or 1)
            return BorgResult<string>.Success(result.StandardOutput);

        var error = ParseError(result.StandardError);
        if (error.Kind == BorgErrorKind.ConnectionFailed && location.Ssh is { } endpoint && sshFiles is not null)
            error = await DiagnoseSshAsync(endpoint, sshFiles, wsl, cancellationToken) ?? error;
        return BorgResult<string>.Failure(error);
    }

    /// <summary>
    /// borg forwards ssh's own error ("Host key verification failed", "Permission denied") only if it reads it
    /// before noticing the closed connection – a race. When borg just says "connection closed", one plain ssh login
    /// with the same settings tells reliably what's wrong; <c>null</c> if it doesn't tell more.
    /// </summary>
    private async Task<BorgError?> DiagnoseSshAsync(
        SshEndpoint endpoint, SshFiles sshFiles, bool wsl, CancellationToken cancellationToken)
    {
        List<string> arguments =
        [
            "-o", "BatchMode=yes", "-o", "IdentitiesOnly=yes", "-o", "StrictHostKeyChecking=yes",
            "-o", $"UserKnownHostsFile={sshFiles.KnownHostsFile}", "-i", sshFiles.PrivateKeyFile,
            "-o", "ConnectTimeout=15", "-p", endpoint.Port.ToString(CultureInfo.InvariantCulture),
            $"{endpoint.User}@{endpoint.Host}", "exit",
        ];
        if (wsl)
            arguments.InsertRange(0, ["-e", "ssh"]);

        ProcessResult? result;
        try
        {
            result = await processRunner.RunAsync(wsl ? wslExecutable! : "ssh", arguments, CopyTimeout,
                cancellationToken: cancellationToken);
        }
        catch (TimeoutException)
        {
            return null;
        }

        var output = result?.StandardError.Trim() ?? "";
        if (output.Contains("Host key verification failed", StringComparison.Ordinal))
            return new BorgError(BorgErrorKind.SshHostKeyFailed, output);
        if (output.Contains("Permission denied", StringComparison.Ordinal))
            return new BorgError(BorgErrorKind.SshAuthenticationFailed, output);
        return null;
    }

    /// <summary>Writes the key to a temporary file next to known_hosts (private permissions).</summary>
    private static SshFiles WriteSshFiles(SshAccess ssh) =>
        new(TemporaryKeyFile.Write(Path.GetDirectoryName(Path.GetFullPath(ssh.KnownHostsFile))!, KeyFilePrefix, ssh.PrivateKey),
            ssh.KnownHostsFile);

    /// <summary>
    /// Copies key and known_hosts to ~/.borgstudio/ssh inside WSL (private permissions; ssh there rejects keys with
    /// Windows permissions); returns those paths. Key copies older than an hour (left by a crash or by older versions,
    /// which kept them) are removed on the way.
    /// </summary>
    private async Task<SshFiles?> CopySshFilesIntoWslAsync(SshAccess ssh, CancellationToken cancellationToken)
    {
        var keyName = KeyFilePrefix + Guid.NewGuid().ToString("N");
        var knownHosts = File.Exists(ssh.KnownHostsFile) ? await File.ReadAllTextAsync(ssh.KnownHostsFile, cancellationToken) : "";
        var minutes = (int)TemporaryKeyFile.MaximumAge.TotalMinutes;
        var copies = new[]
        {
            (Content: ssh.PrivateKey.EndsWith('\n') ? ssh.PrivateKey : ssh.PrivateKey + "\n",
             Command: $"umask 077 && mkdir -p {WslSshDirectory} && (find {WslSshDirectory} -name '{KeyFilePrefix}*' -mmin +{minutes} -delete 2>/dev/null; true) && cat > {WslSshDirectory}/{keyName}"),
            (Content: knownHosts, Command: $"umask 077 && cat > {WslSshDirectory}/known_hosts"),
        };

        foreach (var (content, command) in copies)
        {
            ProcessResult? copied;
            try
            {
                copied = await processRunner.RunAsync(wslExecutable!, ["-e", "sh", "-c", command],
                    CopyTimeout, standardInput: content, cancellationToken: cancellationToken);
            }
            catch (TimeoutException)
            {
                return null;
            }
            if (copied is not { ExitCode: 0 })
                return null;
        }

        // ssh expands the "~" itself.
        return new SshFiles($"{WslSshDirectory}/{keyName}", $"{WslSshDirectory}/known_hosts");
    }

    /// <summary>Removes the key file of one call again; a failure only leaves it for the next clean-up.</summary>
    private async Task DeleteKeyFileAsync(SshFiles files, bool wsl)
    {
        if (!wsl)
        {
            try
            {
                File.Delete(files.PrivateKeyFile);
            }
            catch (IOException)
            {
            }
            return;
        }

        try
        {
            await processRunner.RunAsync(wslExecutable!, ["-e", "sh", "-c", $"rm -f {files.PrivateKeyFile}"], CopyTimeout);
        }
        catch (TimeoutException)
        {
        }
    }

    /// <summary>The last error from borg's JSON log on standard error, classified by its message id.</summary>
    internal static BorgError ParseError(string standardError)
    {
        string? messageId = null;
        string? message = null;
        var allText = new List<string>();
        var plainText = new List<string>();

        foreach (var line in standardError.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object || root.GetProperty("type").GetString() != "log_message")
                    continue;

                var text = root.TryGetProperty("message", out var textElement) ? textElement.GetString() : null;
                if (text is not null)
                    allText.Add(text);
                if (root.GetProperty("levelname").GetString() is not ("ERROR" or "CRITICAL"))
                    continue;

                message = text ?? message;
                if (root.TryGetProperty("msgid", out var id) && id.GetString() is { Length: > 0 } value)
                    messageId = value;
            }
            catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
            {
                plainText.Add(line);
                allText.Add(line);
            }
        }

        // ssh's own complaints arrive as "Remote: …" lines before borg's generic "connection closed".
        var sshHostKeyFailed = allText.Any(text => text.Contains("Host key verification failed", StringComparison.Ordinal));
        var sshPermissionDenied = allText.Any(text => text.Contains("Permission denied", StringComparison.Ordinal));

        // FUSE's own complaints are plain text before borg's generic "FUSE mount failed".
        var mountNotAllowed = allText.Any(text => text.Contains("user_allow_other", StringComparison.Ordinal));
        var mountUnavailable = allText.Any(text => text.Contains("no FUSE support", StringComparison.OrdinalIgnoreCase)
            || text.Contains("fusermount: not found", StringComparison.Ordinal) || text.Contains("/dev/fuse", StringComparison.Ordinal));

        var kind = messageId switch
        {
            _ when mountNotAllowed => BorgErrorKind.MountNotAllowed,
            _ when mountUnavailable => BorgErrorKind.MountUnavailable,
            _ when sshHostKeyFailed => BorgErrorKind.SshHostKeyFailed,
            _ when sshPermissionDenied => BorgErrorKind.SshAuthenticationFailed,
            null => BorgErrorKind.Other,
            _ when messageId.Contains("PassphraseWrong", StringComparison.Ordinal) => BorgErrorKind.PassphraseWrong,
            _ when messageId.Contains("DoesNotExist", StringComparison.Ordinal) => BorgErrorKind.RepositoryNotFound,
            _ when messageId.Contains("InvalidRepository", StringComparison.Ordinal) => BorgErrorKind.NotARepository,
            _ when messageId.Contains("AlreadyExists", StringComparison.Ordinal) => BorgErrorKind.RepositoryExists,
            _ when messageId.Contains("Lock", StringComparison.Ordinal) => BorgErrorKind.Locked,
            _ when messageId.Contains("ConnectionClosed", StringComparison.Ordinal) => BorgErrorKind.ConnectionFailed,
            _ => BorgErrorKind.Other,
        };
        return new BorgError(kind, message ?? (plainText.Count > 0 ? string.Join(Environment.NewLine, plainText) : null));
    }

    internal static BorgRepositoryInfo ParseInfo(string json)
    {
        using var document = JsonDocument.Parse(json);
        var repository = document.RootElement.GetProperty("repository");
        var encryption = document.RootElement.GetProperty("encryption");

        DateTimeOffset? lastModified = null;
        if (repository.TryGetProperty("last_modified", out var modified)
            && DateTimeOffset.TryParse(modified.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed))
            lastModified = parsed;

        return new BorgRepositoryInfo(
            repository.GetProperty("id").GetString()!,
            encryption.GetProperty("mode").GetString()!,
            lastModified);
    }

    private static bool IsUrl(string repository) => repository.Contains("://", StringComparison.Ordinal);

    [GeneratedRegex(@"^(?<drive>[A-Za-z]):[\\/]?(?<rest>.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex DrivePath();

    [GeneratedRegex("^[0-9a-fA-F]{8,}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeRepositoryId();

    // "<what>:   <name>   <weekday>, <yyyy-mm-dd hh:mm:ss> [<id>]"; "what" is "Keeping archive (rule: daily #1)" (borg 1.4
    // may add "[oldest]" to the rule), "Keeping checkpoint archive", "Would prune" or "Pruning archive (1/2)".
    [GeneratedRegex(@"^(?:(?<keep>Keeping archive(?: \(rule: (?<rule>[a-z]+)[^#)]*#(?<number>\d+)\))?|Keeping checkpoint archive)|Would prune|Pruning archive[^:]*):\s+(?<name>.+?)\s+\S+\s+(?<time>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})\s+\[(?<id>[0-9a-f]+)\]$",
        RegexOptions.CultureInvariant)]
    private static partial Regex PruneLine();

    [GeneratedRegex(@"(?<percent>[0-9]+(?:\.[0-9]+)?)%", RegexOptions.CultureInvariant)]
    private static partial Regex PercentInMessage();

    [GeneratedRegex(@"compaction freed about (?<value>-?[0-9.]+) (?<unit>[kMGTPEZY]?B)\b", RegexOptions.CultureInvariant)]
    private static partial Regex CompactFreed();
}
