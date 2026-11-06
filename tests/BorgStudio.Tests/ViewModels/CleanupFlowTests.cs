using BorgStudio.App.Resources;
using BorgStudio.App.ViewModels;
using BorgStudio.Core.Backups;
using BorgStudio.Core.Repositories;
using BorgStudio.Tests.TestSupport;

namespace BorgStudio.Tests.ViewModels;

/// <summary>Cleaning up old archives (prune + compact) after a backup, by hand with a preview, and freeing space – with fake borg.</summary>
public sealed class CleanupFlowTests : IDisposable
{
    private const string Passphrase = "pw";

    private readonly TestServices _test = new();
    private readonly RepositoryConfig _repository;

    public CleanupFlowTests()
    {
        _repository = new RepositoryConfig
        {
            Id = Guid.NewGuid(),
            Name = "Disk",
            ProviderId = "local",
            ProviderValues = new Dictionary<string, string> { ["path"] = _test.Directory.Combine("repo") },
            PassphraseMode = PassphraseMode.Ask,
            EncryptionMode = "repokey-blake2",
        };
        _test.Store.Save([_repository]);
        Directory.CreateDirectory(Source);

        // Three older archives of the job, one of another job whose name starts the same.
        Borg.Archives.Add(("Documents-host-2026-11-01T10:00:00", new DateTime(2026, 11, 1, 10, 0, 0)));
        Borg.Archives.Add(("Documents-Old-host-2026-11-01T11:00:00", new DateTime(2026, 11, 1, 11, 0, 0)));
        Borg.Archives.Add(("Documents-host-2026-11-02T10:00:00", new DateTime(2026, 11, 2, 10, 0, 0)));
        Borg.Archives.Add(("Documents-host-2026-11-03T10:00:00", new DateTime(2026, 11, 3, 10, 0, 0)));
    }

    public void Dispose() => _test.Dispose();

    private string Source => _test.Directory.Combine("Documents");

    private FakeDialogs Dialogs => _test.Dialogs;

    private FakeBorg Borg => _test.Borg;

    private IEnumerable<string> ArchiveNames => Borg.Archives.Select(archive => archive.Name);

    private async Task<MainViewModel> StartAsync(BackupJob job)
    {
        _test.Jobs.Save([job]);
        var main = new MainViewModel(_test.Services);
        await main.CheckBorgCommand.ExecuteAsync(null);
        Borg.Calls.Clear();
        return main;
    }

    private BackupJob Job(RetentionPolicy? retention) => new()
    {
        Id = Guid.NewGuid(),
        Name = "Documents",
        RepositoryId = _repository.Id,
        Sources = [Source],
        Exclusions = new BackupExclusions { CachesAndTemp = false },
        Retention = retention,
    };

    private static RetentionPolicy KeepTwoDaily => new() { Daily = 2 };

    [Fact]
    public async Task A_backup_deletes_the_jobs_old_archives_by_its_rules_and_frees_the_space()
    {
        Borg.PruneKeep = 2;
        var main = await StartAsync(Job(KeepTwoDaily));
        Dialogs.Passphrases.Enqueue(Passphrase);

        await main.RunJobCommand.ExecuteAsync(null);

        Assert.Equal(["create", "prune", "compact"], Borg.Commands);
        var prune = Borg.Calls[1];
        Assert.Equal(Passphrase, prune.Passphrase);
        Assert.DoesNotContain("--dry-run", prune.Arguments);
        Assert.Contains("--list", prune.Arguments);
        Assert.Equal("Documents-{hostname}-*", prune.Arguments[prune.Arguments.ToList().IndexOf("--glob-archives") + 1]);
        Assert.Equal(["--keep-daily", "2"], prune.Arguments.SkipWhile(argument => argument != "--keep-daily").Take(2));
        Assert.Equal("", Borg.Calls[2].Passphrase); // compact needs none

        // The new archive and the newest older one stay; the other job is not touched.
        Assert.Equal(["Documents-Old-host-2026-11-01T11:00:00", "Documents-host-2026-11-03T10:00:00", "Documents-host-2026-11-06T10:00:00"],
            ArchiveNames.Order(StringComparer.Ordinal));

        var run = main.CurrentBackup!;
        Assert.False(run.IsRunning);
        Assert.Equal(BackupOutcome.Succeeded, run.Summary!.Outcome);
        Assert.Equal(new CleanupSummary { PrunedArchives = 2, FreedBytes = 1_500_000 }, run.Summary.Cleanup);
        Assert.EndsWith("\n" + BorgTexts.Format(Strings.CleanupResult, 2, BorgTexts.Size(1_500_000)), run.ResultText);
        Assert.Equal(2, Assert.Single(_test.Jobs.Load()).LastRun!.Cleanup!.PrunedArchives);
    }

    [Fact]
    public async Task Without_rules_or_after_a_cancelled_backup_nothing_is_deleted()
    {
        var main = await StartAsync(Job(retention: null));
        Dialogs.Passphrases.Enqueue(Passphrase);
        await main.RunJobCommand.ExecuteAsync(null);
        Assert.Equal(["create"], Borg.Commands);
        Assert.Null(main.CurrentBackup!.Summary!.Cleanup);
        Assert.Equal(Strings.JobRetentionNone, main.SelectedJob!.RetentionText);
        Assert.False(main.CleanUpJobCommand.CanExecute(null));

        var withRules = await StartAsync(Job(KeepTwoDaily));
        Borg.AfterCreateProgress = step =>
        {
            if (step == 2)
                withRules.CancelBackupCommand.Execute(null);
        };
        Dialogs.Passphrases.Enqueue(Passphrase);
        await withRules.RunJobCommand.ExecuteAsync(null);
        Assert.Equal(["create"], Borg.Commands);
        Assert.Equal(BackupOutcome.Cancelled, withRules.CurrentBackup!.Summary!.Outcome);
        Assert.Null(withRules.CurrentBackup.Summary.Cleanup);
    }

    [Fact]
    public async Task When_the_rules_keep_everything_space_is_not_compacted()
    {
        Borg.PruneKeep = 10;
        var main = await StartAsync(Job(KeepTwoDaily));
        Dialogs.Passphrases.Enqueue(Passphrase);

        await main.RunJobCommand.ExecuteAsync(null);

        Assert.Equal(["create", "prune"], Borg.Commands);
        Assert.Equal(new CleanupSummary(), main.CurrentBackup!.Summary!.Cleanup);
        Assert.EndsWith(Strings.CleanupNothing, main.CurrentBackup.ResultText);
    }

    [Fact]
    public async Task Failing_to_free_the_space_is_reported_with_the_deleted_archives()
    {
        Borg.PruneKeep = 2;
        Borg.CompactError = "LockTimeout";
        var main = await StartAsync(Job(KeepTwoDaily));
        Dialogs.Passphrases.Enqueue(Passphrase);

        await main.RunJobCommand.ExecuteAsync(null);

        var cleanup = main.CurrentBackup!.Summary!.Cleanup!;
        Assert.Equal(2, cleanup.PrunedArchives);
        Assert.Null(cleanup.FreedBytes);
        Assert.NotNull(cleanup.Error);
        Assert.Contains(BorgTexts.Format(Strings.CleanupCompactFailed, 2, cleanup.Error), main.CurrentBackup.ResultText);
        Assert.Equal(BackupOutcome.Succeeded, main.CurrentBackup.Summary.Outcome); // the backup itself is fine
    }

    [Fact]
    public async Task Cleaning_up_by_hand_shows_what_goes_and_deletes_only_when_confirmed()
    {
        Borg.PruneKeep = 2;
        var main = await StartAsync(Job(KeepTwoDaily));
        Assert.Equal(BorgTexts.Format(Strings.JobRetention, BorgTexts.Format(Strings.RetentionDaily, 2)), main.SelectedJob!.RetentionText);
        Assert.True(main.CleanUpJobCommand.CanExecute(null));

        // Not confirmed: only the preview ran.
        Dialogs.Passphrases.Enqueue(Passphrase);
        await main.CleanUpJobCommand.ExecuteAsync(null);

        Assert.Equal(["prune"], Borg.Commands);
        Assert.Contains("--dry-run", Borg.Calls[0].Arguments);
        Assert.Equal(4, Borg.Archives.Count);
        var preview = Assert.Single(Dialogs.PrunePreviews);
        Assert.Equal(BorgTexts.Format(Strings.PruneSummary, "Documents", 3, 1, 2), preview.SummaryText);
        Assert.Equal(BorgTexts.Format(Strings.PruneConfirm, 1), preview.ConfirmText);
        Assert.Equal(BorgTexts.Format(Strings.PruneRules, BorgTexts.Format(Strings.RetentionDaily, 2)), preview.RulesText);
        Assert.Equal(["Documents-host-2026-11-03T10:00:00", "Documents-host-2026-11-02T10:00:00", "Documents-host-2026-11-01T10:00:00"],
            preview.Archives.Select(archive => archive.Name));
        Assert.Equal(
            [BorgTexts.Format(Strings.PruneKept, BorgTexts.Format(Strings.PruneRuleFormat, Strings.RuleDaily, 1)),
             BorgTexts.Format(Strings.PruneKept, BorgTexts.Format(Strings.PruneRuleFormat, Strings.RuleDaily, 2)),
             Strings.PruneDeleted],
            preview.Archives.Select(archive => archive.StatusText));
        Assert.True(preview.Archives[2].IsPruned);
        var run = main.CurrentBackup!;
        Assert.True(run.IsCleanupOnly);
        Assert.Equal(BorgTexts.Format(Strings.CleanupTitle, "Documents"), run.Title);
        Assert.Equal(Strings.CleanupCancelled, run.ResultText);
        Assert.Equal(Strings.OutcomeCancelled, run.StatusText);
        Assert.False(main.IsBackupRunning);

        // Confirmed: deleted, then compacted.
        Borg.Calls.Clear();
        Dialogs.Passphrases.Enqueue(Passphrase);
        Dialogs.PruneAnswers.Enqueue(true);
        await main.CleanUpJobCommand.ExecuteAsync(null);

        Assert.Equal(["prune", "prune", "compact"], Borg.Commands);
        Assert.DoesNotContain("--dry-run", Borg.Calls[1].Arguments);
        Assert.DoesNotContain("Documents-host-2026-11-01T10:00:00", ArchiveNames);
        Assert.Contains("Documents-Old-host-2026-11-01T11:00:00", ArchiveNames);
        run = main.CurrentBackup!;
        Assert.Equal(new CleanupSummary { PrunedArchives = 1, FreedBytes = 1_500_000 }, run.Cleanup);
        Assert.Equal(BorgTexts.Format(Strings.CleanupResult, 1, BorgTexts.Size(1_500_000)), run.ResultText);
        Assert.Equal(Strings.CleanupDone, run.StatusText);
        Assert.Null(Assert.Single(_test.Jobs.Load()).LastRun); // cleaning up by hand is no backup run
    }

    [Fact]
    public async Task Cleaning_up_by_hand_without_anything_to_delete_asks_nothing()
    {
        Borg.PruneKeep = 10;
        var main = await StartAsync(Job(KeepTwoDaily));
        Dialogs.Passphrases.Enqueue(Passphrase);

        await main.CleanUpJobCommand.ExecuteAsync(null);

        Assert.Equal(["prune"], Borg.Commands);
        Assert.Empty(Dialogs.PrunePreviews);
        Assert.Equal(Strings.CleanupNothing, main.CurrentBackup!.ResultText);
    }

    [Fact]
    public async Task A_failing_preview_is_reported()
    {
        Borg.PruneError = "PassphraseWrong";
        var main = await StartAsync(Job(KeepTwoDaily));
        Dialogs.Passphrases.Enqueue("wrong");

        await main.CleanUpJobCommand.ExecuteAsync(null);

        Assert.Empty(Dialogs.PrunePreviews);
        Assert.Equal(BorgTexts.Format(Strings.CleanupFailed, Strings.BorgErrorPassphraseWrong), main.CurrentBackup!.ResultText);
        Assert.Equal(Strings.OutcomeFailed, main.CurrentBackup.StatusText);
    }

    [Fact]
    public async Task Freeing_space_compacts_the_repository_without_a_passphrase()
    {
        var main = await StartAsync(Job(retention: null));
        Assert.True(main.FreeSpaceCommand.CanExecute(null));

        await main.FreeSpaceCommand.ExecuteAsync(null);

        Assert.Equal(["compact"], Borg.Commands);
        Assert.Equal("", Borg.Calls[0].Passphrase);
        Assert.Contains("--progress", Borg.Calls[0].Arguments);
        Assert.Equal(BorgTexts.Format(Strings.SpaceFreed, BorgTexts.Size(1_500_000)), main.SelectedRepository!.ResultText);
        Assert.False(main.IsWorking);
        Assert.False(main.IsWorkCancellable);
        Assert.Null(main.WorkingProgress);
    }

    [Fact]
    public async Task Freeing_space_can_be_cancelled()
    {
        var main = await StartAsync(Job(retention: null));
        Borg.BeforeCompact = () =>
        {
            Assert.True(main.IsWorkCancellable);
            main.CancelWorkCommand.Execute(null);
        };

        await main.FreeSpaceCommand.ExecuteAsync(null);

        Assert.Equal(Strings.SpaceFreeCancelled, main.SelectedRepository!.ResultText);
        Assert.False(main.SelectedRepository.HasError);
    }

    [Fact]
    public void The_editor_offers_the_suggested_rules_when_clean_up_is_switched_on()
    {
        var editor = new BackupJobEditorViewModel(_repository, FakeBorg.Installation, Dialogs) { Name = "Docs" };
        editor.Sources.Add(Source);
        Assert.False(editor.CleanUp);
        Assert.Null(editor.KeepDaily);

        editor.CleanUp = true;
        Assert.Equal<decimal?[]>([7, 4, 6, 0, 0, 0, 0],
            [editor.KeepDaily, editor.KeepWeekly, editor.KeepMonthly, editor.KeepYearly, editor.KeepHourly, editor.KeepLast, editor.KeepWithinDays]);

        editor.KeepDaily = editor.KeepWeekly = editor.KeepMonthly = 0;
        editor.SaveCommand.Execute(null);
        Assert.Contains(Strings.JobEditorRetentionRequired, editor.Errors);
        Assert.Null(editor.Result);

        editor.KeepYearly = 2;
        editor.KeepWithinDays = 10;
        editor.SaveCommand.Execute(null);
        Assert.Equal(new RetentionPolicy { Yearly = 2, WithinDays = 10 }, editor.Result!.Retention);

        // Editing keeps the rules; switching clean-up off drops them.
        var again = new BackupJobEditorViewModel(_repository, FakeBorg.Installation, Dialogs, editor.Result);
        Assert.True(again.CleanUp);
        Assert.Equal<decimal?[]>([2, 10, 0], [again.KeepYearly, again.KeepWithinDays, again.KeepDaily]);
        again.CleanUp = false;
        again.SaveCommand.Execute(null);
        Assert.Null(again.Result!.Retention);
    }
}
