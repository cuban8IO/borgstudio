using System.Text;
using System.Text.Json.Serialization;
using BorgStudio.Core.Processes;

namespace BorgStudio.Core.Backups;

/// <summary>What runs a job's commands.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<HookShell>))]
public enum HookShell
{
    /// <summary>PowerShell on Windows (pwsh if installed, otherwise Windows PowerShell), sh elsewhere.</summary>
    Default,

    PowerShell,

    /// <summary>cmd.exe (Windows).</summary>
    Cmd,

    /// <summary>sh inside WSL (Windows).</summary>
    Wsl,

    Sh,

    Bash,

    /// <summary>zsh (macOS).</summary>
    Zsh,
}

/// <summary>Commands a job runs before and after its backup.</summary>
public sealed record JobHooks
{
    /// <summary>Runs before the backup (e.g. a database dump); empty: none.</summary>
    public string Before { get; init; } = "";

    /// <summary>Runs after the backup and its clean-up (e.g. starting a stopped service again); empty: none.</summary>
    public string After { get; init; } = "";

    public HookShell Shell { get; init; } = HookShell.Default;

    /// <summary>The backup runs even if the command before it fails (then with a warning); otherwise it does not.</summary>
    public bool ContinueOnBeforeFailure { get; init; }

    /// <summary>The command after runs whatever the outcome; otherwise only after a backup that worked.</summary>
    public bool AfterAlways { get; init; }

    [JsonIgnore]
    public bool HasBefore => Before.Trim().Length > 0;

    [JsonIgnore]
    public bool HasAfter => After.Trim().Length > 0;

    /// <summary>The shell actually used on this system.</summary>
    public HookShell EffectiveShell => Shell != HookShell.Default ? Shell : OperatingSystem.IsWindows() ? HookShell.PowerShell : HookShell.Sh;
}

[JsonConverter(typeof(JsonStringEnumConverter<HookStage>))]
public enum HookStage
{
    Before,
    After,
}

/// <summary>A command of a job that did not work.</summary>
/// <param name="ExitCode">Its exit code; <c>null</c> if it could not be started (<see cref="Error"/> tells why) or was stopped.</param>
/// <param name="Output">Its last lines (output and errors).</param>
public sealed record HookProblem(HookStage Stage, int? ExitCode, IReadOnlyList<string> Output, string? Error = null);

/// <summary>How a command ended.</summary>
public sealed record HookResult(int? ExitCode, IReadOnlyList<string> Output, string? Error = null, bool Cancelled = false)
{
    public bool Succeeded => ExitCode == 0;
}

/// <summary>
/// Runs a job's commands in the chosen shell, without time limit (they end with the run when it is cancelled). What they
/// print goes to logs/hooks.log in full; the last lines are kept with the run. They get the job, the repository and – after
/// the backup – its outcome and archive as environment variables (BORGSTUDIO_*).
/// </summary>
public sealed class HookRunner(IProcessRunner? processes = null, string? wslExecutable = null)
{
    /// <summary>The lines of output kept with the run.</summary>
    public const int KeptLines = 10;

    private readonly IProcessRunner _processes = processes ?? new ProcessRunner();
    private readonly string _wsl = wslExecutable ?? Path.Combine(Environment.SystemDirectory, "wsl.exe");

    private static readonly object LogGate = new();

    /// <param name="logFile">Where the whole output goes (appended).</param>
    public async Task<HookResult> RunAsync(HookShell shell, string script, IReadOnlyDictionary<string, string> variables, string logFile,
        string label, CancellationToken cancellationToken = default)
    {
        var environment = variables.ToDictionary(pair => pair.Key, pair => (string?)pair.Value);
        string? scriptFile = null;
        try
        {
            var (program, arguments, file) = Command(shell, script, environment);
            scriptFile = file;
            var lines = new List<string>();
            void OnLine(string line)
            {
                lock (lines)
                    lines.Add(line);
            }

            ProcessResult? result;
            try
            {
                result = await _processes.RunAsync(program, arguments, Timeout.InfiniteTimeSpan, environment, cancellationToken: cancellationToken,
                    standardErrorLine: OnLine, standardOutputLine: OnLine);
                // Windows PowerShell where PowerShell 7 is not installed.
                if (result is null && shell == HookShell.PowerShell && program == "pwsh")
                    result = await _processes.RunAsync("powershell.exe", arguments, Timeout.InfiniteTimeSpan, environment,
                        cancellationToken: cancellationToken, standardErrorLine: OnLine, standardOutputLine: OnLine);
            }
            catch (OperationCanceledException)
            {
                Log(logFile, label, script, lines, "stopped");
                return new HookResult(null, Tail(lines), Cancelled: true);
            }

            if (result is null)
            {
                Log(logFile, label, script, lines, $"{program} could not be started");
                return new HookResult(null, [], Error: program);
            }
            // Output that came without line callbacks (a runner that only returns it at the end).
            if (lines.Count == 0)
                lines.AddRange((result.StandardOutput + "\n" + result.StandardError).Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(line => line.TrimEnd('\r')));
            Log(logFile, label, script, lines, $"exit code {result.ExitCode}");
            return new HookResult(result.ExitCode, Tail(lines));
        }
        finally
        {
            if (scriptFile is not null)
            {
                try
                {
                    File.Delete(scriptFile);
                }
                catch (IOException)
                {
                }
            }
        }
    }

    /// <summary>The program, its arguments and – for shells that need one – the script file written for it.</summary>
    private (string Program, List<string> Arguments, string? ScriptFile) Command(HookShell shell, string script,
        Dictionary<string, string?> environment)
    {
        switch (shell)
        {
            case HookShell.PowerShell:
            {
                // A file: multi-line scripts and their exit code work as in a console ("exit 3").
                var file = ScriptFile(".ps1", "$ErrorActionPreference = 'Stop'\n" + script + "\nif ($LASTEXITCODE) { exit $LASTEXITCODE }\n");
                return ("pwsh", ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", file], file);
            }
            case HookShell.Cmd:
            {
                var file = ScriptFile(".cmd", "@echo off\r\n" + script.ReplaceLineEndings("\r\n") + "\r\n");
                return ("cmd.exe", ["/d", "/c", file], file);
            }
            case HookShell.Wsl:
                // WSL passes on only the Windows variables listed in WSLENV.
                environment["WSLENV"] = string.Join(':', new[] { Environment.GetEnvironmentVariable("WSLENV") }
                    .Where(value => !string.IsNullOrEmpty(value)).Concat(environment.Keys.Where(key => key.StartsWith("BORGSTUDIO_", StringComparison.Ordinal))));
                return (_wsl, ["-e", "sh", "-c", script.ReplaceLineEndings("\n")], null);
            case HookShell.Bash:
                return ("bash", ["-c", script.ReplaceLineEndings("\n")], null);
            case HookShell.Zsh:
                return ("zsh", ["-c", script.ReplaceLineEndings("\n")], null);
            default:
                return ("sh", ["-c", script.ReplaceLineEndings("\n")], null);
        }
    }

    private static string ScriptFile(string extension, string content)
    {
        var file = Path.Combine(Path.GetTempPath(), "borgstudio-hook-" + Guid.NewGuid().ToString("N")[..12] + extension);
        File.WriteAllText(file, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: extension == ".ps1"));
        return file;
    }

    private static IReadOnlyList<string> Tail(List<string> lines)
    {
        lock (lines)
            return [.. lines.Where(line => line.Trim().Length > 0).TakeLast(KeptLines)];
    }

    private static void Log(string logFile, string label, string script, List<string> lines, string end)
    {
        try
        {
            lock (LogGate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(logFile))!);
                var text = new StringBuilder();
                text.AppendLine($"=== {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} {label}");
                foreach (var line in script.ReplaceLineEndings("\n").Split('\n'))
                    text.AppendLine("> " + line);
                lock (lines)
                    foreach (var line in lines)
                        text.AppendLine(line);
                text.AppendLine($"=== {end}");
                File.AppendAllText(logFile, text.ToString());
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The run goes on without its log.
        }
    }
}
