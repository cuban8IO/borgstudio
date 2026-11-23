using BorgStudio.App.Resources;
using BorgStudio.App.ViewModels;
using BorgStudio.Core.Backups;
using BorgStudio.Core.Borg;
using BorgStudio.Core.Repositories;
using BorgStudio.Tests.TestSupport;

namespace BorgStudio.Tests.ViewModels;

/// <summary>Releasing a lock left behind: offered once borg reports the repository locked – with fake borg.</summary>
public sealed class BreakLockFlowTests : IDisposable
{
    private readonly TestServices _test = new();
    private readonly RepositoryConfig _repository;

    public BreakLockFlowTests()
    {
        _repository = new RepositoryConfig
        {
            Id = Guid.NewGuid(),
            Name = "Disk",
            ProviderId = "local",
            ProviderValues = new Dictionary<string, string> { ["path"] = RepositoryFolder },
            PassphraseMode = PassphraseMode.None,
            EncryptionMode = "none",
        };
        Directory.CreateDirectory(RepositoryFolder);
        Directory.CreateDirectory(Source);
        _test.Store.Save([_repository]);
    }

    public void Dispose() => _test.Dispose();

    private string RepositoryFolder => _test.Directory.Combine("repo");

    private string Source => _test.Directory.Combine("Documents");

    private FakeBorg Borg => _test.Borg;

    private FakeDialogs Dialogs => _test.Dialogs;

    private int BreakLocks => Borg.Calls.Count(call => call.Arguments[0] == "break-lock");

    private BackupJob Job(BackupRunSummary? lastRun = null) => new()
    {
        Id = Guid.NewGuid(),
        Name = "Documents",
        RepositoryId = _repository.Id,
        Sources = [Source],
        Exclusions = new BackupExclusions { CachesAndTemp = false },
        LastRun = lastRun,
    };

    private async Task<MainViewModel> StartAsync(params BackupJob[] jobs)
    {
        _test.Jobs.Save(jobs);
        var main = new MainViewModel(_test.Services);
        await main.CheckBorgCommand.ExecuteAsync(null);
        main.ListedRepository = main.Repositories[0];
        return main;
    }

    /// <summary>borg reports the repository locked when BorgStudio tests the connection.</summary>
    private async Task<RepositoryItemViewModel> FindLockedAsync(MainViewModel main)
    {
        Borg.EncryptionMode = null;
        Borg.InfoError = "LockTimeout";
        await main.TestRepositoryCommand.ExecuteAsync(null);
        Borg.EncryptionMode = "none";
        return main.SelectedRepository!;
    }

    [Fact]
    public void Nothing_is_offered_before_borg_reports_a_lock()
    {
        var main = new MainViewModel(_test.Services);
        Assert.False(main.Repositories[0].IsLocked);
        Assert.False(main.BreakLockCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_lock_error_offers_releasing_it_and_a_success_clears_it()
    {
        var main = await StartAsync();
        var item = await FindLockedAsync(main);

        Assert.True(item.IsLocked);
        Assert.StartsWith(Strings.BorgErrorLocked, item.ResultText);
        Assert.Contains(Strings.BreakLock, Strings.BorgErrorLocked, StringComparison.Ordinal);
        Assert.True(main.BreakLockCommand.CanExecute(null));

        // borg gets through again (the other borg finished): nothing to release.
        await main.TestRepositoryCommand.ExecuteAsync(null);
        Assert.False(item.IsLocked);
        Assert.False(main.BreakLockCommand.CanExecute(null));
    }

    [Fact]
    public async Task Releasing_asks_first_with_who_holds_the_lock()
    {
        var main = await StartAsync();
        var item = await FindLockedAsync(main);
        File.WriteAllText(Path.Combine(RepositoryFolder, "lock.roster"), """{"exclusive": [["OTHER-PC.local@1234567", 4711, 0]]}""");
        Borg.Calls.Clear();

        await main.BreakLockCommand.ExecuteAsync(null);
        Assert.Equal(0, BreakLocks);
        Assert.Equal([Strings.BreakLockTitle], Dialogs.Shown);
        Assert.Contains("OTHER-PC.local", Dialogs.Messages[0], StringComparison.Ordinal);
        Assert.Contains("4711", Dialogs.Messages[0], StringComparison.Ordinal);
        Assert.DoesNotContain(Strings.BreakLockThisComputer, Dialogs.Messages[0], StringComparison.Ordinal);
        Assert.True(item.IsLocked);

        Dialogs.ConfirmAnswers.Enqueue(true);
        await main.BreakLockCommand.ExecuteAsync(null);

        Assert.Equal(1, BreakLocks);
        Assert.Equal(["break-lock", "--log-json", RepositoryFolder], Borg.Calls.Single(call => call.Arguments[0] == "break-lock").Arguments);
        Assert.False(item.IsLocked);
        Assert.Equal(Strings.BreakLockDone, item.ResultText);
    }

    [Fact]
    public async Task A_lock_of_this_computer_warns_that_its_process_still_runs()
    {
        var main = await StartAsync();
        await FindLockedAsync(main);
        File.WriteAllText(Path.Combine(RepositoryFolder, "lock.roster"),
            $$"""{"shared": [["{{Environment.MachineName}}.example.net@99", 12, 0]]}""");

        await main.BreakLockCommand.ExecuteAsync(null);

        Assert.Contains(Strings.BreakLockThisComputer, Dialogs.Messages[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failed_release_keeps_the_offer()
    {
        var main = await StartAsync();
        var item = await FindLockedAsync(main);
        Borg.BreakLockError = "ConnectionClosed";
        Dialogs.ConfirmAnswers.Enqueue(true);

        await main.BreakLockCommand.ExecuteAsync(null);

        Assert.True(item.IsLocked);
        Assert.True(item.HasError);
    }

    [Fact]
    public async Task BorgStudios_own_run_is_no_lock_left_behind()
    {
        var main = await StartAsync();
        var item = await FindLockedAsync(main);
        Borg.Calls.Clear();

        using (RepositoryLock.TryAcquire(main.Runner.LockDirectory, _repository.Id))
            await main.BreakLockCommand.ExecuteAsync(null);

        Assert.Equal(0, BreakLocks);
        Assert.Empty(Dialogs.Shown);
        Assert.Equal(Strings.BreakLockBusy, item.ResultText);
    }

    [Fact]
    public async Task A_backup_that_found_the_repository_locked_offers_it_too()
    {
        // A scheduled run without window found it locked.
        var locked = new BackupRunSummary
        {
            Started = DateTimeOffset.Now.AddHours(-1), Duration = TimeSpan.FromSeconds(3), Outcome = BackupOutcome.Failed,
            Message = Strings.BorgErrorLocked, RepositoryLocked = true,
        };
        var main = await StartAsync(Job(locked));
        Assert.True(main.SelectedRepository!.IsLocked);

        // A backup in the window that gets through clears it.
        main.SelectedDetailsTab = MainViewModel.JobsTab;
        main.SelectedJob = main.RepositoryJobs[0];
        await main.RunJobCommand.ExecuteAsync(null);
        Assert.False(main.SelectedRepository!.IsLocked);
    }

    [Fact]
    public void The_lock_files_tell_who_holds_the_lock()
    {
        File.WriteAllText(Path.Combine(RepositoryFolder, "lock.roster"),
            """{"exclusive": [["NAS-PC.example.net@91760128470", 458, 0]], "shared": [["laptop@1", 9, 0]]}""");

        var info = RepositoryLockInfo.Read(RepositoryFolder)!;

        Assert.Equal([new LockHolder("NAS-PC.example.net", 458, true), new LockHolder("laptop", 9, false)], info.Holders);
        Assert.NotNull(info.Since);
        Assert.Null(RepositoryLockInfo.Read(_test.Directory.Combine("nothing")));
        File.WriteAllText(Path.Combine(RepositoryFolder, "lock.roster"), "not json");
        Assert.Null(RepositoryLockInfo.Read(RepositoryFolder));
        Assert.True(RepositoryLockInfo.IsThisComputer(Environment.MachineName.ToLowerInvariant() + ".fritz.box"));
        Assert.False(RepositoryLockInfo.IsThisComputer("some-other-computer"));
    }
}
