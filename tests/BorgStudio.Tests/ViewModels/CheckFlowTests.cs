using System.Text.Json.Nodes;
using BorgStudio.App.Resources;
using BorgStudio.App.ViewModels;
using BorgStudio.Core.Backups;
using BorgStudio.Core.Checks;
using BorgStudio.Core.Notifications;
using BorgStudio.Core.Repositories;
using BorgStudio.Core.Settings;
using BorgStudio.Tests.TestSupport;

namespace BorgStudio.Tests.ViewModels;

/// <summary>Checking a repository from its overview: normally, thoroughly, repairing – with fake borg.</summary>
public sealed class CheckFlowTests : IDisposable
{
    private readonly TestServices _test = new();
    private readonly RepositoryConfig _repository;

    public CheckFlowTests()
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
        Directory.CreateDirectory(_test.Directory.Combine("repo"));
    }

    public void Dispose() => _test.Dispose();

    private FakeBorg Borg => _test.Borg;

    private FakeDialogs Dialogs => _test.Dialogs;

    private RepositoryCheckStore Store => RepositoryCheckStore.ForDataDirectory(_test.Directory.Path);

    private async Task<MainViewModel> StartAsync()
    {
        var main = new MainViewModel(_test.Services);
        await main.CheckBorgCommand.ExecuteAsync(null);
        main.ListedRepository = main.Repositories[0];
        Borg.Calls.Clear();
        return main;
    }

    private List<IReadOnlyList<string>> Checks => [.. Borg.Calls.Where(call => call.Arguments[0] == "check").Select(call => call.Arguments)];

    [Fact]
    public async Task A_check_without_damage_shows_when_it_ran()
    {
        var main = await StartAsync();
        var item = main.SelectedRepository!;
        Assert.Equal(Strings.CheckStateNever, item.CheckStateText);
        Assert.False(main.RepairRepositoryCommand.CanExecute(null));
        Dialogs.Passphrases.Enqueue("pw");

        await main.CheckRepositoryCommand.ExecuteAsync(null);

        var check = Assert.Single(Checks);
        Assert.DoesNotContain("--verify-data", check);
        Assert.Equal("pw", Borg.Calls.Single(call => call.Arguments[0] == "check").Passphrase);
        Assert.True(item.IsCheckOk);
        Assert.Equal(Strings.CheckStateOk, item.CheckStateText);
        Assert.Equal(Strings.CheckResultOk, item.ResultText);
        Assert.False(item.HasError);
        Assert.Equal(CheckDepth.Normal, Store.LastVerdict(_repository.Id)!.Depth);
        Assert.False(main.IsWorking);
    }

    [Fact]
    public async Task Damage_shows_borgs_messages_offers_repair_and_marks_the_overview()
    {
        _test.Settings.Save(new AppSettings
        {
            Notifications = new NotificationSettings { Webhook = new WebhookChannel { Enabled = true, Url = "https://example.com/hook" } },
        });
        Borg.CheckProblems.AddRange(["Data integrity error: Segment entry checksum mismatch [segment 2, offset 8]",
            "Finished full repository check, errors found."]);
        var main = await StartAsync();
        var item = main.SelectedRepository!;
        Dialogs.Passphrases.Enqueue("pw");

        await main.CheckRepositoryCommand.ExecuteAsync(null);

        Assert.True(item.HasCheckProblems);
        Assert.True(item.HasError);
        Assert.Equal(BorgTexts.Format(Strings.CheckResultProblems, 2), item.ResultText);
        Assert.Equal(Borg.CheckProblems, item.CheckMessages);
        Assert.True(main.RepairRepositoryCommand.CanExecute(null));

        main.ShowOverviewCommand.Execute(null);
        var row = Assert.Single(main.Dashboard!.Rows);
        Assert.True(row.IsCritical);
        Assert.Equal(Strings.DashCheckProblems, row.HealthText);

        var posted = JsonNode.Parse(Assert.Single(_test.Http.Requests).Body)!;
        Assert.Equal("check-failed", (string?)posted["event"]);
        Assert.Equal(BorgTexts.Format(Strings.NotifyTitleCheckFailed, "Disk"), (string?)posted["title"]);
        Assert.Equal("Disk", (string?)posted["repository"]);
    }

    [Fact]
    public async Task A_thorough_check_asks_first_and_verifies_the_data()
    {
        var main = await StartAsync();

        await main.CheckRepositoryFullCommand.ExecuteAsync(null);
        Assert.Empty(Checks);
        Assert.Equal([Strings.CheckFullTitle], Dialogs.Shown);
        Assert.Contains(Strings.CheckFullSizeUnknown, Dialogs.Messages[0]);

        Dialogs.ConfirmAnswers.Enqueue(true);
        Dialogs.Passphrases.Enqueue("pw");
        await main.CheckRepositoryFullCommand.ExecuteAsync(null);

        Assert.Contains("--verify-data", Assert.Single(Checks));
        Assert.Equal(CheckDepth.Full, Store.LastVerdict(_repository.Id)!.Depth);
    }

    [Fact]
    public async Task Repairing_takes_two_confirmations_and_checks_again()
    {
        _test.Settings.Save(new AppSettings
        {
            Notifications = new NotificationSettings { Webhook = new WebhookChannel { Enabled = true, Url = "https://example.com/hook" } },
        });
        Borg.CheckProblems.Add("Index object count mismatch.");
        var main = await StartAsync();
        var item = main.SelectedRepository!;
        Dialogs.Passphrases.Enqueue("pw");
        await main.CheckRepositoryCommand.ExecuteAsync(null);
        Borg.Calls.Clear();

        // Backing out at the second question repairs nothing.
        Dialogs.ConfirmAnswers.Enqueue(true);
        Dialogs.ConfirmAnswers.Enqueue(false);
        await main.RepairRepositoryCommand.ExecuteAsync(null);
        Assert.Empty(Checks);

        Dialogs.ConfirmAnswers.Enqueue(true);
        Dialogs.ConfirmAnswers.Enqueue(true);
        Dialogs.Passphrases.Enqueue("pw");
        await main.RepairRepositoryCommand.ExecuteAsync(null);

        Assert.Equal(2, Checks.Count);
        Assert.Contains("--repair", Checks[0]);
        Assert.DoesNotContain("--repair", Checks[1]);
        Assert.Equal([CheckOutcome.Ok, CheckOutcome.Repaired, CheckOutcome.ProblemsFound], Store.Get(_repository.Id).Select(check => check.Outcome));
        Assert.True(item.IsCheckOk);
        Assert.False(main.RepairRepositoryCommand.CanExecute(null));
        Assert.Equal(["check-failed", "recovered"], _test.Http.Requests.Select(request => (string?)JsonNode.Parse(request.Body)!["event"]));
        Assert.Equal(BorgTexts.Format(Strings.NotifyTitleCheckRecovered, "Disk"), (string?)JsonNode.Parse(_test.Http.Requests[1].Body)!["title"]);
    }

    [Fact]
    public async Task A_check_that_cannot_run_keeps_the_last_state_and_says_why()
    {
        var main = await StartAsync();
        var item = main.SelectedRepository!;
        Dialogs.Passphrases.Enqueue("pw");
        await main.CheckRepositoryCommand.ExecuteAsync(null);

        Borg.CheckError = "PassphraseWrong";
        Dialogs.Passphrases.Enqueue("wrong");
        await main.CheckRepositoryCommand.ExecuteAsync(null);

        Assert.True(item.IsCheckOk);
        Assert.True(item.HasError);
        Assert.True(item.HasCheckAttempt);
        Assert.Equal(CheckOutcome.Failed, Store.Get(_repository.Id)[0].Outcome);
    }

    [Fact]
    public async Task Cancelling_a_check_records_it_as_cancelled()
    {
        var main = await StartAsync();
        var item = main.SelectedRepository!;
        Borg.DuringCheck = () => main.Work.CancelCommand.Execute(null);
        Dialogs.Passphrases.Enqueue("pw");

        await main.CheckRepositoryCommand.ExecuteAsync(null);

        Assert.Equal(CheckOutcome.Cancelled, Store.Get(_repository.Id)[0].Outcome);
        Assert.Null(item.LastVerdict);
        Assert.Equal(Strings.CheckResultCancelled, item.ResultText);
    }

    [Fact]
    public async Task A_running_backup_keeps_the_check_away()
    {
        var main = await StartAsync();
        var item = main.SelectedRepository!;
        Dialogs.Passphrases.Enqueue("pw");
        using (RepositoryLock.TryAcquire(main.Runner.LockDirectory, _repository.Id))
            await main.CheckRepositoryCommand.ExecuteAsync(null);

        Assert.Empty(Checks);
        Assert.Equal(Strings.CheckBusy, item.ResultText);
        Assert.Empty(Store.Get(_repository.Id));
    }

    [Fact]
    public async Task Removing_the_repository_removes_its_checks()
    {
        Store.Add(_repository.Id, new CheckRecord(DateTimeOffset.Now, TimeSpan.FromMinutes(1), CheckDepth.Normal, CheckOutcome.Ok));
        var main = await StartAsync();
        Assert.True(main.SelectedRepository!.IsCheckOk);
        Dialogs.ConfirmAnswers.Enqueue(true);

        await main.RemoveRepositoryCommand.ExecuteAsync(null);

        Assert.Empty(Store.Get(_repository.Id));
    }
}
