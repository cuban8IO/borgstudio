using BorgStudio.Core.Backups;
using BorgStudio.Plugins;
using BorgStudio.Tests.TestSupport;

namespace BorgStudio.Tests.Backups;

/// <summary>The conditions of scheduled runs and what the computer tells about itself.</summary>
public sealed class RunConditionTests : IDisposable
{
    private readonly TemporaryDirectory _directory = new();

    public void Dispose() => _directory.Dispose();

    private static readonly RepositoryLocation Server = new("ssh://me@nas:22/./repo") { Ssh = new SshEndpoint("nas", 22, "me", "./repo") };

    private static readonly RepositoryLocation Disk = new("/media/usb/repo");

    private static readonly RunConditions All = new() { NotOnBattery = true, NotMetered = true, WiredOnly = true, TargetReachable = true };

    [Fact]
    public async Task Each_condition_holds_a_run_back()
    {
        var system = new FakeSystemState();
        Assert.Null(await All.CheckAsync(system, Server));

        system.OnBattery = true;
        Assert.Equal(BackupSkipReason.OnBattery, await All.CheckAsync(system, Server));
        system.OnBattery = false;
        system.Metered = true;
        Assert.Equal(BackupSkipReason.MeteredConnection, await All.CheckAsync(system, Server));
        system.Metered = false;
        system.Wired = false;
        Assert.Equal(BackupSkipReason.NotWired, await All.CheckAsync(system, Server));
        system.Wired = true;
        system.Reachable = false;
        Assert.Equal(BackupSkipReason.TargetUnreachable, await All.CheckAsync(system, Server));
    }

    [Fact]
    public async Task The_connection_only_matters_for_network_targets()
    {
        var system = new FakeSystemState { Metered = true, Wired = false };

        Assert.Null(await All.CheckAsync(system, Disk));

        // A folder on a network share counts as a network target.
        system.NetworkTarget = _ => true;
        Assert.Equal(BackupSkipReason.MeteredConnection, await All.CheckAsync(system, Disk));
    }

    [Fact]
    public async Task What_the_system_cannot_tell_holds_nothing_back()
    {
        var system = new FakeSystemState { OnBattery = null, Metered = null, Wired = null };

        Assert.Null(await All.CheckAsync(system, Server));
        Assert.Null(await new RunConditions().CheckAsync(new FakeSystemState { OnBattery = true }, Server));
        Assert.False(new RunConditions().Any);
    }

    [Fact]
    public void Linux_tells_battery_and_mains()
    {
        var supplies = _directory.Combine("power_supply");
        void Supply(string name, string type, string? online)
        {
            Directory.CreateDirectory(Path.Combine(supplies, name));
            File.WriteAllText(Path.Combine(supplies, name, "type"), type + "\n");
            if (online is not null)
                File.WriteAllText(Path.Combine(supplies, name, "online"), online + "\n");
        }

        Assert.Null(SystemState.LinuxOnBattery(supplies));
        Supply("AC", "Mains", "0");
        Assert.Null(SystemState.LinuxOnBattery(supplies)); // no battery: a desktop
        Supply("BAT0", "Battery", null);
        Assert.True(SystemState.LinuxOnBattery(supplies));
        File.WriteAllText(Path.Combine(supplies, "AC", "online"), "1");
        Assert.False(SystemState.LinuxOnBattery(supplies));
    }

    [Theory]
    [InlineData("Unrestricted|False|False", false, true)]
    [InlineData("Fixed|True|False", true, false)]
    [InlineData("Variable|False|True", true, false)]
    public void Windows_tells_cost_and_kind_of_the_connection(string output, bool metered, bool wired)
    {
        var connection = SystemState.ParseWindowsConnection(output + "\r\n")!;

        Assert.Equal((metered, wired), (connection.Metered, connection.Wired));
        Assert.Null(SystemState.ParseWindowsConnection(""));
    }

    [Fact]
    public async Task Network_shares_and_reachability()
    {
        Assert.True(SystemState.IsOnNetworkShare(@"\\nas\backup\repo"));
        Assert.True(SystemState.IsOnNetworkShare("//nas/backup/repo"));
        Assert.False(SystemState.IsOnNetworkShare(_directory.Path));

        var system = new SystemState();
        Assert.True(await system.IsReachableAsync(new RepositoryLocation(_directory.Path)));
        Assert.False(await system.IsReachableAsync(new RepositoryLocation(_directory.Combine("gone"))));
        // Nobody listens on port 1 of this computer.
        Assert.False(await system.IsReachableAsync(new RepositoryLocation("ssh://me@127.0.0.1:1/./repo") { Ssh = new SshEndpoint("127.0.0.1", 1, "me", "./repo") }));
        // Asking the real system does not fail (whatever it says).
        await system.IsOnBatteryAsync();
    }

    [Fact]
    public void A_run_held_back_by_a_condition_is_no_failure_of_the_job()
    {
        var job = new BackupJob
        {
            Id = Guid.NewGuid(), Name = "Docs", RepositoryId = Guid.NewGuid(),
            Schedules = [new BackupSchedule { Kind = ScheduleKind.Daily, Time = new TimeOnly(20, 0) }],
        };
        var now = new DateTime(2026, 11, 20, 12, 0, 0);
        var ok = new BackupRunSummary { Started = new DateTimeOffset(now.AddDays(-1).Date.AddHours(20)), Duration = TimeSpan.FromMinutes(1), Outcome = BackupOutcome.Succeeded };
        var held = new BackupRunSummary { Started = new DateTimeOffset(now.AddHours(-1)), Duration = TimeSpan.Zero, Outcome = BackupOutcome.Skipped, SkipReason = BackupSkipReason.OnBattery };
        var busy = held with { SkipReason = BackupSkipReason.RepositoryBusy };

        Assert.NotEqual(HealthReason.LastRunFailed, JobHealth.Evaluate(job, [held, ok], now).Reason);
        Assert.Equal(HealthReason.LastRunFailed, JobHealth.Evaluate(job, [busy, ok], now).Reason);
    }
}
