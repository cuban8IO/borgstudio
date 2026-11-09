using System.Globalization;
using BorgStudio.App.Services;
using BorgStudio.App.ViewModels;
using BorgStudio.Core.Backups;
using BorgStudio.Core.Borg;
using BorgStudio.Core.Plugins;
using BorgStudio.Core.Processes;
using BorgStudio.Core.Progress;
using BorgStudio.Core.Repositories;
using BorgStudio.Core.Secrets;
using BorgStudio.Core.Security;
using BorgStudio.Core.Settings;
using BorgStudio.Core.Ssh;
using BorgStudio.Plugins;
using BorgStudio.Providers.Hetzner;
using BorgStudio.Providers.Local;
using BorgStudio.Providers.Ssh;

namespace BorgStudio.Tests.TestSupport;

/// <summary>Plays borg: answers by sub command and records every call.</summary>
public sealed class FakeBorg : IProcessRunner
{
    public const string BorgPath = "/usr/bin/borg";

    /// <summary>wsl.exe as the fake detector finds it when borg is "in WSL".</summary>
    public const string WslExecutable = @"C:\Windows\System32\wsl.exe";

    public static readonly BorgInstallation Installation = new(BorgPath, new BorgVersion(1, 4, 1), BorgRuntime.Native);

    public List<(IReadOnlyList<string> Arguments, string? Passphrase)> Calls { get; } = [];

    public List<IReadOnlyDictionary<string, string?>> Environments { get; } = [];

    /// <summary>Content of the private key file ssh would have used, read during each SSH call.</summary>
    public List<string> KeysDuringCalls { get; } = [];

    /// <summary>The key files ssh was given (to check they are gone afterwards).</summary>
    public List<string> KeyFiles { get; } = [];

    /// <summary>Encryption mode reported by "info"; null makes "info" fail with <see cref="InfoError"/>.</summary>
    public string? EncryptionMode { get; set; } = "repokey-blake2";

    public string InfoError { get; set; } = "Repository.DoesNotExist";

    /// <summary>The passphrase "info" accepts (for encrypted repositories).</summary>
    public string? CorrectPassphrase { get; set; }

    public IEnumerable<string> Commands => Calls.Select(call => call.Arguments[0]);

    /// <summary>Keys put into borg's key folder (through `sh -c`).</summary>
    public List<string> InstalledKeyfiles { get; } = [];

    /// <summary>Warnings `borg create` logs (it then exits with 1).</summary>
    public List<string> CreateWarnings { get; } = [];

    /// <summary>Called after each progress line of `borg create` (e.g. to cancel in the middle).</summary>
    public Action<int>? AfterCreateProgress { get; set; }

    /// <summary>What `borg key export` writes.</summary>
    public string ExportedKey { get; set; } = "BORG_KEY repo-id-1\nKEYDATA\n";

    /// <summary>The archives in the (single) repository, oldest first; "create" adds one.</summary>
    public List<(string Name, DateTime Start)> Archives { get; } = [];

    /// <summary>When set, "info --last" fails with this message id.</summary>
    public string? ArchiveDetailsError { get; set; }

    public Task<ProcessResult?> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout,
        IReadOnlyDictionary<string, string?>? environment = null, string? standardInput = null,
        CancellationToken cancellationToken = default, Action<string>? standardErrorLine = null, Action<string>? standardOutputLine = null, string? workingDirectory = null)
    {
        // borg "in WSL": what wsl.exe would run inside it.
        var wsl = fileName == WslExecutable;
        if (wsl)
        {
            if (arguments is ["-u", "root", "-e", ..])
            {
                RootCommands.Add(string.Join(' ', arguments.Skip(3)));
                if (CanEnableAllowOther)
                    AllowOther = true;
                return Result(CanEnableAllowOther ? 0 : 1, "");
            }
            if (arguments is ["-e", "sh", "-lc", ..])
                return Result(0, BorgPath + "\nborg 1.4.1\n");
            if (arguments is ["-e", var program, ..])
            {
                fileName = program;
                arguments = arguments.Skip(2).ToList();
            }
        }

        // The shell scripts around mounting.
        if (fileName == "sh" && arguments is ["-c", var script, "sh", ..])
            return MountShell(script, arguments.Skip(3).ToList(), wsl);

        // Interruptible calls run borg through a small sh wrapper (on Linux/macOS).
        if (fileName == "sh" && arguments is ["-c", _, BorgPath, ..])
        {
            fileName = BorgPath;
            arguments = arguments.Skip(3).ToList();
        }

        var passphrase = environment?.GetValueOrDefault("BORG_PASSPHRASE");
        Environments.Add(environment ?? new Dictionary<string, string?>());
        Calls.Add((arguments, passphrase));
        if (environment?.GetValueOrDefault("BORG_RSH") is { } rsh
            && System.Text.RegularExpressions.Regex.Match(rsh, "-i '(?<path>[^']+)'") is { Success: true } match)
        {
            var keyFile = match.Groups["path"].Value;
            KeyFiles.Add(keyFile);
            KeysDuringCalls.Add(File.Exists(keyFile) ? File.ReadAllText(keyFile) : "");
        }

        if (fileName == BorgPath && arguments is ["--version"])
            return Result(0, "borg 1.4.1\n");
        if (fileName == "sh" && arguments is ["-c", ..])
        {
            InstalledKeyfiles.Add(standardInput ?? "");
            return Result(0, "");
        }
        if (arguments is ["key", "export", .., var target])
            File.WriteAllText(target, ExportedKey);

        if (arguments[0] == "create")
            return CreateAsync(arguments, standardErrorLine, cancellationToken);
        if (arguments[0] == "extract")
            return ExtractAsync(arguments, workingDirectory, standardErrorLine, cancellationToken);
        if (arguments[0] == "prune")
            return PruneAsync(arguments, standardErrorLine);
        if (arguments[0] == "mount")
            return MountAsync(arguments, cancellationToken);
        if (arguments is ["umount", var mountPoint])
            return Unmount(mountPoint);
        if (arguments[0] == "compact")
            return CompactAsync(standardErrorLine, cancellationToken);

        if (arguments is ["list", ..] && arguments.Contains("--json-lines"))
            return ContentsAsync(arguments, standardOutputLine, cancellationToken);
        if (arguments is ["list", ..])
            return Result(0, "{\"archives\": [" + string.Join(", ", Archives.Select(archive => ArchiveJson(archive, details: false))) + "]}");
        if (arguments is ["info", ..] && arguments.Contains("--last"))
        {
            if (ArchiveDetailsError is not null)
                return Error(ArchiveDetailsError);
            var last = int.Parse(arguments[arguments.ToList().IndexOf("--last") + 1], CultureInfo.InvariantCulture);
            // Like borg 1.4 with --progress: the statistics of each archive, half-way, then finished.
            foreach (var archive in Archives.TakeLast(last))
            {
                standardErrorLine?.Invoke($$"""{"message": "Calculating statistics for archive {{archive.Name}} ...  50%", "current": 5, "total": 10, "info": null, "operation": 1, "msgid": "archive.calc_stats", "type": "progress_percent", "finished": false, "time": 0}""");
                standardErrorLine?.Invoke("""{"operation": 1, "msgid": "archive.calc_stats", "type": "progress_percent", "finished": true, "time": 0}""");
            }
            return Result(0, "{\"archives\": [" + string.Join(", ", Archives.TakeLast(last).Select(archive => ArchiveJson(archive, details: true))) + "]}");
        }

        return arguments[0] switch
        {
            "init" => Result(0, ""),
            "key" => Result(0, ""),
            "info" when EncryptionMode is null => Error(InfoError),
            "info" when EncryptionMode != "none" && CorrectPassphrase is not null && passphrase != CorrectPassphrase
                => Error("PassphraseWrong"),
            "info" => Result(0, $$$"""{"encryption": {"mode": "{{{EncryptionMode}}}"}, "repository": {"id": "repo-id-1"}}"""),
            _ => Result(2, ""),
        };
    }

    /// <summary>An archive as borg 1.4 reports it; the sizes are derived from the position so tests can tell them apart.</summary>
    private string ArchiveJson((string Name, DateTime Start) archive, bool details)
    {
        var index = Archives.IndexOf(archive) + 1;
        var id = ArchiveId(archive.Name);
        var start = archive.Start.ToString("yyyy-MM-ddTHH:mm:ss.ffffff", CultureInfo.InvariantCulture);
        return details
            ? $$$"""{"name": "{{{archive.Name}}}", "id": "{{{id}}}", "start": "{{{start}}}", "duration": {{{index * 10}}}.5, "hostname": "host", "stats": {"original_size": {{{index * 1_000_000}}}, "compressed_size": {{{index * 500_000}}}, "deduplicated_size": {{{index * 1000}}}, "nfiles": {{{index * 100}}}}}"""
            : $$"""{"archive": "{{archive.Name}}", "barchive": "{{archive.Name}}", "name": "{{archive.Name}}", "id": "{{id}}", "start": "{{start}}", "time": "{{start}}"}""";
    }

    /// <summary>Three progress lines, the warnings, then the final statistics.</summary>
    private Task<ProcessResult?> CreateAsync(IReadOnlyList<string> arguments, Action<string>? standardErrorLine, CancellationToken cancellationToken)
    {
        for (var step = 1; step <= 3; step++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            standardErrorLine?.Invoke(
                $$"""{"type": "archive_progress", "original_size": {{step * 1000}}, "compressed_size": {{step * 500}}, "deduplicated_size": {{step * 100}}, "nfiles": {{step * 10}}, "path": "home/me/file{{step}}", "time": 0, "finished": false}""");
            AfterCreateProgress?.Invoke(step);
        }
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var warning in CreateWarnings)
            standardErrorLine?.Invoke($$"""{"type": "log_message", "levelname": "WARNING", "message": "{{warning}}", "msgid": null}""");

        var target = arguments.First(argument => argument.Contains("::", StringComparison.Ordinal));
        var name = target[(target.IndexOf("::", StringComparison.Ordinal) + 2)..].Replace("{hostname}", "host").Replace("{now:%Y-%m-%dT%H:%M:%S}", "2026-11-06T10:00:00");
        Archives.Add((name, new DateTime(2026, 11, 6, 10, 0, 0)));
        return Result(CreateWarnings.Count > 0 ? 1 : 0,
            "{\"archive\": {\"name\": \"" + name + "\", \"duration\": 2.5, \"stats\": {\"original_size\": 3000, \"compressed_size\": 1500, \"deduplicated_size\": 300, \"nfiles\": 30}}}");
    }

    /// <summary>What "list --json-lines" lists per archive name: (type, path, size); every archive has <see cref="DefaultContents"/> otherwise.</summary>
    public Dictionary<string, List<(string Type, string Path, long Size)>> Contents { get; } = [];

    public static readonly List<(string Type, string Path, long Size)> DefaultContents =
    [
        ("d", "home/me/Documents", 0),
        ("-", "home/me/Documents/letter.txt", 1200),
        ("d", "home/me/Documents/Invoices", 0),
        ("-", "home/me/Documents/Invoices/invoice-2026.pdf", 50_000),
        ("l", "home/me/Documents/latest", 0),
    ];

    /// <summary>The entries of the archive (after "::") as borg 1.4 prints them, one JSON line each.</summary>
    /// <summary>When set, "extract" fails with this message id.</summary>
    public string? ExtractError { get; set; }

    /// <summary>Awaited before extracting (e.g. to keep borg busy until cancelled).</summary>
    public Func<CancellationToken, Task>? BeforeExtract { get; set; }

    /// <summary>The working folders "extract" ran in.</summary>
    public List<string?> ExtractFolders { get; } = [];

    /// <summary>
    /// Writes the archive's files below the "pp:" paths into the working folder (dropping --strip-components segments),
    /// each with the text "restored"; reports "Calculating …" first, then bytes.
    /// </summary>
    private async Task<ProcessResult?> ExtractAsync(IReadOnlyList<string> arguments, string? workingDirectory,
        Action<string>? standardErrorLine, CancellationToken cancellationToken)
    {
        ExtractFolders.Add(workingDirectory);
        if (BeforeExtract is not null)
            await BeforeExtract(cancellationToken);
        if (ExtractError is not null)
            return await Error(ExtractError);

        var list = arguments.ToList();
        var strip = list.IndexOf("--strip-components") is var at and >= 0 ? int.Parse(list[at + 1], CultureInfo.InvariantCulture) : 0;
        var target = list.First(argument => argument.Contains("::", StringComparison.Ordinal));
        var name = target[(target.IndexOf("::", StringComparison.Ordinal) + 2)..];
        var prefixes = list.Where(argument => argument.StartsWith("pp:", StringComparison.Ordinal)).Select(argument => argument[3..]).ToList();
        var files = (Contents.GetValueOrDefault(name) ?? DefaultContents)
            .Where(entry => entry.Type == "-")
            .Where(entry => prefixes.Count == 0 || prefixes.Any(prefix => entry.Path == prefix || entry.Path.StartsWith(prefix + "/", StringComparison.Ordinal)))
            .ToList();
        var total = files.Sum(file => file.Size);
        standardErrorLine?.Invoke("""{"message": "Calculating total archive size for the progress indicator (might take long for large archives)", "current": 0, "total": 0, "info": null, "operation": 1, "msgid": "extract", "type": "progress_percent", "finished": false, "time": 0}""");
        long done = 0;
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var segments = file.Path.Split('/').Skip(strip).ToArray();
            var destination = Path.Combine([workingDirectory ?? ".", .. segments]);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.WriteAllText(destination, "restored");
            done += file.Size;
            standardErrorLine?.Invoke(System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["message"] = $"{done * 100.0 / Math.Max(1, total):0.0}% Extracting: {file.Path}", ["current"] = done, ["total"] = total,
                ["info"] = new[] { file.Path }, ["operation"] = 1, ["msgid"] = "extract", ["type"] = "progress_percent", ["finished"] = false,
            }));
        }
        return await Result(0, "");
    }

    /// <summary>The modification time of every entry per archive name (borg's format); "2026-11-05T10:20:30.000000" otherwise.</summary>
    public Dictionary<string, string> ContentsTime { get; } = [];

    /// <summary>When set, "list --json-lines" fails with this message id.</summary>
    public string? ContentsError { get; set; }

    /// <summary>Awaited before listing the contents (e.g. to keep borg "reading" until cancelled).</summary>
    public Func<CancellationToken, Task>? BeforeContents { get; set; }

    private async Task<ProcessResult?> ContentsAsync(IReadOnlyList<string> arguments, Action<string>? standardOutputLine,
        CancellationToken cancellationToken)
    {
        if (BeforeContents is not null)
            await BeforeContents(cancellationToken);
        if (ContentsError is not null)
            return await Error(ContentsError);
        var target = arguments.First(argument => argument.Contains("::", StringComparison.Ordinal));
        var name = target[(target.IndexOf("::", StringComparison.Ordinal) + 2)..];
        var lines = (Contents.GetValueOrDefault(name) ?? DefaultContents).Select(entry => System.Text.Json.JsonSerializer.Serialize(
            new Dictionary<string, object>
            {
                ["type"] = entry.Type, ["mode"] = "-rw-r--r--", ["user"] = "me", ["group"] = "me", ["uid"] = 1000, ["gid"] = 1000,
                ["path"] = entry.Path, ["healthy"] = true, ["source"] = "", ["linktarget"] = entry.Type == "l" ? "letter.txt" : "",
                ["flags"] = 0, ["size"] = entry.Size, ["mtime"] = ContentsTime.GetValueOrDefault(name, "2026-11-05T10:20:30.000000"),
            })).ToList();
        foreach (var line in lines)
            standardOutputLine?.Invoke(line);
        return await Result(0, standardOutputLine is null ? string.Join(Environment.NewLine, lines) : "");
    }

    /// <summary>How many of the newest matching archives "prune" keeps (as "daily #1", "#2", …); the rest go.</summary>
    public int PruneKeep { get; set; } = 1;

    /// <summary>When set, "prune" fails with this message id.</summary>
    public string? PruneError { get; set; }

    /// <summary>When set, "compact" fails with this message id.</summary>
    public string? CompactError { get; set; }

    /// <summary>What "compact" says it freed (borg's decimal units).</summary>
    public string CompactFreed { get; set; } = "1.50 MB";

    /// <summary>Runs when "compact" starts (e.g. to cancel it).</summary>
    public Action? BeforeCompact { get; set; }

    /// <summary>Like borg 1.4: one "--list" line per archive matching the glob ({hostname} = "host"), newest first.</summary>
    private Task<ProcessResult?> PruneAsync(IReadOnlyList<string> arguments, Action<string>? standardErrorLine)
    {
        if (PruneError is not null)
            return Error(PruneError);

        var glob = arguments[arguments.ToList().IndexOf("--glob-archives") + 1].Replace("{hostname}", "host", StringComparison.Ordinal);
        var pattern = new System.Text.RegularExpressions.Regex("^" + System.Text.RegularExpressions.Regex.Escape(glob).Replace(@"\*", ".*", StringComparison.Ordinal) + "$");
        var dryRun = arguments.Contains("--dry-run");
        var matching = Archives.Select((archive, index) => (archive, index)).Where(entry => pattern.IsMatch(entry.archive.Name))
            .OrderByDescending(entry => entry.archive.Start).ThenByDescending(entry => entry.index).Select(entry => entry.archive).ToList();
        var kept = 0;
        var pruned = 0;
        foreach (var archive in matching)
        {
            var keep = kept < PruneKeep;
            var what = keep ? $"Keeping archive (rule: daily #{++kept}):"
                : dryRun ? "Would prune:"
                : $"Pruning archive ({++pruned}/{matching.Count - PruneKeep}):";
            var message = $"{what,-44} {archive.Name,-36} {archive.Start.ToString("ddd, yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} [{ArchiveId(archive.Name)}]";
            standardErrorLine?.Invoke(System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["type"] = "log_message", ["time"] = 0, ["message"] = message, ["levelname"] = "INFO", ["name"] = "borg.output.list",
            }));
            if (!keep && !dryRun)
                Archives.Remove(archive);
        }
        standardErrorLine?.Invoke("""{"operation": 1, "msgid": "prune", "type": "progress_percent", "finished": true, "time": 0}""");
        return Result(0, "");
    }

    /// <summary>Two progress steps, then how much was freed.</summary>
    private Task<ProcessResult?> CompactAsync(Action<string>? standardErrorLine, CancellationToken cancellationToken)
    {
        BeforeCompact?.Invoke();
        cancellationToken.ThrowIfCancellationRequested();
        if (CompactError is not null)
            return Error(CompactError);

        for (var step = 1; step <= 2; step++)
            standardErrorLine?.Invoke(
                $$"""{"message": "Compacting segments  {{step * 50}}%", "current": {{step}}, "total": 2, "info": null, "operation": 1, "msgid": "repository.compact_segments", "type": "progress_percent", "finished": false, "time": 0}""");
        standardErrorLine?.Invoke("""{"operation": 1, "msgid": "repository.compact_segments", "type": "progress_percent", "finished": true, "time": 0}""");
        standardErrorLine?.Invoke(
            $$"""{"type": "log_message", "time": 0, "message": "compaction freed about {{CompactFreed}} repository space.", "levelname": "INFO", "name": "borg.repository"}""");
        return Result(0, "");
    }

    public static string ArchiveId(string name) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(name)));

    public BorgDetector Detector(bool wsl = false) => wsl
        ? new(this, new BorgSearchEnvironment(HostPlatform.Windows, null, @"C:\Users\me", @"C:\Windows\System32",
            path => path == WslExecutable))
        : new(this, new BorgSearchEnvironment(HostPlatform.Linux, "/usr/bin", "/home/me", null, path => path == BorgPath));

    // --- Mounting ---

    /// <summary>Whether /etc/fuse.conf has "user_allow_other" (only asked when borg is in WSL).</summary>
    public bool AllowOther { get; set; } = true;

    /// <summary>Whether "wsl -u root" can add it.</summary>
    public bool CanEnableAllowOther { get; set; } = true;

    /// <summary>What ran as root in WSL.</summary>
    public List<string> RootCommands { get; } = [];

    /// <summary>When set, "mount" fails with this message id (or with this text from FUSE, if it has blanks).</summary>
    public string? MountError { get; set; }

    /// <summary>Mount points a program still uses: "umount" fails there.</summary>
    public HashSet<string> BusyMounts { get; } = [];

    /// <summary>The running "borg mount" calls by mount point; they end when unmounted.</summary>
    public Dictionary<string, TaskCompletionSource<ProcessResult?>> Mounted { get; } = [];

    /// <summary>Whether a running "mount" shows up as mounted (false: borg is still getting ready).</summary>
    public bool MountsReady { get; set; } = true;

    /// <summary>The "-o" options of each "mount".</summary>
    public List<string> MountOptions { get; } = [];

    /// <summary>Mount points detached by force (closing the app).</summary>
    public List<string> ForceUnmounted { get; } = [];

    /// <summary>How often the mounts left behind by a crash were cleaned up.</summary>
    public int MountCleanUps { get; set; }

    /// <summary>Lets the borg mounted at <paramref name="mountPoint"/> stop on its own (e.g. the connection broke).</summary>
    public void EndMount(string mountPoint, string msgid = "ConnectionClosed")
    {
        Mounted.Remove(mountPoint, out var running);
        running?.TrySetResult(new ProcessResult(2, "",
            $$"""{"type": "log_message", "levelname": "ERROR", "message": "borg says no", "msgid": "{{msgid}}"}"""));
    }

    private async Task<ProcessResult?> MountAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        if (MountError is { } error)
            return error.Contains(' ', StringComparison.Ordinal)
                ? new ProcessResult(2, "", error + "\n" + """{"type": "log_message", "levelname": "ERROR", "message": "Runtime Error: FUSE mount failed", "msgid": "RTError"}""")
                : await Error(error);

        var options = arguments.ToList();
        MountOptions.Add(options[options.IndexOf("-o") + 1]);
        var running = new TaskCompletionSource<ProcessResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Mounted[arguments[^1]] = running;
        await using var registration = cancellationToken.Register(() => running.TrySetCanceled(cancellationToken));
        return await running.Task;
    }

    private Task<ProcessResult?> Unmount(string mountPoint)
    {
        if (BusyMounts.Contains(mountPoint) || !Mounted.Remove(mountPoint, out var running))
            return Task.FromResult<ProcessResult?>(new ProcessResult(1, "", "umount: target is busy"));
        running.TrySetResult(new ProcessResult(0, "", ""));
        return Result(0, "");
    }

    private Task<ProcessResult?> MountShell(string script, IReadOnlyList<string> arguments, bool wsl)
    {
        var path = arguments.FirstOrDefault() ?? "";
        if (script.Contains("mkdir -p", StringComparison.Ordinal))
        {
            var mountPoint = "/home/me/" + path;
            var windows = wsl ? "\n" + @"\\wsl.localhost\Ubuntu\home\me\" + path.Replace('/', '\\') : "";
            return Result(0, $"{mountPoint}\n1000\n1000{windows}\n");
        }
        if (script.Contains("/proc/mounts", StringComparison.Ordinal))
        {
            MountCleanUps++;
            return Result(0, "");
        }
        if (script.Contains("fuse.conf", StringComparison.Ordinal))
            return Result(AllowOther ? 0 : 1, "");
        if (script.EndsWith("force_unmount \"$1\"", StringComparison.Ordinal))
        {
            ForceUnmounted.Add(path);
            if (Mounted.Remove(path, out var running))
                running.TrySetResult(new ProcessResult(0, "", ""));
            return Result(0, "");
        }
        if (script.EndsWith("is_mounted \"$1\"", StringComparison.Ordinal))
            return Result(MountsReady && Mounted.ContainsKey(path) ? 0 : 1, "");
        return Result(0, ""); // rmdir
    }

    private static Task<ProcessResult?> Result(int exitCode, string output) =>
        Task.FromResult<ProcessResult?>(new ProcessResult(exitCode, output, ""));

    private static Task<ProcessResult?> Error(string msgid) =>
        Task.FromResult<ProcessResult?>(new ProcessResult(2, "",
            $$"""{"type": "log_message", "levelname": "ERROR", "message": "borg says no", "msgid": "{{msgid}}"}"""));
}

/// <summary>Plays an SSH server: presents a host key and records password logins.</summary>
public sealed class FakeSsh : ISshService
{
    public static readonly SshHostKey ServerKey = new(HostKeyBlob("ssh-ed25519", 1));

    public SshHostKey PresentedKey { get; set; } = ServerKey;

    public SshOperationException? LoginFailure { get; set; }

    /// <summary>Commands run after a password login.</summary>
    public List<(SshEndpoint Endpoint, string Password, string Command, string? StandardInput)> Logins { get; } = [];

    /// <summary>Lines appended to authorized_keys over SFTP after a password login.</summary>
    public List<(SshEndpoint Endpoint, string Password, string Line)> SftpAppends { get; } = [];

    public int HostKeyRequests { get; private set; }

    public Task<SshHostKey> GetHostKeyAsync(string host, int port, CancellationToken cancellationToken = default)
    {
        HostKeyRequests++;
        return Task.FromResult(PresentedKey);
    }

    public Task RunWithPasswordAsync(SshEndpoint endpoint, string password, SshHostKey trustedHostKey, string command,
        string? standardInput = null, CancellationToken cancellationToken = default)
    {
        if (LoginFailure is not null)
            throw LoginFailure;
        Logins.Add((endpoint, password, command, standardInput));
        return Task.CompletedTask;
    }

    public Task AppendAuthorizedKeyAsync(SshEndpoint endpoint, string password, SshHostKey trustedHostKey, string line,
        CancellationToken cancellationToken = default)
    {
        if (LoginFailure is not null)
            throw LoginFailure;
        SftpAppends.Add((endpoint, password, line));
        return Task.CompletedTask;
    }

    /// <summary>A syntactically valid host key blob ("type" + 32 bytes of key material).</summary>
    public static byte[] HostKeyBlob(string type, byte fill)
    {
        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        return [0, 0, 0, (byte)typeBytes.Length, .. typeBytes, 0, 0, 0, 32, .. Enumerable.Repeat(fill, 32)];
    }
}

/// <summary>All services of the app wired to fakes, in a temporary directory.</summary>
public sealed class TestServices : IDisposable
{
    public TemporaryDirectory Directory { get; } = new();
    public FakeBorg Borg { get; } = new();
    public FakeSecretStore Secrets { get; } = new();
    public FakeSsh Ssh { get; } = new();
    public FakeDialogs Dialogs { get; } = new();
    public RepositoryStore Store { get; }
    public SshKeyStore SshKeys { get; }
    public SshKeygen SshKeygen { get; } = SshKeygen.CreateDefault();
    public ManualTime Time { get; } = new();
    public Vault Vault { get; }
    public SettingsStore Settings { get; }
    public BackupJobStore Jobs { get; }
    public ArchiveCache Archives { get; }
    public DurationHistory Durations { get; }
    public AppServices Services { get; }

    /// <summary>Cheap key derivation: the tests check the logic, not the cost.</summary>
    public static readonly KdfParameters FastKdf = new(64, 1, 1);

    public TestServices(bool keychainAvailable = true, bool wsl = false)
    {
        if (!keychainAvailable)
            Secrets = new FakeSecretStore(available: false);
        Store = new RepositoryStore(Directory.Combine("repositories.json"));
        SshKeys = new SshKeyStore(Directory.Combine("ssh"));
        Vault = new Vault(Directory.Combine("vault.json"), FastKdf, Time);
        Settings = new SettingsStore(Directory.Combine("settings.json"));
        Jobs = new BackupJobStore(Directory.Combine("jobs.json"));
        var plugins = PluginCatalog.Load([new LocalProviderPlugin(), new SshProviderPlugin(), new HetznerProviderPlugin()], "no-plugins-here");
        Archives = new ArchiveCache(Directory.Combine("archives"), Vault);
        Durations = new DurationHistory(Directory.Combine("durations.json"));
        Services = new AppServices(Borg.Detector(wsl), plugins, Store, Secrets,
            new BorgClient(Borg, FakeBorg.WslExecutable) { NativeIsPosix = true }, Ssh, SshKeys, SshKeygen, Vault, Settings, Jobs, Archives, Durations, Dialogs);
    }

    public KnownHostsFile KnownHosts => new(SshKeys.KnownHostsFile);

    public void Dispose() => Directory.Dispose();
}

public sealed class FakeSecretStore(bool available = true) : ISecretStore
{
    public Dictionary<string, string> Secrets { get; } = [];

    public bool IsAvailable => available;

    public string? Get(string key) => Secrets.GetValueOrDefault(key);

    public void Set(string key, string label, string secret) => Secrets[key] = secret;

    public void Delete(string key) => Secrets.Remove(key);
}

/// <summary>Answers dialogs as configured; the editor is "filled in" by <see cref="Editor"/>.</summary>
public sealed class FakeDialogs : IDialogService
{
    public Func<RepositoryEditorViewModel, Task>? Editor { get; set; }

    public Queue<bool> ConfirmAnswers { get; } = [];

    public Queue<string?> Passphrases { get; } = [];

    /// <summary>Answers to "enter your PIN" (none left: cancelled).</summary>
    public Queue<string?> Pins { get; } = [];

    /// <summary>Answers to "set up a PIN" (none left: cancelled).</summary>
    public Queue<string?> NewPins { get; } = [];

    /// <summary>Error texts shown with the PIN prompts.</summary>
    public List<string?> PinErrors { get; } = [];

    /// <summary>Answers to other secret prompts (key passwords); none left: cancelled.</summary>
    public Queue<string?> Secrets { get; } = [];

    /// <summary>Error texts shown with the secret prompts.</summary>
    public List<string?> SecretErrors { get; } = [];

    /// <summary>The file "chosen" in the open dialog.</summary>
    public string? OpenFile { get; set; }

    public string? Clipboard { get; private set; }

    public string? SaveFile { get; set; }

    /// <summary>Titles of the dialogs shown.</summary>
    public List<string> Shown { get; } = [];

    /// <summary>Messages of the dialogs shown.</summary>
    public List<string> Messages { get; } = [];

    public Task<bool> ConfirmAsync(string title, string message, string confirmText, string? cancelText = null)
    {
        Shown.Add(title);
        Messages.Add(message);
        return Task.FromResult(ConfirmAnswers.Count > 0 && ConfirmAnswers.Dequeue());
    }

    public Task ShowMessageAsync(string title, string message)
    {
        Shown.Add(title);
        Messages.Add(message);
        return Task.CompletedTask;
    }

    public Task<string?> AskPassphraseAsync(string repositoryName) =>
        Task.FromResult(Passphrases.Count > 0 ? Passphrases.Dequeue() : null);

    public Task<string?> AskPinAsync(string? error = null)
    {
        PinErrors.Add(error);
        return Task.FromResult(Pins.Count > 0 ? Pins.Dequeue() : null);
    }

    public Task<string?> AskNewPinAsync() => Task.FromResult(NewPins.Count > 0 ? NewPins.Dequeue() : null);

    public Task<string?> AskSecretAsync(string title, string prompt, string? error = null)
    {
        SecretErrors.Add(error);
        return Task.FromResult(Secrets.Count > 0 ? Secrets.Dequeue() : null);
    }

    public Task<string?> PickSaveFileAsync(string title, string suggestedFileName, string? defaultExtension = "txt") =>
        Task.FromResult(SaveFile);

    public Task<string?> PickOpenFileAsync(string title) => Task.FromResult(OpenFile);

    /// <summary>Folders "chosen" in the folder dialog, in order (none left: cancelled).</summary>
    public Queue<string?> Folders { get; } = [];

    public Task<string?> PickFolderAsync(string title) => Task.FromResult(Folders.Count > 0 ? Folders.Dequeue() : null);

    /// <summary>Fills in the backup job editor.</summary>
    public Func<BackupJobEditorViewModel, Task>? JobEditor { get; set; }

    public Task ShowBackupJobEditorAsync(BackupJobEditorViewModel editor) => JobEditor?.Invoke(editor) ?? Task.CompletedTask;

    /// <summary>The clean-up previews shown, in order.</summary>
    public List<PrunePreviewViewModel> PrunePreviews { get; } = [];

    /// <summary>Whether to confirm the clean-up previews (none left: cancelled).</summary>
    public Queue<bool> PruneAnswers { get; } = [];

    /// <summary>What to do while the archive browser is "open" (it closes when this completes).</summary>
    public Func<ArchiveBrowserViewModel, Task>? Browser { get; set; }

    public List<ArchiveBrowserViewModel> Browsers { get; } = [];

    /// <summary>What to do while the restore dialog is "open" (it closes when this completes).</summary>
    public Func<RestoreViewModel, Task>? Restore { get; set; }

    public List<RestoreViewModel> Restores { get; } = [];

    public Task ShowRestoreAsync(RestoreViewModel restore)
    {
        Restores.Add(restore);
        return Restore?.Invoke(restore) ?? Task.CompletedTask;
    }

    public Task ShowArchiveBrowserAsync(ArchiveBrowserViewModel browser)
    {
        Browsers.Add(browser);
        return Browser?.Invoke(browser) ?? Task.CompletedTask;
    }

    /// <summary>What to do while the search over all archives is "open" (it closes when this completes).</summary>
    public Func<ArchiveSearchViewModel, Task>? Search { get; set; }

    public List<ArchiveSearchViewModel> Searches { get; } = [];

    public Task ShowArchiveSearchAsync(ArchiveSearchViewModel search)
    {
        Searches.Add(search);
        search.Dialogs = this;
        return Search?.Invoke(search) ?? Task.CompletedTask;
    }

    /// <summary>What to do while a versions window is "open"; like the window, opening starts looking through the archives.</summary>
    public Func<FileVersionsViewModel, Task>? Versions { get; set; }

    public List<FileVersionsViewModel> VersionWindows { get; } = [];

    public async Task ShowFileVersionsAsync(FileVersionsViewModel versions)
    {
        VersionWindows.Add(versions);
        versions.Dialogs = this;
        await versions.Begin();
        if (Versions is not null)
            await Versions(versions);
    }

    public Task<bool> ConfirmPruneAsync(PrunePreviewViewModel preview)
    {
        PrunePreviews.Add(preview);
        return Task.FromResult(PruneAnswers.Count > 0 && PruneAnswers.Dequeue());
    }

    /// <summary>Folders opened in the file manager.</summary>
    public List<string> OpenedFolders { get; } = [];

    public Task OpenFolderAsync(string path)
    {
        OpenedFolders.Add(path);
        return Task.CompletedTask;
    }

    public Task CopyToClipboardAsync(string text)
    {
        Clipboard = text;
        return Task.CompletedTask;
    }

    public Task ShowRepositoryEditorAsync(RepositoryEditorViewModel editor) => Editor?.Invoke(editor) ?? Task.CompletedTask;
}

/// <summary>A clock that only moves when told to.</summary>
public sealed class ManualTime : TimeProvider
{
    private DateTimeOffset _now = new(2026, 11, 5, 9, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan time) => _now += time;
}
