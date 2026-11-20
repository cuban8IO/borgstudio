using BorgStudio.App.Services;
using BorgStudio.App.ViewModels;
using BorgStudio.Core.Backups;
using BorgStudio.Core.Borg;
using BorgStudio.Core.Checks;
using BorgStudio.Core.Repositories;
using BorgStudio.Plugins;
using BorgStudio.Tests.TestSupport;

namespace BorgStudio.Tests.ViewModels;

/// <summary>Upload limit and low priority per repository – the rules, borg's arguments, the editor.</summary>
public sealed class LimitFlowTests : IDisposable
{
    private readonly TestServices _test = new();
    private RepositoryConfig _repository;

    public LimitFlowTests()
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
    }

    public void Dispose() => _test.Dispose();

    private string Source => _test.Directory.Combine("Documents");

    private FakeBorg Borg => _test.Borg;

    private static readonly DateTime Noon = new(2026, 11, 20, 12, 0, 0);

    [Fact]
    public void The_upload_limit_applies_to_servers_within_its_window()
    {
        var limits = new TransferLimits { UploadMegabytesPerSecond = 2.5, OnlyBetween = true, From = new TimeOnly(8, 0), To = new TimeOnly(18, 0) };

        Assert.Equal(new BorgThrottle(2560, false), limits.ThrottleAt(Noon, server: true));
        Assert.Equal(BorgThrottle.None, limits.ThrottleAt(Noon.AddHours(7), server: true));
        Assert.Equal(BorgThrottle.None, limits.ThrottleAt(Noon, server: false));

        var overnight = limits with { From = new TimeOnly(22, 0), To = new TimeOnly(6, 0) };
        Assert.Null(overnight.ThrottleAt(Noon, server: true).UploadKibibytesPerSecond);
        Assert.Equal(2560, overnight.ThrottleAt(Noon.AddHours(11), server: true).UploadKibibytesPerSecond);
        Assert.Equal(2560, overnight.ThrottleAt(Noon.AddHours(-9), server: true).UploadKibibytesPerSecond);

        Assert.Equal(new BorgThrottle(null, true), new TransferLimits { LowPriority = true }.ThrottleAt(Noon, server: false));
        Assert.Equal(2560, (limits with { OnlyBetween = false }).ThrottleAt(Noon.AddHours(10), server: true).UploadKibibytesPerSecond);
    }

    [Fact]
    public async Task borg_gets_the_rate_and_runs_under_nice()
    {
        var client = new BorgClient(Borg, FakeBorg.WslExecutable) { NativeIsPosix = true };
        var location = new RepositoryLocation("ssh://me@nas/./repo");

        await client.CreateAsync(FakeBorg.Installation, location, "a", ["/home/me"], [], null, throttle: new BorgThrottle(2048, true));
        await client.CheckAsync(FakeBorg.Installation, location, CheckDepth.Normal, null, throttle: new BorgThrottle(512, false));

        var create = Borg.Calls.Single(call => call.Arguments[0] == "create").Arguments.ToList();
        Assert.Equal(["create", "--log-json", "--upload-ratelimit", "2048", "--progress"], create.Take(5));
        Assert.Equal(["create"], Borg.NiceCalls);
        var check = Borg.Calls.Single(call => call.Arguments[0] == "check").Arguments;
        Assert.Equal(["check", "--log-json", "--upload-ratelimit", "512"], check.Take(4));
    }

    [Fact]
    public async Task A_backup_of_a_local_repository_runs_with_low_priority_but_without_a_rate()
    {
        _repository = _repository with { Limits = new TransferLimits { LowPriority = true, UploadMegabytesPerSecond = 5 } };
        _test.Store.Save([_repository]);
        var job = new BackupJob
        {
            Id = Guid.NewGuid(), Name = "Documents", RepositoryId = _repository.Id, Sources = [Source],
            Exclusions = new BackupExclusions { CachesAndTemp = false },
        };
        _test.Jobs.Save([job]);
        var main = new MainViewModel(_test.Services);
        await main.CheckBorgCommand.ExecuteAsync(null);
        main.ListedRepository = main.Repositories[0];
        main.SelectedDetailsTab = MainViewModel.JobsTab;
        main.SelectedJob = main.RepositoryJobs[0];

        await main.RunJobCommand.ExecuteAsync(null);

        Assert.Contains("create", Borg.NiceCalls);
        Assert.DoesNotContain("--upload-ratelimit", Borg.Calls.Single(call => call.Arguments[0] == "create").Arguments);

        // Runs without window get the same.
        var (access, _) = await new ScheduledJobRunner(_test.Services, MainViewModel.CreateScheduledAccess(_test.Services)).PrepareAsync(_repository);
        Assert.Equal(new BorgThrottle(null, true), access!.Throttle);
    }

    [Fact]
    public async Task The_editor_keeps_the_limits()
    {
        _repository = _repository with { Limits = new TransferLimits { LowPriority = true, UploadMegabytesPerSecond = 3, OnlyBetween = true } };
        _test.Store.Save([_repository]);
        var editor = new RepositoryEditorViewModel(_test.Services, FakeBorg.Installation, _repository);
        Assert.True(editor.LowPriority);
        Assert.True(editor.LimitUpload);
        Assert.Equal(3, editor.UploadLimit);
        Assert.True(editor.UploadOnlyBetween);

        editor.LowPriority = false;
        editor.UploadTo = new TimeSpan(20, 0, 0);
        await editor.ConfirmCommand.ExecuteAsync(null);

        var limits = editor.Result!.Repository.Limits;
        Assert.False(limits.LowPriority);
        // A local folder: borg cannot limit writing there, so no rate is kept.
        Assert.Null(limits.UploadMegabytesPerSecond);
        Assert.Equal(new TimeOnly(20, 0), limits.To);
    }
}
