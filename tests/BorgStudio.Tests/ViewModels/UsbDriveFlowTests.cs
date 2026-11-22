using BorgStudio.App.Services;
using BorgStudio.App.ViewModels;
using BorgStudio.Core.Backups;
using BorgStudio.Core.Borg;
using BorgStudio.Core.Repositories;
using BorgStudio.Plugins;
using BorgStudio.Providers.Usb;
using BorgStudio.Tests.TestSupport;

namespace BorgStudio.Tests.ViewModels;

/// <summary>A repository on a USB drive: found under another letter, missing, ejected after automatic runs.</summary>
public sealed class UsbDriveFlowTests : IDisposable
{
    private const string Id = "aa11bb22cc33dd44ee55ff6600112233445566778899aabbccddeeff00112233";

    private readonly TestServices _test = new();
    private RepositoryConfig _repository;
    private readonly BackupJob _job;

    public UsbDriveFlowTests()
    {
        _test.Drives.Attached.Add(new DriveRoot(Drive("F"), IsNetwork: false));
        _repository = new RepositoryConfig
        {
            Id = Guid.NewGuid(),
            Name = "Stick",
            ProviderId = UsbRepositoryProvider.ProviderId,
            ProviderValues = new Dictionary<string, string> { [UsbRepositoryProvider.PathKey] = LastSeen, [UsbRepositoryProvider.EjectKey] = "true" },
            PassphraseMode = PassphraseMode.None,
            EncryptionMode = "none",
            BorgRepositoryId = Id,
        };
        _test.Store.Save([_repository]);
        Directory.CreateDirectory(Source);
        _job = new BackupJob
        {
            Id = Guid.NewGuid(), Name = "Documents", RepositoryId = _repository.Id, Sources = [Source],
            Exclusions = new BackupExclusions { CachesAndTemp = false },
        };
        _test.Jobs.Save([_job]);
    }

    public void Dispose() => _test.Dispose();

    private string Drive(string letter)
    {
        var root = _test.Directory.Combine("drives", letter);
        Directory.CreateDirectory(root);
        return root;
    }

    private string LastSeen => Path.Combine(_test.Directory.Combine("drives", "E"), "Backups", "repo");

    private string Moved => Path.Combine(Drive("F"), "Backups", "repo");

    private string Source => _test.Directory.Combine("Documents");

    private static void PutRepository(string folder)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "config"), $"[repository]\nversion = 1\nid = {Id}\n");
    }

    private string? CreatedIn => _test.Borg.Calls.LastOrDefault(call => call.Arguments[0] == "create").Arguments?
        .FirstOrDefault(argument => argument.Contains("::", StringComparison.Ordinal))?.Split("::")[0];

    private ScheduledJobRunner Runner => new(_test.Services, MainViewModel.CreateScheduledAccess(_test.Services));

    private async Task<MainViewModel> StartAsync()
    {
        var main = new MainViewModel(_test.Services);
        await main.CheckBorgCommand.ExecuteAsync(null);
        main.ListedRepository = main.Repositories[0];
        main.SelectedDetailsTab = MainViewModel.JobsTab;
        main.SelectedJob = main.RepositoryJobs[0];
        return main;
    }

    [Fact]
    public async Task A_backup_by_hand_finds_the_drive_under_its_new_letter_and_keeps_it()
    {
        PutRepository(Moved);
        var main = await StartAsync();

        await main.RunJobCommand.ExecuteAsync(null);

        Assert.Equal(Moved, CreatedIn);
        Assert.Equal(Moved, main.Repositories[0].Config.ProviderValues[UsbRepositoryProvider.PathKey]);
        Assert.Equal(Moved, main.Repositories[0].LocationText);
        Assert.Equal(Moved, _test.Store.Load().Repositories.Single().ProviderValues[UsbRepositoryProvider.PathKey]);
        // By hand: never ejected.
        Assert.Empty(_test.Drives.Ejected);
    }

    [Fact]
    public async Task Without_the_drive_the_repository_says_so_and_borg_is_not_asked()
    {
        var main = await StartAsync();

        await main.RunJobCommand.ExecuteAsync(null);

        Assert.DoesNotContain(_test.Borg.Calls, call => call.Arguments[0] == "create");
        Assert.True(main.Repositories[0].HasError);
        Assert.Contains(LastSeen, main.Repositories[0].ResultText);
    }

    [Fact]
    public async Task A_scheduled_backup_ejects_the_drive_when_it_is_done()
    {
        PutRepository(LastSeen);

        var result = await Runner.RunAsync(_job.Id);

        Assert.Equal(BackupOutcome.Succeeded, result.Summary.Outcome);
        Assert.Equal([LastSeen], _test.Drives.Ejected);
        Assert.False(_test.Services.Connections.IsConnected(_repository.Id));

        // Refused (in use): the run stays a success, the reason goes to the log.
        _test.Drives.EjectProblem = "the drive is still in use";
        result = await Runner.RunAsync(_job.Id);
        Assert.Equal(BackupOutcome.Succeeded, result.Summary.Outcome);
        Assert.Contains("Stick: the drive is still in use", File.ReadAllText(_test.Directory.Combine("logs", "scheduled.log")));
    }

    [Fact]
    public async Task A_scheduled_backup_without_the_drive_is_recorded_as_target_missing()
    {
        var result = await Runner.RunAsync(_job.Id);

        Assert.Equal(BackupSkipReason.TargetMissing, result.Summary.SkipReason);
        Assert.Empty(_test.Drives.Ejected);
        Assert.Contains(LastSeen, File.ReadAllText(_test.Directory.Combine("logs", "scheduled.log")));
    }

    [Fact]
    public async Task Switched_off_the_drive_stays_attached()
    {
        _repository = _repository with
        {
            ProviderValues = new Dictionary<string, string> { [UsbRepositoryProvider.PathKey] = LastSeen, [UsbRepositoryProvider.EjectKey] = "false" },
        };
        _test.Store.Save([_repository]);
        PutRepository(LastSeen);

        await Runner.RunAsync(_job.Id);

        Assert.Empty(_test.Drives.Ejected);
    }

    [Fact]
    public async Task A_local_repository_on_an_external_drive_can_be_kept_as_a_usb_drive()
    {
        var local = new RepositoryConfig
        {
            Id = Guid.NewGuid(), Name = "Disk", ProviderId = "local", PassphraseMode = PassphraseMode.None, EncryptionMode = "none",
            ProviderValues = new Dictionary<string, string> { ["path"] = Moved },
        };
        var editor = new RepositoryEditorViewModel(_test.Services, FakeBorg.Installation, local);
        await editor.Loading;
        Assert.False(editor.CanConvertToUsb); // an internal drive

        _test.Drives.External.Add(Drive("F"));
        editor = new RepositoryEditorViewModel(_test.Services, FakeBorg.Installation, local);
        await editor.Loading;
        Assert.True(editor.ShowConvertToUsb);

        editor.ConvertToUsbCommand.Execute(null);
        Assert.False(editor.ShowConvertToUsb);
        Assert.Equal(UsbRepositoryProvider.ProviderId, editor.SelectedProvider!.Provider.Id);
        var eject = editor.Fields.Single(field => field.Key == UsbRepositoryProvider.EjectKey);
        Assert.True(eject.IsToggle);
        Assert.False(eject.IsOn);
        eject.IsOn = true;
        await editor.ConfirmCommand.ExecuteAsync(null);

        var saved = editor.Result!.Repository;
        Assert.Equal(UsbRepositoryProvider.ProviderId, saved.ProviderId);
        Assert.Equal(local.Id, saved.Id);
        Assert.Equal(Moved, saved.ProviderValues[UsbRepositoryProvider.PathKey]);
        Assert.Equal("true", saved.ProviderValues[UsbRepositoryProvider.EjectKey]);
    }

    [Fact]
    public async Task Inside_wsl_a_drive_attached_later_is_mounted_for_borg_and_unmounted_after()
    {
        var borg = new BorgClient(_test.Borg, FakeBorg.WslExecutable);
        var installation = FakeBorg.Installation with { Runtime = BorgRuntime.Wsl };
        _test.Borg.WslDrivesMissing.Add('e');

        await borg.InfoAsync(installation, new RepositoryLocation(@"E:\Backups\repo"), null);

        Assert.Equal(2, _test.Borg.RootCommands.Count);
        Assert.Contains("for d in e;", _test.Borg.RootCommands[0]);
        Assert.Contains("mount -t drvfs", _test.Borg.RootCommands[0]);
        Assert.StartsWith("for d in e; do umount /mnt/$d", _test.Borg.RootCommands[1]);
        Assert.Contains(_test.Borg.Calls, call => call.Arguments.Contains("/mnt/e/Backups/repo"));

        // Mounted by WSL itself (or the system drive): left alone.
        _test.Borg.RootCommands.Clear();
        _test.Borg.WslDrivesMissing.Clear();
        await borg.InfoAsync(installation, new RepositoryLocation(@"E:\Backups\repo"), null);
        await borg.InfoAsync(installation, new RepositoryLocation(@"C:\Backups\repo"), null);
        Assert.Single(_test.Borg.RootCommands);
        Assert.DoesNotContain("umount /mnt", _test.Borg.RootCommands[0]);
    }
}
