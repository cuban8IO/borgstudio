using BorgStudio.App.Resources;
using BorgStudio.App.Services;
using BorgStudio.App.ViewModels;
using BorgStudio.Core.Backups;
using BorgStudio.Core.Borg;
using BorgStudio.Core.Repositories;
using BorgStudio.Core.Settings;
using BorgStudio.Providers.Usb;
using BorgStudio.Tests.TestSupport;

namespace BorgStudio.Tests.ViewModels;

/// <summary>A removable drive as what a job backs up: found by its id wherever it is attached, backed up from its root.</summary>
public sealed class SourceDriveFlowTests : IDisposable
{
    private const string Uuid = "8A3F-12C4";

    // Monday, 16 November 2026, 10:00.
    private static readonly DateTime Monday = new(2026, 11, 16, 10, 0, 0);

    private readonly TestServices _test = new();
    private readonly RepositoryConfig _repository;

    public SourceDriveFlowTests()
    {
        _repository = new RepositoryConfig
        {
            Id = Guid.NewGuid(),
            Name = "NAS",
            ProviderId = "local",
            ProviderValues = new Dictionary<string, string> { ["path"] = _test.Directory.Combine("repo") },
            PassphraseMode = PassphraseMode.None,
            EncryptionMode = "none",
        };
        _test.Store.Save([_repository]);
        Directory.CreateDirectory(_test.Directory.Combine("repo"));
        Directory.CreateDirectory(Path.Combine(Stick, "DCIM", "Thumbs"));
        Directory.CreateDirectory(Path.Combine(OtherLetter, "DCIM"));
        Directory.CreateDirectory(Card);
        _test.Drives.Attached.Add(new DriveRoot(_test.Directory.Combine("drives", "C"), IsNetwork: false));
    }

    public void Dispose() => _test.Dispose();

    /// <summary>The stick, as it is attached first.</summary>
    private string Stick => _test.Directory.Combine("drives", "E");

    /// <summary>The same stick attached later under another letter.</summary>
    private string OtherLetter => _test.Directory.Combine("drives", "G");

    /// <summary>A memory card without serial number.</summary>
    private string Card => _test.Directory.Combine("drives", "K");

    private SourceDriveLocator Locator => new(_test.Drives);

    private void Attach(string root, string? uuid = Uuid, string label = "KAMERA")
    {
        _test.Drives.Attached.Add(new DriveRoot(root, IsNetwork: false));
        _test.Drives.External.Add(root);
        if (uuid is not null)
            _test.Drives.VolumeIds[root] = uuid;
        _test.Drives.Labels[root] = label;
    }

    private void Pull(string root) => _test.Drives.Attached.RemoveAll(drive => drive.Path == root);

    private BackupJob Job(SourceDrive? drive = null, string name = "Kamera") => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        RepositoryId = _repository.Id,
        Drive = drive ?? new SourceDrive { Id = "volume:" + Uuid, Label = "KAMERA" },
        Exclusions = new BackupExclusions { CachesAndTemp = false, RecycleBin = false, CacheDirTag = false, Folders = [@"DCIM\Thumbs"] },
    };

    private IReadOnlyList<string> CreateCall => _test.Borg.Calls.Single(call => call.Arguments[0] == "create").Arguments;

    private string? CreateFolder => _test.Borg.WorkingDirectories[_test.Borg.Calls.FindIndex(call => call.Arguments[0] == "create")];

    private async Task<MainViewModel> StartAsync()
    {
        var main = new MainViewModel(_test.Services);
        await main.CheckBorgCommand.ExecuteAsync(null);
        _test.Borg.Calls.Clear();
        _test.Borg.WorkingDirectories.Clear();
        return main;
    }

    [Fact]
    public async Task The_editor_offers_the_attached_drives_and_keeps_the_one_chosen_by_its_id()
    {
        Attach(Stick);
        var editor = new BackupJobEditorViewModel(_repository, FakeBorg.Installation, _test.Dialogs, drives: Locator);
        await editor.DrivesLoaded;

        Assert.True(editor.CanBackUpDrive);
        var option = Assert.Single(editor.AttachedDrives);
        Assert.Equal(Stick, option.Root);
        Assert.Contains("KAMERA", option.Text);
        Assert.False(editor.DriveNeedsIdFile);

        editor.Name = "Kamera";
        editor.IsDriveSource = true;
        editor.SomeDriveFolders = true;
        await editor.SaveCommand.ExecuteAsync(null);
        Assert.Contains(Strings.JobEditorDriveFoldersRequired, editor.Errors);

        // Folders on the drive are kept relative to its root; one elsewhere is refused.
        _test.Dialogs.Folders.Enqueue(Path.Combine(Stick, "DCIM"));
        _test.Dialogs.Folders.Enqueue(_test.Directory.Combine("drives", "C"));
        _test.Dialogs.Folders.Enqueue(Path.Combine(Stick, "DCIM", "Thumbs"));
        await editor.AddDriveFolderCommand.ExecuteAsync(null);
        await editor.AddDriveFolderCommand.ExecuteAsync(null);
        await editor.AddExcludedFolderCommand.ExecuteAsync(null);
        Assert.Equal(["DCIM"], editor.DriveFolders);
        Assert.Single(_test.Dialogs.Messages);
        Assert.Equal([Path.Combine("DCIM", "Thumbs")], editor.ExcludedFolders);
        Assert.Contains("pp:DCIM/Thumbs", editor.PatternPreview);
        Assert.Contains("pp:System Volume Information", editor.PatternPreview);
        editor.DrivePlugIn = true;
        editor.DriveEject = true;
        await editor.SaveCommand.ExecuteAsync(null);

        var job = editor.Result!;
        Assert.Empty(job.Sources);
        Assert.Equal(new SourceDrive { Id = "volume:" + Uuid, Label = "KAMERA" }.Id, job.Drive!.Id);
        Assert.Equal("KAMERA", job.Drive.Label);
        Assert.Equal(["DCIM"], job.Drive.Folders);
        Assert.True(job.Drive.Eject);
        Assert.Equal(new PlugInBackup { Enabled = true, MinimumHours = PlugInBackup.DefaultMinimumHours }, job.Drive.PlugIn);
        Assert.False(File.Exists(Path.Combine(Stick, DriveIdentity.MarkerFile)));
    }

    [Fact]
    public async Task A_drive_without_uuid_gets_an_id_file_and_is_found_by_it()
    {
        Attach(Card, uuid: null, label: "");
        var editor = new BackupJobEditorViewModel(_repository, FakeBorg.Installation, _test.Dialogs, drives: Locator);
        await editor.DrivesLoaded;
        Assert.True(editor.DriveNeedsIdFile);
        Assert.Contains(Strings.SourceDriveUnnamed, editor.SelectedDrive!.Text);

        editor.Name = "Karte";
        editor.IsDriveSource = true;
        await editor.SaveCommand.ExecuteAsync(null);

        var drive = editor.Result!.Drive!;
        Assert.StartsWith("file:", drive.Id, StringComparison.Ordinal);
        Assert.True(drive.IsWholeDrive);
        Assert.True(File.Exists(Path.Combine(Card, DriveIdentity.MarkerFile)));
        Assert.Equal(Card, await Locator.FindAsync(drive));

        // Opened again: the same drive is chosen, nothing written again.
        var again = new BackupJobEditorViewModel(_repository, FakeBorg.Installation, _test.Dialogs, editor.Result, Locator);
        await again.DrivesLoaded;
        Assert.True(again.IsDriveSource);
        Assert.Equal(drive.Id, again.SelectedDrive!.Id);
        Assert.False(again.DriveNeedsIdFile);
    }

    [Fact]
    public async Task The_editor_shows_the_jobs_drive_when_it_is_not_attached()
    {
        var editor = new BackupJobEditorViewModel(_repository, FakeBorg.Installation, _test.Dialogs, Job(), Locator);
        await editor.DrivesLoaded;

        Assert.Equal(BorgTexts.Format(Strings.JobEditorDriveNotAttached, "KAMERA"), editor.SelectedDrive!.Text);
        Assert.Null(editor.SelectedDrive.Root);
        editor.Name = "Kamera";
        await editor.SaveCommand.ExecuteAsync(null);
        Assert.Equal("volume:" + Uuid, editor.Result!.Drive!.Id);
    }

    [Fact]
    public async Task A_run_backs_up_from_the_drives_root_wherever_it_is_attached()
    {
        Attach(Stick);
        _test.Jobs.Save([Job()]);
        var main = await StartAsync();

        await main.RunJobCommand.ExecuteAsync(null);

        Assert.Equal(".", CreateCall[^1]);
        Assert.Equal(Stick, CreateFolder);
        Assert.Contains("pp:DCIM/Thumbs", CreateCall);
        Assert.Contains("pp:System Volume Information", CreateCall);
        Assert.Contains(CreateCall, argument => argument.EndsWith("::Kamera-{hostname}-{now:%Y-%m-%dT%H:%M:%S}", StringComparison.Ordinal));
        Assert.Equal(Strings.SourceDriveWhole.Replace("{0}", "KAMERA"), main.RepositoryJobs[0].SourcesText);

        // Attached under another letter next time: found there by its id.
        Pull(Stick);
        Attach(OtherLetter);
        _test.Borg.Calls.Clear();
        _test.Borg.WorkingDirectories.Clear();
        await main.RunJobCommand.ExecuteAsync(null);
        Assert.Equal(OtherLetter, CreateFolder);
    }

    [Fact]
    public async Task Only_the_chosen_folders_are_backed_up_relative_to_the_root()
    {
        Attach(Stick);
        _test.Jobs.Save([Job(new SourceDrive { Id = "volume:" + Uuid, Label = "KAMERA", Folders = [@"DCIM", @"Projects\2026"] })]);
        var main = await StartAsync();

        await main.RunJobCommand.ExecuteAsync(null);

        Assert.Equal(["DCIM", "Projects/2026"], CreateCall.TakeLast(2));
        Assert.Equal(Stick, CreateFolder);
    }

    [Fact]
    public async Task Without_the_drive_a_run_by_hand_says_so_and_does_nothing()
    {
        Attach(Stick, uuid: "1111-2222"); // another stick
        _test.Jobs.Save([Job()]);
        var main = await StartAsync();

        await main.RunJobCommand.ExecuteAsync(null);

        Assert.Empty(_test.Borg.Calls);
        Assert.Equal(BorgTexts.Format(Strings.SourceDriveMissing, "KAMERA"), Assert.Single(_test.Dialogs.Messages));
    }

    [Fact]
    public async Task A_scheduled_run_without_the_drive_is_skipped_as_a_condition_not_a_failure()
    {
        var job = Job() with { Schedules = [new BackupSchedule { Kind = ScheduleKind.Daily, Time = new TimeOnly(10, 30) }] };
        _test.Jobs.Save([job]);
        var runner = new ScheduledJobRunner(_test.Services, MainViewModel.CreateScheduledAccess(_test.Services));

        Assert.Equal(BackupSkipReason.SourceMissing, await runner.CheckConditionsAsync(job));
        var result = await runner.RunAsync(job.Id);

        Assert.Equal(BackupOutcome.Skipped, result.Summary.Outcome);
        Assert.Equal(BackupSkipReason.SourceMissing, result.Summary.SkipReason);
        Assert.Empty(_test.Borg.Calls);
        Assert.True(BackupSkipReason.SourceMissing.IsCondition());

        // Attached: it may run, and it is ejected afterwards if the job wants that.
        Attach(Stick);
        _test.Jobs.Save([job with { Drive = job.Drive! with { Eject = true } }]);
        Assert.Null(await runner.CheckConditionsAsync(job));
        result = await runner.RunAsync(job.Id);
        Assert.Equal(BackupOutcome.Succeeded, result.Summary.Outcome);
        Assert.Equal(Stick, result.SourceRoot);
        Assert.Equal([Stick], _test.Drives.Ejected);
    }

    [Fact]
    public async Task Attaching_the_drive_asks_and_backs_up_its_jobs_then_ejects_it_once()
    {
        var start = DateTime.Now;
        var now = start;
        var drive = new SourceDrive { Id = "volume:" + Uuid, Label = "KAMERA", Eject = true, PlugIn = new PlugInBackup { Enabled = true } };
        _test.Jobs.Save([Job(drive, "Fotos"), Job(drive with { Folders = ["Videos"] }, "Videos"), Job(drive with { PlugIn = new PlugInBackup() }, "Ohne")]);
        _test.Settings.Save(new AppSettings { ScheduleRunner = ScheduleRunner.Background });
        var main = new MainViewModel(_test.Services) { SchedulerExecutable = "BorgStudio.exe", Clock = () => now };
        await main.CheckBorgCommand.ExecuteAsync(null);
        await main.SchedulesApplied;
        _test.Borg.Calls.Clear();
        main.DriveTick();

        Attach(Stick);
        main.DriveTick();
        await main.PlugInRun;

        var (title, message) = Assert.Single(_test.Dialogs.OnTop);
        Assert.Equal(Strings.PlugInTitle, title);
        Assert.Equal(BorgTexts.Format(Strings.PlugInSourceQuestion, "KAMERA", "Fotos, Videos"), message);
        Assert.Equal(2, _test.Borg.Calls.Count(call => call.Arguments[0] == "create"));
        Assert.Equal([Stick], _test.Drives.Ejected);

        // Attached again soon after: backed up within the 12 hours, nothing asked.
        Pull(Stick);
        main.DriveTick();
        now = start.AddHours(2);
        Attach(Stick);
        main.DriveTick();
        await main.PlugInRun;
        Assert.Single(_test.Dialogs.OnTop);
    }

    [Fact]
    public async Task Trying_the_job_by_hand_does_not_keep_attaching_from_asking()
    {
        var drive = new SourceDrive { Id = "volume:" + Uuid, Label = "KAMERA", PlugIn = new PlugInBackup { Enabled = true } };
        _test.Jobs.Save([Job(drive, "Fotos")]);
        _test.Settings.Save(new AppSettings { ScheduleRunner = ScheduleRunner.Background });
        Attach(Stick);
        var main = new MainViewModel(_test.Services) { SchedulerExecutable = "BorgStudio.exe", Clock = () => DateTime.Now };
        await main.CheckBorgCommand.ExecuteAsync(null);
        await main.SchedulesApplied;
        main.DriveTick();
        await main.RunJobCommand.ExecuteAsync(null);
        Assert.Equal(BackupOutcome.Succeeded, _test.Jobs.Load()[0].LastRun!.Outcome);

        Pull(Stick);
        main.DriveTick();
        Attach(Stick);
        main.DriveTick();
        await main.PlugInRun;

        Assert.Single(_test.Dialogs.OnTop);
        Assert.True(_test.Jobs.Load()[0].LastRun!.Automatic);
    }

    [Fact]
    public void Archives_of_a_drive_have_relative_paths_and_no_original_place()
    {
        var tree = new ArchiveTree();
        tree.Add(new BorgItem(".", ArchiveEntryKind.Directory, 0, DateTimeOffset.UnixEpoch));
        tree.Add(new BorgItem("DCIM", ArchiveEntryKind.Directory, 0, DateTimeOffset.UnixEpoch));
        tree.Add(new BorgItem("DCIM/a.jpg", ArchiveEntryKind.File, 5, DateTimeOffset.UnixEpoch));
        tree.Complete();

        Assert.True(tree.IsRelative);
        Assert.Equal(["DCIM"], tree.Root.Children.Select(child => child.Name));
        var file = tree.Find("DCIM")!.Children[0];
        Assert.True(ArchiveTree.IsRelativeNode(file));
        Assert.False(RestorePlan.CanRestoreToOriginal([file], BorgRuntime.Native));
        Assert.Equal(0, MainViewModel.SharedFolderDepth(tree));

        // Kept like that in the contents cache.
        _test.Archives.SaveContents(_repository.Id, "abcdef0123456789", tree);
        Assert.True(_test.Archives.LoadContents(_repository.Id, "abcdef0123456789")!.IsRelative);
    }

    [Theory]
    [InlineData(@"E:\", @"E:\DCIM\Thumbs", @"DCIM\Thumbs")]
    [InlineData(@"E:\", @"E:\", null)]
    [InlineData(@"E:\", @"F:\DCIM", null)]
    [InlineData(@"E:\Photos", @"E:\Photos2\x", null)]
    public void Folders_below_a_drive_are_kept_relative_to_it(string root, string path, string? expected)
    {
        if (!OperatingSystem.IsWindows())
            return;
        Assert.Equal(expected, SourceDrive.Below(root, path));
    }
}
