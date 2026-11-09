using BorgStudio.App.Resources;
using BorgStudio.App.ViewModels;
using BorgStudio.Core.Backups;
using BorgStudio.Core.Borg;
using BorgStudio.Core.Repositories;
using BorgStudio.Tests.TestSupport;

namespace BorgStudio.Tests.ViewModels;

/// <summary>Mounting archives and whole repositories from the "Archives" tab – with fake borg.</summary>
public sealed class MountFlowTests : IDisposable
{
    private const string Passphrase = "pw";
    private const string Older = "Documents-host-2026-11-05T10:00:00";
    private const string Newest = "Documents-host-2026-11-06T10:00:00";
    // ':' can't be shown by Windows: "My Disk_ 1/Documents-host-2026-11-06T10_00_00".
    private const string ArchiveMountPoint = "/home/me/borgstudio-mounts/My Disk_ 1/Documents-host-2026-11-06T10_00_00";
    private const string RepositoryMountPoint = "/home/me/borgstudio-mounts/My Disk_ 1/";

    private readonly TestServices _test;
    private readonly RepositoryConfig _repository;

    public MountFlowTests() : this(wsl: false)
    {
    }

    private MountFlowTests(bool wsl)
    {
        _test = new TestServices(wsl: wsl);
        _repository = new RepositoryConfig
        {
            Id = Guid.NewGuid(),
            Name = "My Disk: 1",
            ProviderId = "local",
            ProviderValues = new Dictionary<string, string> { ["path"] = _test.Directory.Combine("repo") },
            PassphraseMode = PassphraseMode.Ask,
            EncryptionMode = "repokey-blake2",
        };
        _test.Store.Save([_repository]);
        Borg.Archives.Add((Older, new DateTime(2026, 11, 5, 10, 0, 0)));
        Borg.Archives.Add((Newest, new DateTime(2026, 11, 6, 10, 0, 0)));
    }

    public void Dispose() => _test.Dispose();

    private FakeBorg Borg => _test.Borg;

    private FakeDialogs Dialogs => _test.Dialogs;

    private async Task<MainViewModel> StartAsync(BackupJob? job = null)
    {
        if (job is not null)
            _test.Jobs.Save([job]);
        var main = new MainViewModel(_test.Services);
        await main.CheckBorgCommand.ExecuteAsync(null);
        Dialogs.Passphrases.Enqueue(Passphrase);
        main.SelectedDetailsTab = MainViewModel.ArchivesTab;
        await main.ArchivesLoad;
        main.SelectedArchive = main.Archives[0];
        Borg.Calls.Clear();
        return main;
    }

    private static async Task MountArchiveAsync(MainViewModel main)
    {
        await main.MountArchiveCommand.ExecuteAsync(null);
        await main.MountTask;
    }

    [Fact]
    public async Task Mounts_the_selected_archive_opens_it_and_unmounts_it_again()
    {
        var main = await StartAsync();
        Assert.Equal(1, Borg.MountCleanUps); // mounts left behind by a crash, when borg was found
        Assert.Equal(Newest, main.SelectedArchive!.Name);
        Assert.True(main.MountArchiveCommand.CanExecute(null));

        await MountArchiveAsync(main);

        var mount = Assert.Single(Borg.Calls, call => call.Arguments[0] == "mount");
        Assert.Contains("-f", mount.Arguments);
        Assert.EndsWith("::" + Newest, mount.Arguments[^2]);
        Assert.Equal(ArchiveMountPoint, mount.Arguments[^1]); // "My Disk: 1" – no ':' for Windows
        Assert.Equal(Passphrase, mount.Passphrase);
        Assert.Equal("uid=1000,gid=1000", Assert.Single(Borg.MountOptions)); // natively no allow_other
        Assert.Equal([ArchiveMountPoint], Dialogs.OpenedFolders);
        var entry = Assert.Single(main.Mounts);
        Assert.Equal($"My Disk: 1 · {Newest}", entry.Title);
        Assert.True(main.HasMounts);
        Assert.Equal(Strings.MountsStatusOne, main.MountsText);
        Assert.False(main.IsMounting);
        Assert.False(main.MountProgress.IsActive);

        // Again: it is open already – just shown.
        await MountArchiveAsync(main);
        Assert.Single(Borg.Calls, call => call.Arguments[0] == "mount");
        Assert.Equal(2, Dialogs.OpenedFolders.Count);

        await entry.UnmountCommand.ExecuteAsync(null);

        Assert.Contains(Borg.Calls, call => call.Arguments is ["umount", ArchiveMountPoint]);
        Assert.Empty(Borg.Mounted);
        Assert.Empty(main.Mounts);
        Assert.False(main.HasMounts);
        Assert.Null(main.Notice); // unmounted by us: nothing to report
    }

    [Fact]
    public async Task The_whole_repository_mounts_next_to_an_archive()
    {
        var main = await StartAsync();
        await MountArchiveAsync(main);

        await main.MountRepositoryCommand.ExecuteAsync(null);
        await main.MountTask;

        var mount = Borg.Calls.Last(call => call.Arguments[0] == "mount");
        Assert.DoesNotContain("::", mount.Arguments[^2]);
        Assert.Equal(RepositoryMountPoint + Strings.MountAllFolder, mount.Arguments[^1]);
        Assert.Equal(2, main.Mounts.Count);
        Assert.Equal(BorgTexts.Format(Strings.MountAllTitle, "My Disk: 1"), main.Mounts[1].Title);
        Assert.Equal(BorgTexts.Format(Strings.MountsStatus, 2), main.MountsText);

        // Closing the app: everything goes, even what is in use.
        Borg.BusyMounts.Add(ArchiveMountPoint);
        await main.UnmountAllAsync();
        Assert.Empty(main.Mounts);
        Assert.Empty(Borg.Mounted);
        Assert.Equal([ArchiveMountPoint], Borg.ForceUnmounted);
    }

    [Fact]
    public async Task A_folder_in_use_stays_mounted_and_says_why()
    {
        var main = await StartAsync();
        await MountArchiveAsync(main);
        Borg.BusyMounts.Add(ArchiveMountPoint);

        await main.Mounts[0].UnmountCommand.ExecuteAsync(null);

        Assert.Single(main.Mounts);
        Assert.Equal(BorgTexts.Format(Strings.MountBusy, main.Mounts[0].Title), Dialogs.Messages[^1]);
        Assert.Empty(Borg.ForceUnmounted);

        Borg.BusyMounts.Clear();
        await main.UnmountAllMountsCommand.ExecuteAsync(null);
        Assert.Empty(main.Mounts);
    }

    [Fact]
    public async Task Borg_stopping_on_its_own_removes_the_mount_and_tells_why()
    {
        var main = await StartAsync();
        await MountArchiveAsync(main);
        var title = main.Mounts[0].Title;

        Borg.EndMount(ArchiveMountPoint);
        await WaitForAsync(() => main.Mounts.Count == 0);

        Assert.StartsWith(BorgTexts.Format(Strings.MountEnded, title, Strings.BorgErrorConnectionFailed), main.Notice);
    }

    [Fact]
    public async Task Failing_or_cancelled_mounts_leave_nothing_behind()
    {
        var main = await StartAsync();
        Borg.MountError = "PassphraseWrong";

        await MountArchiveAsync(main);

        Assert.Empty(main.Mounts);
        Assert.Equal(BorgTexts.Format(Strings.MountFailed, Strings.BorgErrorPassphraseWrong), main.ArchivesError);
        Assert.Empty(Dialogs.OpenedFolders);

        // FUSE's own complaint (allow_other not allowed) is recognised.
        Borg.MountError = "fusermount3: option allow_other only allowed if 'user_allow_other' is set in /etc/fuse.conf";
        await MountArchiveAsync(main);
        Assert.StartsWith(BorgTexts.Format(Strings.MountFailed, Strings.BorgErrorMountNotAllowed), main.ArchivesError);

        // Cancelled while borg is still getting ready.
        Borg.MountError = null;
        Borg.MountsReady = false; // borg runs, but the folder is not there yet
        var mounting = main.MountArchiveCommand.ExecuteAsync(null);
        await WaitForAsync(() => Borg.Mounted.ContainsKey(ArchiveMountPoint) && main.MountProgress.CanCancel);
        Assert.Equal(BorgTexts.Format(Strings.Mounting, Newest), main.MountProgress.Text);
        Assert.True(main.IsMounting);
        Assert.False(main.MountArchiveCommand.CanExecute(null));
        main.MountProgress.CancelCommand.Execute(null);
        await mounting;
        await main.MountTask;
        Assert.Empty(main.Mounts);
        Assert.Empty(Borg.Mounted);
        Assert.Null(main.ArchivesError);
        Assert.False(main.IsMounting);
    }

    [Fact]
    public async Task A_backup_asks_to_unmount_the_repository_first()
    {
        var source = _test.Directory.Combine("Documents");
        Directory.CreateDirectory(source);
        var job = new BackupJob
        {
            Id = Guid.NewGuid(),
            Name = "Documents",
            RepositoryId = _repository.Id,
            Sources = [source],
            Exclusions = new BackupExclusions { CachesAndTemp = false },
        };
        var main = await StartAsync(job);
        await MountArchiveAsync(main);
        Assert.NotNull(main.SelectedJob);

        // Not now: no backup, still mounted.
        Dialogs.ConfirmAnswers.Enqueue(false);
        await main.RunJobCommand.ExecuteAsync(null);
        Assert.Equal(Strings.MountedBlocksTitle, Dialogs.Shown[^1]);
        Assert.Equal(BorgTexts.Format(Strings.MountedBlocksMessage, "My Disk: 1"), Dialogs.Messages[^1]);
        Assert.DoesNotContain("create", Borg.Commands);
        Assert.Single(main.Mounts);

        // Yes: unmounted, then backed up.
        Dialogs.ConfirmAnswers.Enqueue(true);
        Dialogs.Passphrases.Enqueue(Passphrase);
        await main.RunJobCommand.ExecuteAsync(null);
        Assert.Empty(main.Mounts);
        Assert.Contains("umount", Borg.Commands);
        Assert.Contains("create", Borg.Commands);
    }

    [Fact]
    public async Task Removing_the_repository_unmounts_its_archives()
    {
        var main = await StartAsync();
        await MountArchiveAsync(main);
        Dialogs.ConfirmAnswers.Enqueue(true);

        await main.RemoveRepositoryCommand.ExecuteAsync(null);

        Assert.Empty(main.Mounts);
        Assert.Empty(Borg.Mounted);
    }

    [Fact]
    public async Task In_WSL_Windows_needs_allow_other_which_the_app_sets_up_after_asking()
    {
        using var wsl = new MountFlowTests(wsl: true);
        await wsl.InWslAsync();
    }

    private async Task InWslAsync()
    {
        // Archives from the cache: borg itself can't read the test folder from "WSL" everywhere.
        _test.Archives.Save(_repository.Id, [new BorgArchive(Newest, FakeBorg.ArchiveId(Newest), new DateTimeOffset(2026, 11, 6, 10, 0, 0, TimeSpan.Zero))]);
        var main = new MainViewModel(_test.Services);
        await main.CheckBorgCommand.ExecuteAsync(null);
        Assert.Equal(BorgRuntime.Wsl, main.Borg!.Runtime);
        main.SelectedDetailsTab = MainViewModel.ArchivesTab;
        main.SelectedArchive = main.Archives[0];
        Borg.AllowOther = false;

        // Declined: nothing changed, nothing mounted.
        Dialogs.ConfirmAnswers.Enqueue(false);
        await MountArchiveAsync(main);
        Assert.Equal(Strings.MountSetupTitle, Dialogs.Shown[^1]);
        Assert.Contains(BorgClient.AllowOtherCommand, Dialogs.Messages[^1]);
        Assert.Empty(Borg.RootCommands);
        Assert.DoesNotContain("mount", Borg.Commands);

        // Failed (e.g. root not allowed): the command is on the clipboard and shown.
        Borg.CanEnableAllowOther = false;
        Dialogs.ConfirmAnswers.Enqueue(true);
        await MountArchiveAsync(main);
        Assert.Single(Borg.RootCommands);
        Assert.Equal(BorgClient.AllowOtherCommand, Dialogs.Clipboard);
        Assert.Equal(BorgTexts.Format(Strings.MountSetupFailed, BorgClient.AllowOtherCommand), Dialogs.Messages[^1]);
        Assert.DoesNotContain("mount", Borg.Commands);

        // Set up as root, then mounting goes on (here: the passphrase is asked for, and not given).
        Borg.CanEnableAllowOther = true;
        Dialogs.ConfirmAnswers.Enqueue(true);
        await MountArchiveAsync(main);
        Assert.Contains("user_allow_other >> /etc/fuse.conf", Borg.RootCommands[^1]);
        Assert.True(Borg.AllowOther);

        // Next time it is set up: no question.
        var shown = Dialogs.Shown.Count;
        await MountArchiveAsync(main);
        Assert.DoesNotContain(Strings.MountSetupTitle, Dialogs.Shown.Skip(shown));
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
            await Task.Delay(20);
        Assert.True(condition());
    }
}
