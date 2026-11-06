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
        CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(borg, location, ssh, ["info"],
            ["--json", "--last", last.ToString(CultureInfo.InvariantCulture)], [], passphrase, InfoTimeout, cancellationToken);
        return ParseArchives(result);
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
        string? archive = null, Action<string>? standardErrorLine = null, bool interruptible = false)
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
                standardErrorLine, interruptible);
        }
        finally
        {
            if (sshFiles is not null)
                await DeleteKeyFileAsync(sshFiles, wsl);
        }
    }

    private async Task<BorgResult<string>> RunBorgAsync(BorgInstallation borg, RepositoryLocation location,
        List<string> arguments, Dictionary<string, string?> environment, SshFiles? sshFiles, TimeSpan timeout,
        CancellationToken cancellationToken, Action<string>? standardErrorLine, bool interruptible)
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
                standardErrorLine: interruptible || standardErrorLine is not null ? OnLine : null);
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

        var kind = messageId switch
        {
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
}
