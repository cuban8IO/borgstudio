using BorgStudio.Core.Backups;
using BorgStudio.Core.Processes;
using BorgStudio.Tests.TestSupport;

namespace BorgStudio.Tests.Backups;

/// <summary>Running a job's commands: the shells, the variables, the log – and once in a real shell.</summary>
public sealed class HookRunnerTests : IDisposable
{
    private readonly TemporaryDirectory _directory = new();

    public void Dispose() => _directory.Dispose();

    private string LogFile => _directory.Combine("logs", "hooks.log");

    private static readonly Dictionary<string, string> Variables = new() { ["BORGSTUDIO_JOB"] = "Documents", ["BORGSTUDIO_OUTCOME"] = "succeeded" };

    /// <summary>Records each call, with the script file's content while it exists.</summary>
    private sealed class Recorder(ProcessResult? result) : IProcessRunner
    {
        public List<(string FileName, IReadOnlyList<string> Arguments, IReadOnlyDictionary<string, string?> Environment, string? Script)> Calls { get; } = [];

        public ProcessResult? Result { get; set; } = result;

        public Func<string, ProcessResult?>? ResultFor { get; set; }

        public Task<ProcessResult?> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout,
            IReadOnlyDictionary<string, string?>? environment = null, string? standardInput = null, CancellationToken cancellationToken = default,
            Action<string>? standardErrorLine = null, Action<string>? standardOutputLine = null, string? workingDirectory = null)
        {
            Assert.Equal(Timeout.InfiniteTimeSpan, timeout);
            var file = arguments.FirstOrDefault(argument => argument.EndsWith(".ps1", StringComparison.Ordinal) || argument.EndsWith(".cmd", StringComparison.Ordinal));
            Calls.Add((fileName, [.. arguments], environment ?? new Dictionary<string, string?>(), file is null ? null : File.ReadAllText(file)));
            var result = ResultFor?.Invoke(fileName) ?? Result;
            if (result is not null)
            {
                foreach (var line in result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    standardOutputLine?.Invoke(line);
            }
            return Task.FromResult(result);
        }
    }

    [Fact]
    public async Task PowerShell_gets_a_script_file_that_keeps_exit_codes()
    {
        var recorder = new Recorder(new ProcessResult(3, "dumping\nfailed: no connection\n", ""));

        var result = await new HookRunner(recorder).RunAsync(HookShell.PowerShell, "pg_dump mydb > dump.sql", Variables, LogFile, "Documents: before");

        Assert.Equal(3, result.ExitCode);
        Assert.Equal(["dumping", "failed: no connection"], result.Output);
        var call = Assert.Single(recorder.Calls);
        Assert.Equal("pwsh", call.FileName);
        Assert.Equal(["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File"], call.Arguments.Take(5));
        Assert.Contains("pg_dump mydb > dump.sql", call.Script);
        Assert.Contains("exit $LASTEXITCODE", call.Script);
        Assert.Equal("Documents", call.Environment["BORGSTUDIO_JOB"]);
        Assert.False(File.Exists(call.Arguments[^1]));
        var log = File.ReadAllText(LogFile);
        Assert.Contains("Documents: before", log);
        Assert.Contains("> pg_dump mydb > dump.sql", log);
        Assert.Contains("failed: no connection", log);
        Assert.Contains("exit code 3", log);
    }

    [Fact]
    public async Task Windows_PowerShell_steps_in_without_PowerShell_7()
    {
        var recorder = new Recorder(null) { ResultFor = program => program == "pwsh" ? null : new ProcessResult(0, "", "") };

        var result = await new HookRunner(recorder).RunAsync(HookShell.PowerShell, "Stop-Service X", Variables, LogFile, "test");

        Assert.True(result.Succeeded);
        Assert.Equal(["pwsh", "powershell.exe"], recorder.Calls.Select(call => call.FileName));
    }

    [Fact]
    public async Task Cmd_and_the_posix_shells_and_WSL()
    {
        var recorder = new Recorder(new ProcessResult(0, "", ""));
        var hooks = new HookRunner(recorder, @"C:\Windows\System32\wsl.exe");

        await hooks.RunAsync(HookShell.Cmd, "net stop X\nnet start Y", Variables, LogFile, "test");
        await hooks.RunAsync(HookShell.Sh, "echo $BORGSTUDIO_JOB", Variables, LogFile, "test");
        await hooks.RunAsync(HookShell.Bash, "echo a", Variables, LogFile, "test");
        await hooks.RunAsync(HookShell.Zsh, "echo a", Variables, LogFile, "test");
        await hooks.RunAsync(HookShell.Wsl, "mysqldump db > /tmp/db.sql", Variables, LogFile, "test");

        Assert.Equal("cmd.exe", recorder.Calls[0].FileName);
        Assert.Equal(["/d", "/c"], recorder.Calls[0].Arguments.Take(2));
        Assert.Equal("@echo off\r\nnet stop X\r\nnet start Y\r\n", recorder.Calls[0].Script);
        Assert.Equal("sh", recorder.Calls[1].FileName);
        Assert.Equal(["-c", "echo $BORGSTUDIO_JOB"], recorder.Calls[1].Arguments);
        Assert.Equal("bash", recorder.Calls[2].FileName);
        Assert.Equal("zsh", recorder.Calls[3].FileName);
        var wsl = recorder.Calls[4];
        Assert.Equal(@"C:\Windows\System32\wsl.exe", wsl.FileName);
        Assert.Equal(["-e", "sh", "-c", "mysqldump db > /tmp/db.sql"], wsl.Arguments);
        Assert.Contains("BORGSTUDIO_JOB", wsl.Environment["WSLENV"]!.Split(':'));
        Assert.Contains("BORGSTUDIO_OUTCOME", wsl.Environment["WSLENV"]!.Split(':'));
    }

    [Fact]
    public async Task A_missing_shell_and_a_stopped_command_are_told()
    {
        var missing = await new HookRunner(new Recorder(null)).RunAsync(HookShell.Bash, "echo a", Variables, LogFile, "test");
        Assert.Equal((null, "bash"), (missing.ExitCode, missing.Error));

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var stopped = await new HookRunner(new StoppedRunner()).RunAsync(HookShell.Sh, "sleep 100", Variables, LogFile, "test", cancellation.Token);
        Assert.True(stopped.Cancelled);
    }

    private sealed class StoppedRunner : IProcessRunner
    {
        public Task<ProcessResult?> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout,
            IReadOnlyDictionary<string, string?>? environment = null, string? standardInput = null, CancellationToken cancellationToken = default,
            Action<string>? standardErrorLine = null, Action<string>? standardOutputLine = null, string? workingDirectory = null) =>
            throw new OperationCanceledException(cancellationToken);
    }

    [Fact]
    public async Task Runs_in_the_systems_shell_with_the_variables()
    {
        var shell = OperatingSystem.IsWindows() ? HookShell.PowerShell : HookShell.Sh;
        var script = OperatingSystem.IsWindows()
            ? "Write-Output \"job $env:BORGSTUDIO_JOB\"\ncmd /c exit 3"
            : "echo \"job $BORGSTUDIO_JOB\"\nexit 3";

        var result = await new HookRunner().RunAsync(shell, script, Variables, LogFile, "real");

        Assert.Equal(3, result.ExitCode);
        Assert.Contains("job Documents", result.Output);
    }
}
