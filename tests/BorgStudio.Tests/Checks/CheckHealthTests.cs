using BorgStudio.Core.Backups;
using BorgStudio.Core.Checks;
using BorgStudio.Core.Notifications;
using BorgStudio.Tests.TestSupport;

namespace BorgStudio.Tests.Checks;

/// <summary>When checks are due, overdue or could not run – and how that is reported.</summary>
public sealed class CheckHealthTests : IDisposable
{
    private readonly TemporaryDirectory _directory = new();

    public void Dispose() => _directory.Dispose();

    // Friday, 20 November 2026, 12:00.
    private static readonly DateTime Now = new(2026, 11, 20, 12, 0, 0);

    private static readonly BackupSchedule FirstSunday = new() { Kind = ScheduleKind.MonthlyWeekday, Week = 1, Weekday = DayOfWeek.Sunday, Time = new TimeOnly(3, 0) };

    private static CheckRecord Check(DateTime at, CheckOutcome outcome = CheckOutcome.Ok, bool automatic = false) =>
        new(new DateTimeOffset(at), TimeSpan.FromMinutes(5), CheckDepth.Normal, outcome) { Automatic = automatic };

    private static CheckHealthReason Reason(CheckPolicy policy, params CheckRecord[] checks) => CheckHealth.Evaluate(policy, checks, Now).Reason;

    [Fact]
    public void A_check_after_a_backup_is_due_once_the_interval_passed()
    {
        var policy = new CheckPolicy { AfterBackup = true, IntervalDays = 30 };
        var now = new DateTimeOffset(Now);

        Assert.True(policy.IsDueAfterBackup(null, now));
        Assert.False(policy.IsDueAfterBackup(Check(Now.AddDays(-29)), now));
        Assert.True(policy.IsDueAfterBackup(Check(Now.AddDays(-30)), now));
        Assert.False(new CheckPolicy().IsDueAfterBackup(null, now));
    }

    [Fact]
    public void Damage_comes_first()
    {
        var policy = new CheckPolicy { Schedules = [FirstSunday] };

        Assert.Equal((HealthLevel.Critical, CheckHealthReason.Damaged),
            CheckHealth.Evaluate(policy, [Check(Now.AddDays(-1), CheckOutcome.Failed, automatic: true), Check(Now.AddDays(-2), CheckOutcome.ProblemsFound)], Now));
    }

    [Fact]
    public void An_automatic_check_that_could_not_run_is_a_warning_one_by_hand_is_not()
    {
        var policy = new CheckPolicy { AfterBackup = true };

        Assert.Equal(CheckHealthReason.NotRun, Reason(policy, Check(Now.AddDays(-1), CheckOutcome.Failed, automatic: true), Check(Now.AddDays(-9))));
        Assert.Equal(CheckHealthReason.Fine, Reason(policy, Check(Now.AddDays(-1), CheckOutcome.Failed), Check(Now.AddDays(-9))));
    }

    [Fact]
    public void A_scheduled_check_is_overdue_a_day_after_its_time()
    {
        var policy = new CheckPolicy { Schedules = [FirstSunday] };

        // The 1st of November was the first Sunday; checked before it.
        Assert.Equal(CheckHealthReason.Overdue, Reason(policy, Check(new DateTime(2026, 10, 4, 3, 10, 0))));
        Assert.Equal(CheckHealthReason.Fine, Reason(policy, Check(new DateTime(2026, 11, 1, 3, 10, 0))));
        // Never checked: not judged (it shows as "never checked").
        Assert.Equal((HealthLevel.Info, CheckHealthReason.Fine), CheckHealth.Evaluate(policy, [], Now));
    }

    [Fact]
    public void Checks_after_backups_are_overdue_a_week_after_the_interval()
    {
        var policy = new CheckPolicy { AfterBackup = true, IntervalDays = 30 };

        Assert.Equal(CheckHealthReason.Fine, Reason(policy, Check(Now.AddDays(-36))));
        Assert.Equal(CheckHealthReason.Overdue, Reason(policy, Check(Now.AddDays(-38))));
        Assert.Equal(CheckHealthReason.Fine, Reason(new CheckPolicy(), Check(Now.AddDays(-300))));
    }

    [Fact]
    public void A_check_that_could_not_run_is_reported_once_until_one_runs()
    {
        var policy = NotificationPolicy.ForDataDirectory(_directory.Path);
        var repository = Guid.NewGuid();

        Assert.Null(policy.DecideCheck(repository, CheckOutcome.Failed));
        Assert.Equal(new NotificationDecision(NotificationEvent.CheckFailed, false), policy.DecideCheck(repository, CheckOutcome.Failed, automatic: true));
        Assert.Equal(new NotificationDecision(NotificationEvent.CheckFailed, true), policy.DecideCheck(repository, CheckOutcome.Failed, automatic: true));
        Assert.Equal(new NotificationDecision(NotificationEvent.Recovered, false), policy.DecideCheck(repository, CheckOutcome.Ok, automatic: true));

        // Known damage stays the open problem when a later check cannot run.
        policy.DecideCheck(repository, CheckOutcome.ProblemsFound);
        Assert.Null(policy.DecideCheck(repository, CheckOutcome.Failed, automatic: true));
        Assert.True(policy.DecideCheck(repository, CheckOutcome.ProblemsFound, automatic: true)!.IsRepeat);
    }
}
