using System.Globalization;
using BorgStudio.App.Services;
using BorgStudio.App.ViewModels;
using BorgStudio.Core.Backups;
using BorgStudio.Core.Borg;
using BorgStudio.Core.Notifications;
using BorgStudio.Core.Plugins;
using BorgStudio.Core.Processes;
using BorgStudio.Core.Progress;
using BorgStudio.Core.Repositories;
using BorgStudio.Core.Secrets;
using BorgStudio.Core.Security;
using BorgStudio.Core.Settings;
using BorgStudio.Core.Updates;
using BorgStudio.Core.Ssh;
using BorgStudio.Plugins;
using BorgStudio.Providers.BorgBase;
using BorgStudio.Providers.Hetzner;
using BorgStudio.Providers.Local;
using BorgStudio.Providers.NetworkDisk;
using BorgStudio.Providers.RsyncNet;
using BorgStudio.Providers.Sftp;
using BorgStudio.Providers.Ssh;
using BorgStudio.Providers.Usb;

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

    /// <summary>The folder each call (of <see cref="RunAsync"/>) started in, in the order of <see cref="Calls"/>.</summary>
    public List<string?> WorkingDirectories { get; } = [];

    /// <summary>Content of the private key file ssh would have used, read during each SSH call.</summary>
    public List<string> KeysDuringCalls { get; } = [];

    /// <summary>The key files ssh was given (to check they are gone afterwards).</summary>
    public List<string> KeyFiles { get; } = [];

    /// <summary>Encryption mode reported by "info"; null makes "info" fail with <see cref="InfoError"/>.</summary>
    public string? EncryptionMode { get; set; } = "repokey-blake2";

    public string InfoError { get; set; } = "Repository.DoesNotExist";

    /// <summary>What "info" reports the repository takes up (unique compressed size).</summary>
    public long RepositorySize { get; set; } = 300L * 1024 * 1024 * 1024;

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
            // Mounting Windows drives inside WSL: says which of the drives it mounted.
            if (arguments is ["-u", "root", "-e", "sh", "-c", var driveScript] && driveScript.Contains("/mnt/$d", StringComparison.Ordinal))
            {
                RootCommands.Add(driveScript);
                var letters = System.Text.RegularExpressions.Regex.Match(driveScript, "for d in ([a-z ]+);").Groups[1].Value
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries);
                return Result(0, string.Join('\n', letters.Where(letter => WslDrivesMissing.Contains(letter[0]))));
            }
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

        // sshfs in the foreground: "runs" until its folder is unmounted.
        if (fileName == "sshfs")
            return RunSshfs(arguments, cancellationToken);

        // borg on an SFTP folder: the guard lets it start only on a mounted folder (inside the pid wrapper, or on its own).
        if (fileName == "sh" && arguments is ["-c", _, "sh", "-c", var wrapped, ..] && wrapped.Contains("BORGSTUDIO_NOT_MOUNTED", StringComparison.Ordinal))
            arguments = arguments.Skip(2).ToList();
        if (fileName == "sh" && arguments is ["-c", var guard, var guarded, ..] && guard.Contains("BORGSTUDIO_NOT_MOUNTED", StringComparison.Ordinal))
        {
            GuardedMounts.Add(guarded);
            if (!SftpMounts.ContainsKey(guarded) || SftpVanished)
                return Task.FromResult<ProcessResult?>(new ProcessResult(97, "", "BORGSTUDIO_NOT_MOUNTED\n"));
            fileName = arguments[3];
            arguments = arguments.Skip(4).ToList();
        }

        // The shell scripts around mounting.
        if (fileName == "sh" && arguments is ["-c", var script, "sh", ..])
            return MountShell(script, arguments.Skip(3).ToList(), wsl);

        // Low priority: borg runs under "nice -n 10" (inside the sh wrapper, or on its own).
        if (fileName == "sh" && arguments is ["-c", _, "nice", "-n", _, BorgPath, ..])
        {
            NiceCalls.Add(arguments[6]);
            arguments = [arguments[0], arguments[1], .. arguments.Skip(5)];
        }
        else if (fileName == "nice" && arguments is ["-n", _, BorgPath, ..])
        {
            NiceCalls.Add(arguments[3]);
            fileName = BorgPath;
            arguments = arguments.Skip(3).ToList();
        }

        // Interruptible calls run borg through a small sh wrapper (on Linux/macOS).
        if (fileName == "sh" && arguments is ["-c", _, BorgPath, ..])
        {
            fileName = BorgPath;
            arguments = arguments.Skip(3).ToList();
        }

        var passphrase = environment?.GetValueOrDefault("BORG_PASSPHRASE");
        Environments.Add(environment ?? new Dictionary<string, string?>());
        Calls.Add((arguments, passphrase));
        WorkingDirectories.Add(workingDirectory);
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
        if (arguments[0] == "check")
            return CheckAsync(arguments, environment, standardErrorLine, cancellationToken);
        if (arguments[0] == "diff")
            return DiffAsync(standardOutputLine, cancellationToken);
        if (arguments[0] is "rename" or "delete" or "recreate")
            return ChangeArchives(arguments);
        if (arguments[0] == "export-tar")
            return ExportTarAsync(arguments, standardErrorLine, cancellationToken);

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
            "break-lock" when BreakLockError is not null => Error(BreakLockError),
            "break-lock" => Result(0, ""),
            "key" => Result(0, ""),
            "info" when EncryptionMode is null => Error(InfoError),
            "info" when EncryptionMode != "none" && CorrectPassphrase is not null && passphrase != CorrectPassphrase
                => Error("PassphraseWrong"),
            "info" => Result(0, $$$"""{"encryption": {"mode": "{{{EncryptionMode}}}"}, "repository": {"id": "repo-id-1"}, "cache": {"stats": {"unique_csize": """
                + RepositorySize.ToString(CultureInfo.InvariantCulture) + "}}}"),
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
            : $$"""{"archive": "{{archive.Name}}", "barchive": "{{archive.Name}}", "name": "{{archive.Name}}", "id": "{{id}}", "start": "{{start}}", "time": "{{start}}", "comment": "{{Comments.GetValueOrDefault(archive.Name, "")}}"}""";
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
        var dryRun = list.Contains("--dry-run");
        standardErrorLine?.Invoke("""{"message": "Calculating total archive size for the progress indicator (might take long for large archives)", "current": 0, "total": 0, "info": null, "operation": 1, "msgid": "extract", "type": "progress_percent", "finished": false, "time": 0}""");
        long done = 0;
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var segments = file.Path.Split('/').Skip(strip).ToArray();
            var destination = Path.Combine([workingDirectory ?? ".", .. segments]);
            if (!dryRun)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                if (ExtractedContent is { } content)
                    File.WriteAllBytes(destination, content(file.Path, file.Size));
                else
                    File.WriteAllText(destination, "restored");
            }
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

    /// <summary>Where "export-tar -" writes its tar stream (set while <see cref="RunStreamingAsync"/> runs).</summary>
    private Func<Stream, CancellationToken, Task>? _output;

    public async Task<ProcessResult?> RunStreamingAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout,
        Func<Stream, CancellationToken, Task> readOutput, IReadOnlyDictionary<string, string?>? environment = null,
        CancellationToken cancellationToken = default, Action<string>? standardErrorLine = null, string? workingDirectory = null)
    {
        _output = readOutput;
        try
        {
            return await RunAsync(fileName, arguments, timeout, environment, null, cancellationToken, standardErrorLine, null, workingDirectory);
        }
        finally
        {
            _output = null;
        }
    }

    /// <summary>When set, "export-tar" fails with this message (as borg's error), e.g. a missing zstd.</summary>
    public string? ExportError { get; set; }

    /// <summary>Called after the progress of "export-tar" (e.g. to cancel in the middle).</summary>
    public Action? DuringExport { get; set; }

    /// <summary>
    /// "export-tar": the archive's <see cref="Contents"/> (only the paths asked for, with --strip-components) as a tar – to
    /// the stream for "-", into the file otherwise. Files hold their size in bytes of 'x'.
    /// </summary>
    private async Task<ProcessResult?> ExportTarAsync(IReadOnlyList<string> arguments, Action<string>? standardErrorLine,
        CancellationToken cancellationToken)
    {
        if (ExportError is not null)
            return new ProcessResult(2, "", $$"""{"type": "log_message", "levelname": "ERROR", "message": "{{ExportError}}", "msgid": "Error"}""");
        var list = arguments.ToList();
        var target = list.First(argument => argument.Contains("::", StringComparison.Ordinal));
        var name = target[(target.IndexOf("::", StringComparison.Ordinal) + 2)..];
        var file = list[list.IndexOf(target) + 1];
        var strip = list.IndexOf("--strip-components") is var at and >= 0 ? int.Parse(list[at + 1], CultureInfo.InvariantCulture) : 0;
        var prefixes = list.Where(argument => argument.StartsWith("pp:", StringComparison.Ordinal)).Select(argument => argument[3..]).ToList();
        var entries = (Contents.GetValueOrDefault(name) ?? DefaultContents)
            .Where(entry => prefixes.Count == 0 || prefixes.Any(prefix => entry.Path == prefix || entry.Path.StartsWith(prefix + "/", StringComparison.Ordinal)))
            .ToList();
        standardErrorLine?.Invoke("""{"message": "Calculating size", "current": 0, "total": 0, "info": null, "operation": 1, "msgid": "extract", "type": "progress_percent", "finished": false, "time": 0}""");
        standardErrorLine?.Invoke(System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["message"] = "50% Processing", ["current"] = 1, ["total"] = 2, ["info"] = new[] { entries.FirstOrDefault().Path ?? "" },
            ["operation"] = 1, ["msgid"] = "extract", ["type"] = "progress_percent", ["finished"] = false, ["time"] = 0,
        }));
        DuringExport?.Invoke();
        cancellationToken.ThrowIfCancellationRequested();

        var tar = new MemoryStream();
        await using (var writer = new System.Formats.Tar.TarWriter(tar, System.Formats.Tar.TarEntryFormat.Pax, leaveOpen: true))
        {
            foreach (var (type, path, size) in entries)
            {
                var stripped = string.Join('/', path.Split('/').Skip(strip));
                if (stripped.Length == 0)
                    continue;
                System.Formats.Tar.PaxTarEntry entry = type switch
                {
                    "d" => new(System.Formats.Tar.TarEntryType.Directory, stripped + "/"),
                    "l" => new(System.Formats.Tar.TarEntryType.SymbolicLink, stripped) { LinkName = "letter.txt" },
                    _ => new(System.Formats.Tar.TarEntryType.RegularFile, stripped) { DataStream = new MemoryStream(Enumerable.Repeat((byte)'x', (int)size).ToArray()) },
                };
                entry.ModificationTime = new DateTimeOffset(2026, 11, 5, 10, 20, 30, TimeSpan.Zero);
                await writer.WriteEntryAsync(entry, cancellationToken);
            }
        }
        tar.Position = 0;
        if (file == "-")
            await _output!(tar, cancellationToken);
        else
            await File.WriteAllBytesAsync(file, tar.ToArray(), cancellationToken);
        return new ProcessResult(0, "", "");
    }

    /// <summary>The archives' comments ("recreate --comment" sets them, "list" reports them).</summary>
    public Dictionary<string, string> Comments { get; } = [];

    /// <summary>When set, "rename", "delete" and "recreate" fail with this message id.</summary>
    public string? ChangeError { get; set; }

    /// <summary>"rename", "delete" (several archives at once) and "recreate --comment" on <see cref="Archives"/>.</summary>
    private Task<ProcessResult?> ChangeArchives(IReadOnlyList<string> arguments)
    {
        if (ChangeError is not null)
            return Error(ChangeError);
        var target = arguments.First(argument => argument.Contains("::", StringComparison.Ordinal));
        var first = target[(target.IndexOf("::", StringComparison.Ordinal) + 2)..];
        var rest = arguments.SkipWhile(argument => argument != target).Skip(1).ToList();
        int Index(string name) => Archives.FindIndex(archive => archive.Name == name);
        switch (arguments[0])
        {
            case "rename":
                if (Index(rest[0]) >= 0)
                    return Error("Archive.AlreadyExists");
                if (Index(first) is var index and >= 0)
                    Archives[index] = (rest[0], Archives[index].Start);
                if (Comments.Remove(first, out var comment))
                    Comments[rest[0]] = comment;
                break;
            case "delete":
                foreach (var name in rest.Prepend(first))
                    Archives.RemoveAll(archive => archive.Name == name);
                break;
            default:
                var text = arguments[arguments.ToList().IndexOf("--comment") + 1];
                if (text.Length == 0)
                    Comments.Remove(first);
                else
                    Comments[first] = text;
                break;
        }
        return Result(0, "");
    }

    /// <summary>What "diff" prints (JSON lines, as borg 1.4 does).</summary>
    public List<string> DiffLines { get; } = [];

    /// <summary>When set, "diff" fails with this message id.</summary>
    public string? DiffError { get; set; }

    /// <summary>Awaited before "diff" prints anything (e.g. to close the window meanwhile).</summary>
    public Func<CancellationToken, Task>? BeforeDiff { get; set; }

    private async Task<ProcessResult?> DiffAsync(Action<string>? standardOutputLine, CancellationToken cancellationToken)
    {
        if (BeforeDiff is not null)
            await BeforeDiff(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (DiffError is not null)
            return await Error(DiffError);
        foreach (var line in DiffLines)
            standardOutputLine?.Invoke(line);
        return await Result(0, "");
    }

    /// <summary>The borg commands that ran under nice (low priority).</summary>
    public List<string> NiceCalls { get; } = [];

    /// <summary>When set, "break-lock" fails with this message id.</summary>
    public string? BreakLockError { get; set; }

    /// <summary>What "extract" writes for a file (path in the archive, size); unset: the text "restored".</summary>
    public Func<string, long, byte[]>? ExtractedContent { get; set; }

    /// <summary>The damage "check" finds (logged as errors, exit code 1); "check --repair" repairs it.</summary>
    public List<string> CheckProblems { get; } = [];

    /// <summary>When set, "check" fails with this message id.</summary>
    public string? CheckError { get; set; }

    /// <summary>Called after the first progress line of "check" (e.g. to cancel in the middle).</summary>
    public Action? DuringCheck { get; set; }

    /// <summary>Like borg 1.4: segments, (verify-data: data,) archives – each half-way, then finished.</summary>
    private Task<ProcessResult?> CheckAsync(IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string?>? environment,
        Action<string>? standardErrorLine, CancellationToken cancellationToken)
    {
        if (CheckError is not null)
            return Error(CheckError);
        var repair = arguments.Contains("--repair");
        if (repair && environment?.GetValueOrDefault("BORG_CHECK_I_KNOW_WHAT_I_AM_DOING") != "YES")
            return Error("CancelledByUser");

        List<string> phases = ["repository.check", .. arguments.Contains("--verify-data") ? ["check.verify_data"] : Array.Empty<string>(),
            "check.rebuild_refcounts"];
        foreach (var phase in phases)
        {
            standardErrorLine?.Invoke(
                $$"""{"message": "Checking 50.0%", "current": 1, "total": 2, "info": null, "operation": 1, "msgid": "{{phase}}", "type": "progress_percent", "finished": false, "time": 0}""");
            if (phase == phases[0])
                DuringCheck?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            standardErrorLine?.Invoke($$"""{"operation": 1, "msgid": "{{phase}}", "type": "progress_percent", "finished": true, "time": 0}""");
        }
        foreach (var problem in CheckProblems)
            standardErrorLine?.Invoke($$"""{"type": "log_message", "time": 0, "message": "{{problem}}", "levelname": "ERROR", "name": "borg.repository"}""");
        var found = CheckProblems.Count > 0;
        if (repair)
            CheckProblems.Clear();
        return Result(found ? 1 : 0, "");
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

    /// <summary>Windows drives WSL has not mounted (attached after it started).</summary>
    public HashSet<char> WslDrivesMissing { get; } = [];

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

    // --- sshfs (SFTP folders) ---

    /// <summary>SFTP folders mounted now (mount point → sshfs "running" until unmounted).</summary>
    public Dictionary<string, TaskCompletionSource<ProcessResult?>> SftpMounts { get; } = [];

    public List<IReadOnlyList<string>> SshfsCalls { get; } = [];

    public List<string> SftpUnmounts { get; } = [];

    /// <summary>Mount points borg's guard looked at before borg started.</summary>
    public List<string> GuardedMounts { get; } = [];

    public bool SshfsMissing { get; set; }

    /// <summary>sshfs fails with this on standard error.</summary>
    public string? SshfsError { get; set; }

    /// <summary>The folder is gone while sshfs still runs (the guard must stop borg).</summary>
    public bool SftpVanished { get; set; }

    private Task<ProcessResult?> RunSshfs(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        SshfsCalls.Add(arguments);
        if (SshfsError is { } error)
            return Task.FromResult<ProcessResult?>(new ProcessResult(1, "", error));
        var running = new TaskCompletionSource<ProcessResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        SftpMounts[arguments[^1]] = running;
        return running.Task.WaitAsync(cancellationToken);
    }

    private Task<ProcessResult?> MountShell(string script, IReadOnlyList<string> arguments, bool wsl)
    {
        var path = arguments.FirstOrDefault() ?? "";
        if (script.Contains(".borgstudio/sftp", StringComparison.Ordinal))
            return SshfsMissing ? Result(3, "BORGSTUDIO_SSHFS_MISSING\n") : Result(0, $"READY /home/me/.borgstudio/sftp/{path}\n");
        if (script.StartsWith("fusermount -u \"$1\"", StringComparison.Ordinal))
        {
            SftpUnmounts.Add(path);
            if (SftpMounts.Remove(path, out var sshfs))
                sshfs.TrySetResult(new ProcessResult(0, "", ""));
            return Result(0, "");
        }
        if (script.EndsWith("is_mounted \"$1\"", StringComparison.Ordinal) && SftpMounts.ContainsKey(path))
            return Result(0, "");
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
    public FakeUpdateSource UpdateSource { get; } = new();
    public FakeSystemScheduler Scheduler { get; } = new();
    public FakeAutostart Autostart { get; } = new();
    public Updater Updates { get; }

    /// <summary>The programs system notifications start (notify-send: the notifier pretends to be on Linux).</summary>
    public RecordingProcesses NotifyProcesses { get; } = new();

    /// <summary>What the conditions of scheduled runs find (battery, connection, target).</summary>
    public FakeSystemState SystemState { get; } = new();

    /// <summary>What the jobs' commands start (nothing really runs; results by program name).</summary>
    public RecordingProcesses HookProcesses { get; } = new();

    /// <summary>What webhooks post.</summary>
    public FakeHttpHandler Http { get; } = new();

    public Notifier Notifier { get; }

    /// <summary>The "installed" app the updater replaces.</summary>
    public string AppFolder => Directory.Combine("app");

    /// <summary>Cheap key derivation: the tests check the logic, not the cost.</summary>
    public static readonly KdfParameters FastKdf = new(64, 1, 1);

    /// <summary>Network disks: logging in attaches <see cref="FakeNetworkDisks.Volume"/> to <see cref="Drives"/>.</summary>
    public FakeNetworkDisks NetworkDisks { get; }

    /// <summary>BorgBase's GraphQL API.</summary>
    public FakeBorgBase BorgBaseApi { get; } = new();

    /// <summary>The drives the USB provider searches and ejects.</summary>
    public FakeDrives Drives { get; } = new();

    public TestServices(bool keychainAvailable = true, bool wsl = false)
    {
        NetworkDisks = new FakeNetworkDisks(Drives);
        if (!keychainAvailable)
            Secrets = new FakeSecretStore(available: false);
        Store = new RepositoryStore(Directory.Combine("repositories.json"));
        SshKeys = new SshKeyStore(Directory.Combine("ssh"));
        Vault = new Vault(Directory.Combine("vault.json"), FastKdf, Time);
        Settings = new SettingsStore(Directory.Combine("settings.json"));
        Jobs = new BackupJobStore(Directory.Combine("jobs.json"));
        var plugins = PluginCatalog.Load([new LocalProviderPlugin(), new UsbProviderPlugin(Drives), new NetworkDiskProviderPlugin(NetworkDisks, Drives), new SshProviderPlugin(), new SftpProviderPlugin(), new HetznerProviderPlugin(), new RsyncNetProviderPlugin(), new BorgBaseProviderPlugin(BorgBaseApi)], "no-plugins-here");
        Archives = new ArchiveCache(Directory.Combine("archives"), Vault);
        Durations = new DurationHistory(Directory.Combine("durations.json"));
        System.IO.Directory.CreateDirectory(AppFolder);
        File.WriteAllText(Path.Combine(AppFolder, FakeUpdateSource.Executable), "old");
        Updates = new Updater(UpdateSource, "0.2.0", new InstallLocation(AppFolder, FakeUpdateSource.Executable), Directory.Combine("updates"))
        {
            Rid = "win-x64",
        };
        Notifier = new Notifier(new SystemNotifier(NotifyProcesses) { Platform = SystemNotifier.OSPlatform.Linux }, new EmailSender(),
            new WebhookSender(new HttpClient(Http)));
        Services = new AppServices(Borg.Detector(wsl), plugins, Store, Secrets,
            new BorgClient(Borg, FakeBorg.WslExecutable) { NativeIsPosix = true }, Ssh, SshKeys, SshKeygen, Vault, Settings, Jobs, Archives, Durations, Dialogs,
            Updates,
            Scheduler,
            Autostart,
            Notifier,
            new HookRunner(HookProcesses, FakeBorg.WslExecutable),
            SystemState);
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

    public Task ShowMessageAsync(string title, string message, string? buttonText = null)
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

    /// <summary>Answers to text prompts (names, comments); none left: cancelled.</summary>
    public Queue<string?> TextAnswers { get; } = [];

    /// <summary>What the text prompts were filled in with.</summary>
    public List<string> TextInitials { get; } = [];

    public Task<string?> AskTextAsync(string title, string prompt, string initial)
    {
        Shown.Add(title);
        TextInitials.Add(initial);
        return Task.FromResult(TextAnswers.Count > 0 ? TextAnswers.Dequeue() : null);
    }

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

    /// <summary>The file "chosen" as the export's target; <c>null</c>: cancelled.</summary>
    public string? ExportFile { get; set; }

    public List<ArchiveExportViewModel> Exports { get; } = [];

    public Task<string?> PickArchiveExportFileAsync(string title, string suggestedName)
    {
        Shown.Add(title);
        TextInitials.Add(suggestedName);
        return Task.FromResult(ExportFile);
    }

    /// <summary>Public keys shown to add by hand; answered with <see cref="PublicKeyAdded"/>.</summary>
    public List<(string Instructions, string PublicKey)> PublicKeys { get; } = [];

    public bool PublicKeyAdded { get; set; } = true;

    public Task<bool> ShowPublicKeyAsync(string title, string instructions, string publicKey)
    {
        PublicKeys.Add((instructions, publicKey));
        return Task.FromResult(PublicKeyAdded);
    }

    /// <summary>The questions asked on top (title, message); answered with <see cref="OnTopAnswer"/>.</summary>
    public List<(string Title, string Message)> OnTop { get; } = [];

    public bool OnTopAnswer { get; set; } = true;

    /// <summary>Set: the question stays open until this completes (or it is taken away).</summary>
    public Task? OnTopOpenUntil { get; set; }

    public async Task<bool> AskOnTopAsync(string title, string message, string confirmText, string cancelText, CancellationToken cancellationToken)
    {
        OnTop.Add((title, message));
        if (OnTopOpenUntil is { } open)
        {
            try
            {
                await open.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }
        return OnTopAnswer;
    }

    /// <summary>Like the window: the export starts when it opens; "open" until it has finished.</summary>
    public async Task ShowArchiveExportAsync(ArchiveExportViewModel export)
    {
        Exports.Add(export);
        export.Dialogs = this;
        await export.Begin();
    }

    /// <summary>The comparison choices "shown"; <see cref="PickCompare"/> may change the choice (default: as preset), <c>false</c> cancels.</summary>
    public List<ArchiveComparePickerViewModel> ComparePickers { get; } = [];

    public Func<ArchiveComparePickerViewModel, bool>? PickCompare { get; set; }

    public Task<bool> PickArchivesToCompareAsync(ArchiveComparePickerViewModel picker)
    {
        ComparePickers.Add(picker);
        return Task.FromResult(PickCompare?.Invoke(picker) ?? true);
    }

    /// <summary>The comparisons "shown"; <see cref="Diff"/> runs while one is open (default: until borg is done).</summary>
    public List<ArchiveDiffViewModel> Diffs { get; } = [];

    public Func<ArchiveDiffViewModel, Task>? Diff { get; set; }

    public async Task ShowArchiveDiffAsync(ArchiveDiffViewModel diff)
    {
        Diffs.Add(diff);
        if (Diff is not null)
            await Diff(diff);
        else
            while (diff.IsLoading)
                await Task.Delay(10);
    }

    public Task<bool> ConfirmPruneAsync(PrunePreviewViewModel preview)
    {
        PrunePreviews.Add(preview);
        return Task.FromResult(PruneAnswers.Count > 0 && PruneAnswers.Dequeue());
    }

    /// <summary>Folders opened in the file manager.</summary>
    public List<string> OpenedFolders { get; } = [];

    public int Restarts { get; private set; }

    /// <summary>What a restart would have started instead of the app.</summary>
    public List<System.Diagnostics.ProcessStartInfo> RestartsWith { get; } = [];

    public void RestartApp(System.Diagnostics.ProcessStartInfo? instead = null)
    {
        Restarts++;
        if (instead is not null)
            RestartsWith.Add(instead);
    }

    public Func<UpdateViewModel, Task>? Update { get; set; }

    public List<UpdateViewModel> Updates { get; } = [];

    public Task ShowUpdateAsync(UpdateViewModel update)
    {
        Updates.Add(update);
        return Update?.Invoke(update) ?? Task.CompletedTask;
    }

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

/// <summary>Releases for the updater: <see cref="Publish"/> makes one with a zip package and its checksum.</summary>
public sealed class FakeUpdateSource : IUpdateSource
{
    public const string Executable = "BorgStudio.exe";

    public ReleaseInfo? Latest { get; set; }

    public Dictionary<string, byte[]> Files { get; } = [];

    public Exception? Error { get; set; }

    /// <summary>Runs before a download writes anything (e.g. to cancel it).</summary>
    public Func<Task>? BeforeDownload { get; set; }

    public int Checks { get; private set; }

    public ReleaseInfo Publish(string version, string rid = "win-x64", string content = "new", bool checksums = true, bool corrupt = false)
    {
        using var buffer = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(buffer, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, text) in new[] { (Executable, content), ("lib/core.dll", "library " + version) })
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open());
                writer.Write(text);
            }
        }
        var bytes = buffer.ToArray();
        var package = $"BorgStudio-{version}-{rid}.zip";
        var url = $"https://example.test/v{version}/{package}";
        Files[url] = corrupt ? [.. bytes, 0] : bytes;
        var assets = new List<ReleaseAsset> { new(package, url, Files[url].Length) };
        if (checksums)
        {
            var sums = System.Text.Encoding.UTF8.GetBytes($"{Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes))}  {package}\n");
            var sumsUrl = $"https://example.test/v{version}/{Updater.ChecksumsFile}";
            Files[sumsUrl] = sums;
            assets.Add(new ReleaseAsset(Updater.ChecksumsFile, sumsUrl, sums.Length));
        }
        Latest = new ReleaseInfo(UpdateVersion.Parse(version)!, "v" + version, "## What's new\n- Things\n\n### Downloads\n\n| Platform | File |",
            $"https://example.test/releases/v{version}", assets);
        return Latest;
    }

    public Task<ReleaseInfo?> GetLatestAsync(CancellationToken cancellationToken = default)
    {
        Checks++;
        return Error is null ? Task.FromResult(Latest) : Task.FromException<ReleaseInfo?>(Error);
    }

    public async Task DownloadAsync(string url, string path, IProgress<long>? progress, CancellationToken cancellationToken = default)
    {
        if (BeforeDownload is not null)
            await BeforeDownload();
        cancellationToken.ThrowIfCancellationRequested();
        if (!Files.TryGetValue(url, out var bytes))
            throw new HttpRequestException("404 " + url);
        var half = bytes.Length / 2;
        await using var file = File.Create(path);
        await file.WriteAsync(bytes.AsMemory(0, half), cancellationToken);
        progress?.Report(half);
        await file.WriteAsync(bytes.AsMemory(half), cancellationToken);
        progress?.Report(bytes.Length);
    }

    public Task<string> GetTextAsync(string url, CancellationToken cancellationToken = default) =>
        Files.TryGetValue(url, out var bytes)
            ? Task.FromResult(System.Text.Encoding.UTF8.GetString(bytes))
            : Task.FromException<string>(new HttpRequestException("404 " + url));
}

/// <summary>The system's scheduler: remembers what it was told last.</summary>
public sealed class FakeSystemScheduler : ISystemScheduler
{
    public List<IReadOnlyList<BackupJob>> Applied { get; } = [];

    public string? Executable { get; private set; }

    public string? Error { get; set; }

    /// <summary>The scheduled jobs of the last update.</summary>
    public IReadOnlyList<BackupJob> Scheduled => Applied.Count == 0 ? [] : [.. Applied[^1].Where(job => job.IsScheduled)];

    /// <summary>The time of the daily check in the last update; <c>null</c>: none.</summary>
    public TimeOnly? DailyCheck { get; private set; }

    /// <summary>The repositories with check schedules in the last update.</summary>
    public IReadOnlyList<RepositoryConfig> ScheduledChecks { get; private set; } = [];

    public Task ApplyAsync(IReadOnlyList<BackupJob> jobs, string executable, TimeOnly? dailyCheck = null,
        IReadOnlyList<RepositoryConfig>? repositories = null, CancellationToken cancellationToken = default)
    {
        if (Error is not null)
            throw new SchedulerException(Error);
        Applied.Add(jobs);
        ScheduledChecks = [.. (repositories ?? []).Where(repository => repository.Check.IsScheduled)];
        DailyCheck = dailyCheck;
        Executable = executable;
        return Task.CompletedTask;
    }
}

/// <summary>Remembers whether BorgStudio would start at login, and with what.</summary>
public sealed class FakeAutostart : IAutostart
{
    public string? Executable { get; private set; }

    public bool IsEnabled => Executable is not null;

    public void Enable(string executable) => Executable = executable;

    public void Disable() => Executable = null;
}

/// <summary>Records the programs started; answers with success, or as <see cref="Results"/> says (<c>null</c>: not found).</summary>
public sealed class RecordingProcesses : IProcessRunner
{
    public sealed record Call(string FileName, IReadOnlyList<string> Arguments, IReadOnlyDictionary<string, string?> Environment);

    public List<Call> Calls { get; } = [];

    /// <summary>Program name → its result; missing: exit code 0.</summary>
    public Dictionary<string, ProcessResult?> Results { get; } = [];

    public Task<ProcessResult?> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout,
        IReadOnlyDictionary<string, string?>? environment = null, string? standardInput = null, CancellationToken cancellationToken = default,
        Action<string>? standardErrorLine = null, Action<string>? standardOutputLine = null, string? workingDirectory = null)
    {
        lock (Calls)
            Calls.Add(new Call(fileName, [.. arguments], environment ?? new Dictionary<string, string?>()));
        return Task.FromResult(Results.TryGetValue(Path.GetFileName(fileName), out var result) ? result : new ProcessResult(0, "", ""));
    }
}

/// <summary>Answers HTTP requests with <see cref="Status"/> and records them with their bodies.</summary>
public sealed class FakeHttpHandler : HttpMessageHandler
{
    public sealed record Request(HttpMethod Method, Uri Url, IReadOnlyDictionary<string, string> Headers, string Body);

    public List<Request> Requests { get; } = [];

    public System.Net.HttpStatusCode Status { get; set; } = System.Net.HttpStatusCode.OK;

    public string ResponseBody { get; set; } = "";

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        var headers = request.Headers.ToDictionary(header => header.Key, header => string.Join(",", header.Value), StringComparer.OrdinalIgnoreCase);
        lock (Requests)
            Requests.Add(new Request(request.Method, request.RequestUri!, headers, body));
        return new HttpResponseMessage(Status) { Content = new StringContent(ResponseBody) };
    }
}

/// <summary>The computer's state for the conditions of scheduled runs, as a test sets it.</summary>
public sealed class FakeSystemState : ISystemState
{
    public bool? OnBattery { get; set; } = false;

    public bool? Metered { get; set; } = false;

    public bool? Wired { get; set; } = true;

    public bool Reachable { get; set; } = true;

    /// <summary>Whether a location counts as reached over the network; by default servers only.</summary>
    public Func<BorgStudio.Plugins.RepositoryLocation, bool> NetworkTarget { get; set; } = location => location.Ssh is not null;

    public int Looks { get; private set; }

    public Task<bool?> IsOnBatteryAsync(CancellationToken cancellationToken = default)
    {
        Looks++;
        return Task.FromResult(OnBattery);
    }

    public Task<bool?> IsMeteredAsync(CancellationToken cancellationToken = default) => Task.FromResult(Metered);

    public Task<bool?> IsWiredAsync(CancellationToken cancellationToken = default) => Task.FromResult(Wired);

    public Task<bool> IsReachableAsync(BorgStudio.Plugins.RepositoryLocation location, CancellationToken cancellationToken = default) =>
        Task.FromResult(Reachable);

    public bool IsNetworkTarget(BorgStudio.Plugins.RepositoryLocation location) => NetworkTarget(location);
}

/// <summary>Drives as folders of the test; ejecting is recorded (and can be refused).</summary>
public sealed class FakeDrives : IDriveSystem
{
    public List<DriveRoot> Attached { get; } = [];

    /// <summary>Folders on external drives (everything below them counts).</summary>
    public List<string> External { get; } = [];

    public List<string> Ejected { get; } = [];

    /// <summary>The answer to ejecting; <c>null</c>: ejected.</summary>
    public string? EjectProblem { get; set; }

    public IReadOnlyList<DriveRoot> Roots() => Attached;

    public Task<bool> IsExternalAsync(string path, CancellationToken cancellationToken = default) =>
        Task.FromResult(External.Any(root => path.StartsWith(root, StringComparison.OrdinalIgnoreCase)));

    public Task<string?> EjectAsync(string path, CancellationToken cancellationToken = default)
    {
        Ejected.Add(path);
        return Task.FromResult(EjectProblem);
    }

    /// <summary>The drives' UUIDs by root; a root without one has none (like a card without serial number).</summary>
    public Dictionary<string, string> VolumeIds { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, string> Labels { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Task<string?> VolumeIdAsync(string root, CancellationToken cancellationToken = default) =>
        Task.FromResult(VolumeIds.GetValueOrDefault(root));

    public string Label(string root) => Labels.GetValueOrDefault(root) ?? "";

    public long? Size(string root) => 64L << 30;
}

/// <summary>Plays BorgBase's GraphQL API: SSH keys and repositories of one account; requests are recorded.</summary>
public sealed class FakeBorgBase : HttpMessageHandler
{
    public const string Token = "borgbase-test-token";

    public List<(string Query, System.Text.Json.Nodes.JsonObject? Variables, string? Token)> Requests { get; } = [];

    public List<(string Id, string KeyData)> Keys { get; } = [];

    public List<FakeBorgBaseRepo> Repos { get; } = [];

    public double DiskUsageMb { get; set; } = 1500;

    public int StorageLimitMb { get; set; } = 10000;

    public IEnumerable<string> Operations => Requests.Select(request => request.Query.Split('{', '(')[0].Trim() + " "
        + System.Text.RegularExpressions.Regex.Match(request.Query, @"\{\s*(\w+)").Groups[1].Value);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = System.Text.Json.Nodes.JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
        var query = body["query"]!.GetValue<string>();
        var variables = body["variables"] as System.Text.Json.Nodes.JsonObject;
        var token = request.Headers.Authorization?.Parameter;
        Requests.Add((query, variables, token));
        if (token != Token)
            return new HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized);

        System.Text.Json.Nodes.JsonObject data;
        if (query.Contains("sshList"))
            data = new() { ["sshList"] = new System.Text.Json.Nodes.JsonArray([.. Keys.Select(key => (System.Text.Json.Nodes.JsonNode)new System.Text.Json.Nodes.JsonObject { ["id"] = key.Id, ["keyData"] = key.KeyData })]) };
        else if (query.Contains("sshAdd"))
        {
            var id = (Keys.Count + 100).ToString(CultureInfo.InvariantCulture);
            Keys.Add((id, variables!["keyData"]!.GetValue<string>()));
            data = new() { ["sshAdd"] = new System.Text.Json.Nodes.JsonObject { ["keyAdded"] = new System.Text.Json.Nodes.JsonObject { ["id"] = id } } };
        }
        else if (query.Contains("repoList"))
            data = new() { ["repoList"] = new System.Text.Json.Nodes.JsonArray([.. Repos.Select(repo => (System.Text.Json.Nodes.JsonNode)repo.Json())]) };
        else if (query.Contains("repoAdd"))
        {
            var repo = new FakeBorgBaseRepo($"r{Repos.Count + 1}", variables!["name"]!.GetValue<string>(), $"abcd{Repos.Count + 1}")
            {
                Region = variables["region"]!.GetValue<string>(),
            };
            repo.FullAccessKeys.AddRange(variables["keys"]!.AsArray().Select(key => key!.GetValue<string>()));
            Repos.Add(repo);
            data = new() { ["repoAdd"] = new System.Text.Json.Nodes.JsonObject { ["repoAdded"] = repo.Json() } };
        }
        else if (query.Contains("repoEdit"))
        {
            var repo = Repos.Single(candidate => candidate.Id == variables!["id"]!.GetValue<string>());
            repo.FullAccessKeys.Clear();
            repo.FullAccessKeys.AddRange(variables!["keys"]!.AsArray().Select(key => key!.GetValue<string>()));
            data = new() { ["repoEdit"] = new System.Text.Json.Nodes.JsonObject { ["repoEdited"] = new System.Text.Json.Nodes.JsonObject { ["id"] = repo.Id } } };
        }
        else if (query.Contains("repo(repoId"))
        {
            var repo = Repos.Single(candidate => candidate.Id == variables!["id"]!.GetValue<string>());
            data = new() { ["repo"] = new System.Text.Json.Nodes.JsonObject { ["currentUsage"] = repo.UsageMb, ["quota"] = repo.QuotaMb, ["quotaEnabled"] = repo.QuotaMb > 0 } };
        }
        else if (query.Contains("me"))
            data = new() { ["me"] = new System.Text.Json.Nodes.JsonObject { ["diskUsage"] = DiskUsageMb, ["storageLimitMb"] = StorageLimitMb } };
        else
            return new HttpResponseMessage(System.Net.HttpStatusCode.BadRequest);

        return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(new System.Text.Json.Nodes.JsonObject { ["data"] = data }.ToJsonString()),
        };
    }
}

public sealed record FakeBorgBaseRepo(string Id, string Name, string Login)
{
    public const string Ed25519 = "SHA256:BorgBaseEd25519FingerprintAAAAAAAAAAAAAAAAAA";

    public string Region { get; init; } = "eu";

    public List<string> FullAccessKeys { get; } = [];

    public double UsageMb { get; set; } = 250;

    public int QuotaMb { get; set; }

    public System.Text.Json.Nodes.JsonObject Json() => new()
    {
        ["id"] = Id,
        ["name"] = Name,
        ["repoPath"] = $"{Login}@{Login}.repo.borgbase.com:repo",
        ["fullAccessKeyList"] = new System.Text.Json.Nodes.JsonArray([.. FullAccessKeys.Select(key => (System.Text.Json.Nodes.JsonNode)new System.Text.Json.Nodes.JsonObject { ["id"] = key })]),
        ["server"] = new System.Text.Json.Nodes.JsonObject { ["fingerprintEd25519"] = Ed25519, ["fingerprintEcdsa"] = "", ["fingerprintRsa"] = "ab:cd:ef" },
    };
}

/// <summary>Plays network disks: set up and logged in to by id; logging in attaches a volume (a folder of the test).</summary>
public sealed class FakeNetworkDisks(FakeDrives drives) : INetworkDiskSystem
{
    public List<(NetworkDiskTarget Target, string Id, string? ChapSecret)> SetUps { get; } = [];

    public List<string> Connects { get; } = [];

    public List<string> Disconnects { get; } = [];

    /// <summary>The volume that appears when logged in; <c>null</c>: nothing appears.</summary>
    public string? Volume { get; set; }

    public string? SetUpRefusal { get; set; }

    public Task SetUpAsync(NetworkDiskTarget target, string id, string? chapSecret, CancellationToken cancellationToken)
    {
        if (SetUpRefusal is { } refusal)
            throw new RepositoryUnavailableException(refusal);
        SetUps.Add((target, id, chapSecret));
        return Task.CompletedTask;
    }

    public Task ConnectAsync(NetworkDiskTarget target, string id, CancellationToken cancellationToken)
    {
        if (!SetUps.Any(setUp => setUp.Id == id))
            throw new RepositoryUnavailableException("not set up");
        Connects.Add(id);
        if (Volume is { } volume && !drives.Attached.Any(root => root.Path == volume))
            drives.Attached.Add(new DriveRoot(volume, IsNetwork: false));
        return Task.CompletedTask;
    }

    public Task<string?> DisconnectAsync(NetworkDiskTarget target, string id, CancellationToken cancellationToken)
    {
        Disconnects.Add(id);
        drives.Attached.RemoveAll(root => root.Path == Volume);
        return Task.FromResult<string?>(null);
    }
}
