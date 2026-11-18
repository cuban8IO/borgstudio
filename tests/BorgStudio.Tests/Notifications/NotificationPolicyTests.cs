using BorgStudio.Core.Backups;
using BorgStudio.Core.Notifications;
using BorgStudio.Tests.TestSupport;

namespace BorgStudio.Tests.Notifications;

/// <summary>Which event a run is, and when a problem is only a repeat.</summary>
public sealed class NotificationPolicyTests : IDisposable
{
    private readonly TemporaryDirectory _directory = new();

    public void Dispose() => _directory.Dispose();

    private NotificationPolicy NewPolicy() => NotificationPolicy.ForDataDirectory(_directory.Path);

    private static BackupRunSummary Run(BackupOutcome outcome, string? message = null, BackupSkipReason? skip = null) => new()
    {
        Started = DateTimeOffset.Now,
        Duration = TimeSpan.FromMinutes(1),
        Outcome = outcome,
        Message = message,
        SkipReason = skip,
    };

    [Fact]
    public void A_problem_is_reported_once_and_its_end_as_recovered()
    {
        var policy = NewPolicy();
        var job = Guid.NewGuid();

        Assert.Equal(new NotificationDecision(NotificationEvent.Succeeded, false), policy.Decide(job, Run(BackupOutcome.Succeeded)));
        Assert.Equal(new NotificationDecision(NotificationEvent.Failed, false), policy.Decide(job, Run(BackupOutcome.Failed, "disk full")));
        // The same again: a repeat – also for a new policy (another process, the next day).
        Assert.Equal(new NotificationDecision(NotificationEvent.Failed, true), NewPolicy().Decide(job, Run(BackupOutcome.Failed, "disk full")));
        // Another problem is news.
        Assert.Equal(new NotificationDecision(NotificationEvent.Failed, false), policy.Decide(job, Run(BackupOutcome.Failed, "host unknown")));
        Assert.Equal(new NotificationDecision(NotificationEvent.Recovered, false), policy.Decide(job, Run(BackupOutcome.Succeeded)));
        Assert.Equal(new NotificationDecision(NotificationEvent.Succeeded, false), policy.Decide(job, Run(BackupOutcome.Succeeded)));
    }

    [Fact]
    public void Warnings_and_skipped_runs_are_problems_of_their_own()
    {
        var policy = NewPolicy();
        var job = Guid.NewGuid();

        Assert.Equal(NotificationEvent.Skipped,
            policy.Decide(job, Run(BackupOutcome.Skipped, skip: BackupSkipReason.PassphraseAsked))!.Event);
        Assert.True(policy.Decide(job, Run(BackupOutcome.Skipped, skip: BackupSkipReason.PassphraseAsked))!.IsRepeat);
        Assert.Equal(new NotificationDecision(NotificationEvent.Warnings, false), policy.Decide(job, Run(BackupOutcome.Warnings, "file locked")));
        Assert.Equal(NotificationEvent.Recovered, policy.Decide(job, Run(BackupOutcome.Succeeded))!.Event);
    }

    [Fact]
    public void A_stopped_run_is_not_reported_and_changes_nothing()
    {
        var policy = NewPolicy();
        var job = Guid.NewGuid();
        policy.Decide(job, Run(BackupOutcome.Failed, "x"));

        Assert.Null(policy.Decide(job, Run(BackupOutcome.Cancelled)));
        Assert.True(policy.Decide(job, Run(BackupOutcome.Failed, "x"))!.IsRepeat);
    }

    [Fact]
    public void Jobs_are_apart_and_a_deleted_job_is_forgotten()
    {
        var policy = NewPolicy();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        policy.Decide(first, Run(BackupOutcome.Failed, "x"));

        Assert.False(policy.Decide(second, Run(BackupOutcome.Failed, "x"))!.IsRepeat);
        policy.Forget(first);
        Assert.False(policy.Decide(first, Run(BackupOutcome.Failed, "x"))!.IsRepeat);
    }
}
