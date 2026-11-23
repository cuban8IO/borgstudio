using System.Text.Json;
using BorgStudio.Core.Checks;
using BorgStudio.Core.Notifications;
using BorgStudio.Tests.TestSupport;

namespace BorgStudio.Tests.Checks;

/// <summary>Keeping checks per repository, what counts as the repository's state, and how checks are reported.</summary>
public sealed class RepositoryCheckStoreTests : IDisposable
{
    private readonly TemporaryDirectory _directory = new();
    private readonly Guid _repository = Guid.NewGuid();

    public void Dispose() => _directory.Dispose();

    private RepositoryCheckStore Store => RepositoryCheckStore.ForDataDirectory(_directory.Path);

    private static readonly DateTimeOffset Start = new(2026, 11, 20, 10, 0, 0, TimeSpan.FromHours(1));

    private static CheckRecord Record(int day, CheckOutcome outcome, CheckDepth depth = CheckDepth.Normal) =>
        new(Start.AddDays(day), TimeSpan.FromMinutes(3), depth, outcome);

    [Fact]
    public void Keeps_the_checks_newest_first_with_their_messages()
    {
        Store.Add(_repository, Record(0, CheckOutcome.Ok));
        Store.Add(_repository, Record(1, CheckOutcome.ProblemsFound) with { Messages = ["Index object count mismatch."] });

        var checks = Store.Get(_repository);

        Assert.Equal([CheckOutcome.ProblemsFound, CheckOutcome.Ok], checks.Select(check => check.Outcome));
        Assert.Equal(["Index object count mismatch."], checks[0].Messages);
        Assert.Empty(Store.Get(Guid.NewGuid()));
        Assert.True(File.Exists(Path.Combine(_directory.Path, "checks", $"{_repository:N}.json")));
    }

    [Fact]
    public void Only_a_check_that_ran_to_its_end_tells_the_state()
    {
        Store.Add(_repository, Record(0, CheckOutcome.ProblemsFound));
        Store.Add(_repository, Record(1, CheckOutcome.Repaired, CheckDepth.Repair));
        Store.Add(_repository, Record(2, CheckOutcome.Failed) with { Error = "no connection" });
        Store.Add(_repository, Record(3, CheckOutcome.Cancelled));

        Assert.Equal(Start, Store.LastVerdict(_repository)!.Started);

        Store.Add(_repository, Record(4, CheckOutcome.Ok));
        Assert.Equal(CheckOutcome.Ok, Store.LastVerdict(_repository)!.Outcome);
    }

    [Fact]
    public void Keeps_the_last_fifty_and_goes_with_the_repository()
    {
        for (var day = 0; day < RepositoryCheckStore.MaximumRecords + 5; day++)
            Store.Add(_repository, Record(day, CheckOutcome.Ok));

        Assert.Equal(RepositoryCheckStore.MaximumRecords, Store.Get(_repository).Count);
        Assert.Equal(Start.AddDays(RepositoryCheckStore.MaximumRecords + 4), Store.Get(_repository)[0].Started);

        Store.Remove(_repository);
        Assert.Empty(Store.Get(_repository));
    }

    [Fact]
    public void Damage_is_reported_once_until_a_check_finds_none()
    {
        var policy = NotificationPolicy.ForDataDirectory(_directory.Path);

        Assert.Null(policy.DecideCheck(_repository, CheckOutcome.Ok));
        Assert.Equal(new NotificationDecision(NotificationEvent.CheckFailed, false), policy.DecideCheck(_repository, CheckOutcome.ProblemsFound));
        Assert.Null(policy.DecideCheck(_repository, CheckOutcome.Failed));
        Assert.Null(policy.DecideCheck(_repository, CheckOutcome.Repaired));
        Assert.Equal(new NotificationDecision(NotificationEvent.CheckFailed, true), policy.DecideCheck(_repository, CheckOutcome.ProblemsFound));
        Assert.Equal(new NotificationDecision(NotificationEvent.Recovered, false), policy.DecideCheck(_repository, CheckOutcome.Ok));
        Assert.Null(policy.DecideCheck(_repository, CheckOutcome.Ok));

        policy.DecideCheck(_repository, CheckOutcome.ProblemsFound);
        policy.ForgetRepository(_repository);
        Assert.False(policy.DecideCheck(_repository, CheckOutcome.ProblemsFound)!.IsRepeat);
    }

    [Fact]
    public void Channels_saved_before_get_check_errors_if_they_report_failures()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        const string before = """{"system": {"enabled": true, "events": ["Failed", "Overdue"]}, "email": {"events": ["Overdue"]}}""";

        var loaded = JsonSerializer.Deserialize<NotificationSettings>(before, options)!;

        Assert.Equal([NotificationEvent.Failed, NotificationEvent.Overdue, NotificationEvent.CheckFailed], loaded.System.Events);
        Assert.Equal([NotificationEvent.Overdue], loaded.Email.Events);
        Assert.Contains(NotificationEvent.CheckFailed, new NotificationSettings().Webhook.Events);

        // Switched off afterwards, it stays off.
        var saved = JsonSerializer.Serialize(loaded with { System = loaded.System with { Events = [NotificationEvent.Failed] } }, options);
        Assert.Equal([NotificationEvent.Failed], JsonSerializer.Deserialize<NotificationSettings>(saved, options)!.System.Events);
    }

    [Fact]
    public void The_webhook_tells_the_check()
    {
        var record = Record(0, CheckOutcome.ProblemsFound) with { Messages = ["Index object count mismatch."] };
        var message = new NotificationMessage(NotificationEvent.CheckFailed, "Check found errors: NAS", "…") { Repository = "NAS", Check = record };

        var body = WebhookSender.Json(message);

        Assert.Equal("check-failed", (string?)body["event"]);
        Assert.Equal("ProblemsFound", (string?)body["check"]!["outcome"]);
        Assert.Equal("Normal", (string?)body["check"]!["depth"]);
        Assert.Equal(180, (double?)body["check"]!["durationSeconds"]);
        Assert.Equal("Index object count mismatch.", (string?)body["check"]!["messages"]![0]);
    }
}
