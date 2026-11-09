using BorgStudio.App.Resources;
using BorgStudio.App.ViewModels;
using BorgStudio.Core.Backups;
using BorgStudio.Core.Borg;
using BorgStudio.Core.Repositories;
using BorgStudio.Tests.TestSupport;

namespace BorgStudio.Tests.ViewModels;

/// <summary>Backup jobs: creating, running (progress, warnings, cancelling), deleting – with fake borg.</summary>
public sealed class BackupJobFlowTests : IDisposable
{
    private readonly TestServices _test = new();
    private readonly RepositoryConfig _repository;

    public BackupJobFlowTests()
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
        _test.Store.Save([_repository]);
        Directory.CreateDirectory(Source);
    }

    public void Dispose() => _test.Dispose();

    private string Source => _test.Directory.Combine("Documents");

    private FakeDialogs Dialogs => _test.Dialogs;

    private async Task<MainViewModel> StartAsync()
    {
        var main = new MainViewModel(_test.Services);
        await main.CheckBorgCommand.ExecuteAsync(null);
        _test.Borg.Calls.Clear();
        return main;
    }

    private BackupJob Job(string name = "Documents") => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        RepositoryId = _repository.Id,
        Sources = [Source],
        // The test folder lies in the temp folder on Windows: keep that template off.
        Exclusions = new BackupExclusions { CachesAndTemp = false, Extensions = [".iso"] },
    };

    [Fact]
    public async Task A_new_job_is_created_in_the_editor_and_saved()
    {
        var main = await StartAsync();
        Assert.False(main.HasRepositoryJobs);
        Dialogs.Folders.Enqueue(Source);
        Dialogs.JobEditor = async editor =>
        {
            Assert.Equal(BorgTexts.Format(Strings.JobEditorRepository, "Disk"), editor.RepositoryTitle);
            editor.SaveCommand.Execute(null);
            Assert.Contains(Strings.JobEditorNameRequired, editor.Errors);
            Assert.Contains(Strings.JobEditorSourceRequired, editor.Errors);

            editor.Name = "  Documents & Photos ";
            await editor.AddSourceCommand.ExecuteAsync(null);
            editor.CachesAndTemp = false;
            editor.Extensions = "iso, TMP";
            Assert.Contains(@"re:(?i)\.iso$", editor.PatternPreview);
            Assert.Contains("--exclude-caches", editor.PatternPreview);
            editor.SelectedCompression = editor.CompressionOptions.Single(option => option.Value == "lz4");
            editor.SaveCommand.Execute(null);
        };

        await main.NewJobCommand.ExecuteAsync(null);

        var item = Assert.Single(main.RepositoryJobs);
        Assert.Same(item, main.SelectedJob);
        Assert.Equal("Documents & Photos", item.Name);
        Assert.Equal(Strings.JobNeverRun, item.LastRunText);
        var saved = Assert.Single(_test.Jobs.Load());
        Assert.Equal([Source], saved.Sources);
        Assert.Equal([".iso", ".tmp"], saved.Exclusions.Extensions);
        Assert.Equal("lz4", saved.Compression);
        Assert.Equal(_repository.Id, saved.RepositoryId);
    }

    [Fact]
    public void The_editor_warns_about_sources_that_would_end_up_empty()
    {
        var temp = _test.Directory.Combine("AppData", "Local", "Temp", "work");
        Directory.CreateDirectory(temp);
        var editor = new BackupJobEditorViewModel(_repository, FakeBorg.Installation, Dialogs);

        editor.Sources.Add(temp);
        editor.Sources.Add(_test.Directory.Combine("gone"));

        Assert.Contains(BorgTexts.Format(Strings.JobEditorSourceExcluded, temp), editor.Warnings);
        Assert.Contains(BorgTexts.Format(Strings.JobEditorSourceMissing, _test.Directory.Combine("gone")), editor.Warnings);
    }

    [Fact]
    public async Task Running_a_job_shows_progress_and_keeps_the_outcome()
    {
        _test.Jobs.Save([Job()]);
        var main = await StartAsync();
        var states = new List<(long Original, string Path)>();
        _test.Borg.AfterCreateProgress = _ => states.Add((main.CurrentBackup!.OriginalSize, main.CurrentBackup.CurrentPath!));

        await main.RunJobCommand.ExecuteAsync(null);

        var call = Assert.Single(_test.Borg.Calls).Arguments;
        Assert.Equal("create", call[0]);
        Assert.Contains("--progress", call);
        Assert.Equal("--compression", call[call.ToList().IndexOf("--compression")]);
        Assert.Contains(@"re:(?i)\.iso$", call);
        Assert.Contains(call, argument => argument.EndsWith("::Documents-{hostname}-{now:%Y-%m-%dT%H:%M:%S}", StringComparison.Ordinal));
        Assert.Equal(Source, call[^1]);
        Assert.Equal([(1000, "home/me/file1"), (2000, "home/me/file2"), (3000, "home/me/file3")], states);

        var run = main.CurrentBackup!;
        Assert.False(main.IsBackupRunning);
        Assert.False(run.IsRunning);
        Assert.Equal(BackupOutcome.Succeeded, run.Summary!.Outcome);
        Assert.Contains("Documents-host-2026-11-06T10:00:00", run.ResultText);
        var saved = Assert.Single(_test.Jobs.Load()).LastRun!;
        Assert.Equal(BackupOutcome.Succeeded, saved.Outcome);
        Assert.Equal(3000, saved.OriginalSize);
        Assert.Equal(300, saved.DeduplicatedSize);
        Assert.Equal(30, saved.Files);
        Assert.StartsWith(Strings.JobLastRun[..Strings.JobLastRun.IndexOf('{')], main.SelectedJob!.LastRunText);
    }

    [Fact]
    public async Task Warnings_are_kept_and_shown()
    {
        _test.Jobs.Save([Job()]);
        _test.Borg.CreateWarnings.AddRange(Enumerable.Range(1, 25).Select(index => $"file{index}: Permission denied"));
        var main = await StartAsync();

        await main.RunJobCommand.ExecuteAsync(null);

        var run = main.CurrentBackup!;
        Assert.Equal(BackupOutcome.Warnings, run.Summary!.Outcome);
        Assert.True(run.HasWarnings);
        Assert.Equal(21, run.WarningsShown.Count);
        Assert.Equal(BorgTexts.Format(Strings.BackupMoreWarnings, 5), run.WarningsShown[^1]);
        Assert.Equal("file1: Permission denied", Assert.Single(_test.Jobs.Load()).LastRun!.Message);
        Assert.True(main.SelectedJob!.LastRunWarned);
    }

    [Fact]
    public async Task A_running_backup_can_be_cancelled()
    {
        _test.Jobs.Save([Job()]);
        var main = await StartAsync();
        _test.Borg.AfterCreateProgress = step =>
        {
            Assert.True(main.IsBackupRunning);
            Assert.False(main.TestRepositoryCommand.CanExecute(null)); // nothing else on the side
            if (step == 2)
                main.CancelBackupCommand.Execute(null);
        };

        await main.RunJobCommand.ExecuteAsync(null);

        var run = main.CurrentBackup!;
        Assert.Equal(BackupOutcome.Cancelled, run.Summary!.Outcome);
        Assert.Equal(Strings.BackupCancelledText, run.ResultText);
        Assert.Equal(2000, run.Summary.OriginalSize);
        Assert.Equal(BackupOutcome.Cancelled, Assert.Single(_test.Jobs.Load()).LastRun!.Outcome);
        Assert.True(main.TestRepositoryCommand.CanExecute(null));
        Assert.StartsWith(Strings.JobLastRunShort[..Strings.JobLastRunShort.IndexOf('{')], main.SelectedJob!.LastRunText);
        Assert.Contains(Strings.OutcomeCancelled, main.SelectedJob.LastRunText);
    }

    [Fact]
    public async Task Running_is_possible_once_borg_was_found()
    {
        _test.Jobs.Save([Job()]);
        var main = new MainViewModel(_test.Services);
        Assert.False(main.RunJobCommand.CanExecute(null));

        await main.CheckBorgCommand.ExecuteAsync(null);

        Assert.True(main.RunJobCommand.CanExecute(null));
    }

    [Fact]
    public async Task Jobs_are_deleted_on_their_own_or_with_their_repository()
    {
        _test.Jobs.Save([Job("A"), Job("B")]);
        var main = await StartAsync();
        Assert.Equal(["A", "B"], main.RepositoryJobs.Select(item => item.Name));

        Dialogs.ConfirmAnswers.Enqueue(true);
        await main.DeleteJobCommand.ExecuteAsync(null);
        Assert.Equal(["B"], _test.Jobs.Load().Select(job => job.Name));

        Dialogs.ConfirmAnswers.Enqueue(true);
        await main.RemoveRepositoryCommand.ExecuteAsync(null);
        Assert.Contains(Strings.RemoveJobsHint, Dialogs.Messages[^1]);
        Assert.Empty(_test.Jobs.Load());
        Assert.False(main.HasRepositoryJobs);
    }

    [Theory]
    [InlineData("Documents", "Documents")]
    [InlineData("  Dokumente & Fotos ", "Dokumente-Fotos")]
    [InlineData("a/b::c", "a-b-c")]
    [InlineData("///", "backup")]
    public void Job_names_become_safe_archive_prefixes(string name, string expected) =>
        Assert.Equal(expected, MainViewModel.ArchivePrefix(name));
}

/// <summary>Rates and remaining time of a running backup, with a clock under the test's control.</summary>
public sealed class BackupRunViewModelTests
{
    private readonly ManualTime _time = new();

    private BackupRunViewModel NewRun() =>
        new(new BackupJob { Id = Guid.NewGuid(), Name = "Docs", RepositoryId = Guid.NewGuid() }, _time);

    [Fact]
    public void Rates_and_remaining_time_follow_the_progress()
    {
        var run = NewRun();
        Assert.True(run.IsIndeterminate);
        Assert.Equal(Strings.BackupRemainingUnknown, run.RemainingText);

        run.Report(new BorgCreateProgress(0, 0, 0, 0, "a"));
        run.SetEstimate(100 * 1024 * 1024);
        Assert.False(run.IsIndeterminate);
        Assert.Equal(Strings.BackupRemainingCalculating, run.RemainingText);

        _time.Advance(TimeSpan.FromSeconds(10));
        run.Report(new BorgCreateProgress(20 * 1024 * 1024, 10 * 1024 * 1024, 5 * 1024 * 1024, 100, "b"));

        Assert.Equal(TimeSpan.FromSeconds(10), run.Elapsed);
        Assert.Equal(2 * 1024 * 1024, run.ReadRate!.Value, precision: 0);
        Assert.Equal(512 * 1024, run.UploadRate!.Value, precision: 0);
        Assert.Equal(0.2, run.Fraction, precision: 3);
        Assert.Equal(TimeSpan.FromSeconds(40), run.Remaining);
        Assert.Equal(BorgTexts.Format(Strings.BackupRemaining, "0:40"), run.RemainingText);
        Assert.Equal(BorgTexts.Format(Strings.BackupUpload, BorgTexts.Rate(512 * 1024), BorgTexts.Size(5 * 1024 * 1024)), run.UploadText);
        Assert.Equal(BorgTexts.Format(Strings.BackupProcessedOf, "20.0 MB".Replace(".", System.Globalization.CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator), "100 MB", 100),
            run.ProcessedText);
    }

    [Fact]
    public void Rates_look_at_the_last_seconds_only()
    {
        var run = NewRun();
        run.Report(new BorgCreateProgress(0, 0, 0, 0, null));
        _time.Advance(TimeSpan.FromSeconds(30));
        run.Report(new BorgCreateProgress(30_000, 0, 0, 0, null)); // 1000 B/s over the first 30 s
        _time.Advance(TimeSpan.FromSeconds(10));
        run.Report(new BorgCreateProgress(130_000, 0, 0, 0, null)); // then 10000 B/s

        Assert.Equal(10_000, run.ReadRate!.Value, precision: 0);
    }

    [Fact]
    public void Finishing_stops_the_estimate_and_keeps_the_summary()
    {
        var run = NewRun();
        run.SetEstimate(1000);
        run.Report(new BorgCreateProgress(500, 0, 0, 1, null));

        run.Finish(new BackupRunSummary
        {
            Started = run.Started,
            Duration = TimeSpan.FromSeconds(5),
            Outcome = BackupOutcome.Failed,
            Message = "Connection lost",
        }, []);
        run.Report(new BorgCreateProgress(900, 0, 0, 2, null));

        Assert.False(run.IsRunning);
        Assert.Equal(500, run.OriginalSize);
        Assert.Null(run.Remaining);
        Assert.Equal("Connection lost", run.ResultText);
        Assert.Equal(Strings.OutcomeFailed, run.StatusText);
    }
}
