using BorgStudio.App.Resources;
using BorgStudio.App.ViewModels;
using BorgStudio.Core.Backups;
using BorgStudio.Core.Processes;
using BorgStudio.Core.Repositories;
using BorgStudio.Tests.TestSupport;

namespace BorgStudio.Tests.ViewModels;

/// <summary>A job's commands before and after its backup – with fake borg and recorded shells.</summary>
public sealed class HookFlowTests : IDisposable
{
    private readonly TestServices _test = new();
    private readonly RepositoryConfig _repository;

    public HookFlowTests()
    {
        _repository = new RepositoryConfig
        {
            Id = Guid.NewGuid(),
            Name = "Disk",
            ProviderId = "local",
            ProviderValues = new Dictionary<string, string> { ["path"] = _test.Directory.Combine("repo") },
            PassphraseMode = PassphraseMode.None,
            EncryptionMode = "none",
        };
        Directory.CreateDirectory(_test.Directory.Combine("repo"));
        Directory.CreateDirectory(Source);
        _test.Store.Save([_repository]);
    }

    public void Dispose() => _test.Dispose();

    private string Source => _test.Directory.Combine("Documents");

    private RecordingProcesses Shells => _test.HookProcesses;

    private int Backups => _test.Borg.Calls.Count(call => call.Arguments[0] == "create");

    private BackupJob Job(JobHooks hooks) => new()
    {
        Id = Guid.NewGuid(),
        Name = "Documents",
        RepositoryId = _repository.Id,
        Sources = [Source],
        Exclusions = new BackupExclusions { CachesAndTemp = false },
        Hooks = hooks with { Shell = HookShell.Sh },
    };

    private async Task<MainViewModel> RunAsync(BackupJob job)
    {
        _test.Jobs.Save([job]);
        var main = new MainViewModel(_test.Services);
        await main.CheckBorgCommand.ExecuteAsync(null);
        main.ListedRepository = main.Repositories[0];
        main.SelectedDetailsTab = MainViewModel.JobsTab;
        main.SelectedJob = main.RepositoryJobs[0];
        await main.RunJobCommand.ExecuteAsync(null);
        return main;
    }

    private BackupRunSummary LastRun => _test.Jobs.Load()[0].LastRun!;

    [Fact]
    public async Task The_commands_run_around_the_backup_with_its_outcome()
    {
        await RunAsync(Job(new JobHooks { Before = "dump-db", After = "start-service" }));

        Assert.Equal(1, Backups);
        Assert.Equal([["-c", "dump-db"], ["-c", "start-service"]], Shells.Calls.Select(call => call.Arguments));
        Assert.Equal("Documents", Shells.Calls[0].Environment["BORGSTUDIO_JOB"]);
        Assert.False(Shells.Calls[0].Environment.ContainsKey("BORGSTUDIO_OUTCOME"));
        Assert.Equal("succeeded", Shells.Calls[1].Environment["BORGSTUDIO_OUTCOME"]);
        Assert.Equal("Documents-host-2026-11-06T10:00:00", Shells.Calls[1].Environment["BORGSTUDIO_ARCHIVE"]);
        Assert.Equal(BackupOutcome.Succeeded, LastRun.Outcome);
        Assert.True(File.Exists(Path.Combine(_test.Directory.Path, "logs", "hooks.log")));
    }

    [Fact]
    public async Task A_failed_command_before_stops_the_backup()
    {
        Shells.Results["sh"] = new ProcessResult(2, "connection refused", "");

        var main = await RunAsync(Job(new JobHooks { Before = "dump-db", After = "start-service" }));

        Assert.Equal(0, Backups);
        // Only after a backup that worked: the command after does not run.
        Assert.Single(Shells.Calls);
        Assert.Equal(BackupOutcome.Failed, LastRun.Outcome);
        Assert.Equal((HookStage.Before, 2), (LastRun.Hook!.Stage, LastRun.Hook.ExitCode));
        var expected = BorgTexts.Format(Strings.HookFailedExit, Strings.HookStageBefore, 2) + ": connection refused";
        Assert.Equal(expected, LastRun.Message);
        Assert.Contains(expected, main.CurrentBackup!.ResultText);
    }

    [Fact]
    public async Task The_command_after_can_run_always_also_after_a_failure()
    {
        Shells.Results["sh"] = new ProcessResult(2, "", "");

        await RunAsync(Job(new JobHooks { Before = "stop-service", After = "start-service", AfterAlways = true }));

        Assert.Equal(2, Shells.Calls.Count);
        Assert.Equal("failed", Shells.Calls[1].Environment["BORGSTUDIO_OUTCOME"]);
        // The first problem is what the run tells.
        Assert.Equal(HookStage.Before, LastRun.Hook!.Stage);
    }

    [Fact]
    public async Task Backing_up_anyway_makes_it_a_warning()
    {
        Shells.Results["sh"] = new ProcessResult(1, "", "");

        await RunAsync(Job(new JobHooks { Before = "dump-db", ContinueOnBeforeFailure = true }));

        Assert.Equal(1, Backups);
        Assert.Equal(BackupOutcome.Warnings, LastRun.Outcome);
        Assert.StartsWith(BorgTexts.Format(Strings.HookFailedExit, Strings.HookStageBefore, 1), LastRun.Message);
    }

    [Fact]
    public async Task A_failed_command_after_makes_a_good_backup_a_warning()
    {
        await RunAsync(Job(new JobHooks { After = "start-service" }) with { Hooks = new JobHooks { After = "start-service", Shell = HookShell.Bash } });
        Assert.Equal(BackupOutcome.Succeeded, LastRun.Outcome);

        Shells.Results["bash"] = null; // not installed
        await RunAsync(Job(new JobHooks()) with { Hooks = new JobHooks { After = "start-service", Shell = HookShell.Bash } });

        Assert.Equal(BackupOutcome.Warnings, LastRun.Outcome);
        Assert.Equal(BorgTexts.Format(Strings.HookNotStarted, Strings.HookStageAfter, "bash"), LastRun.Message);
    }

    [Fact]
    public async Task Without_commands_nothing_else_runs()
    {
        await RunAsync(Job(new JobHooks()));

        Assert.Empty(Shells.Calls);
        Assert.Equal(1, Backups);
    }

    [Fact]
    public void The_editor_keeps_the_commands()
    {
        var job = Job(new JobHooks());
        var editor = new BackupJobEditorViewModel(_repository, FakeBorg.Installation, _test.Dialogs, job);
        Assert.True(editor.HookAfterOnSuccess);
        Assert.Contains(editor.HookShellOptions, option => option.Value == (OperatingSystem.IsWindows() ? HookShell.Cmd : HookShell.Bash));

        editor.HookBefore = "  dump-db  ";
        editor.HookAfter = "start-service";
        editor.HookContinueOnFailure = true;
        editor.HookAfterAlways = true;
        editor.SelectedHookShell = editor.HookShellOptions[^1];
        editor.SaveCommand.Execute(null);

        var hooks = editor.Result!.Hooks;
        Assert.Equal(("dump-db", "start-service", true, true), (hooks.Before, hooks.After, hooks.ContinueOnBeforeFailure, hooks.AfterAlways));
        Assert.Equal(editor.HookShellOptions[^1].Value, hooks.Shell);
    }
}
