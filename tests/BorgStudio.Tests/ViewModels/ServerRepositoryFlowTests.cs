using BorgStudio.App.Resources;
using BorgStudio.App.Services;
using BorgStudio.App.ViewModels;
using BorgStudio.Core.Repositories;
using BorgStudio.Plugins;
using BorgStudio.Providers.Server;
using BorgStudio.Tests.TestSupport;

namespace BorgStudio.Tests.ViewModels;

/// <summary>
/// Repositories on a BorgStudio Server (fake interface): pairing through the browser, choosing or creating a repository,
/// the certificate, the repository's card, pairing again after a revocation, unpairing and the reports of runs.
/// </summary>
public sealed class ServerRepositoryFlowTests : IDisposable
{
    private readonly TestServices _test = new();

    // The token and the SSH keys live in the vault.
    public ServerRepositoryFlowTests() => _test.Vault.SetUp("123456");

    public void Dispose() => _test.Dispose();

    private FakeBorgStudioServer Server => _test.ServerApi;

    private ServerProvider Provider => _test.Services.Server!;

    private RepositoryEditorViewModel NewEditor()
    {
        var editor = new RepositoryEditorViewModel(_test.Services, FakeBorg.Installation);
        editor.SelectedProvider = editor.Providers.Single(option => option.Provider.Id == ServerProvider.ProviderId);
        editor.NextCommand.Execute(null);
        return editor;
    }

    private async Task<ServerRepositoryPicker> PairInEditorAsync(RepositoryEditorViewModel editor)
    {
        var picker = editor.Server!;
        _test.Dialogs.TextAnswers.Enqueue("backup.test:8443");
        await picker.PairCommand.ExecuteAsync(null);
        await picker.LoadAsync();
        return picker;
    }

    /// <summary>Adds a repository connected through the editor (paired before) and keeps it in the store.</summary>
    private async Task<RepositoryConfig> ConnectAsync(ServerRepository repository)
    {
        var editor = NewEditor();
        var picker = editor.Server!;
        if (Provider.Accounts.All().Count == 0)
            picker = await PairInEditorAsync(editor);
        else
            await picker.LoadAsync();
        picker.SelectedRepository = picker.Repositories.Single(option => option.Repository!.Id == repository.Id);
        editor.Passphrase = "secret";
        editor.StorageAsk = true;
        _test.Borg.EncryptionMode = "repokey-blake2";
        // The fake SSH server's key is not the one the server publishes: confirmed after the warning.
        _test.Dialogs.ConfirmAnswers.Enqueue(true);
        await editor.ConfirmCommand.ExecuteAsync(null);
        Assert.Empty(editor.Errors);
        var saved = editor.Result!.Repository;
        _test.Store.Save([.. _test.Store.Load().Repositories, saved]);
        return saved;
    }

    [Fact]
    public async Task Pairing_in_the_editor_then_connecting_a_repository_registers_the_key()
    {
        var laptop = Server.AddRepository("laptop", used: 5L << 30, quota: 50L << 30);
        Server.AddRepository("shared", used: 1L << 30, access: "appendOnly", isOwner: false);
        var editor = NewEditor();
        Assert.True(editor.ShowServerPicker);
        Assert.True(editor.IsProvisioned);
        Assert.True(editor.Server!.HasNoAccounts);

        Server.PendingPolls = 2;
        var picker = await PairInEditorAsync(editor);

        var account = Assert.Single(Provider.Accounts.All());
        Assert.Equal((FakeBorgStudioServer.Url, "Backup Server", "alice", Server.DeviceId), (account.Url, account.Name, account.UserName, account.DeviceId));
        Assert.Null(account.PinnedCertificate);
        Assert.Equal(FakeBorgStudioServer.Token, _test.Vault.GetSecret(ServerAccounts.SecretPrefix + FakeBorgStudioServer.Url));
        Assert.DoesNotContain(FakeBorgStudioServer.Token, File.ReadAllText(Provider.Accounts.FilePath));
        Assert.Equal("ABCD-EFGH", Assert.Single(_test.Dialogs.Pairings).UserCode);
        Assert.Equal(account.Url, picker.SelectedAccount!.Url);

        // Own repositories first, with access and usage.
        Assert.Equal(["alice/laptop", "bob/shared"], picker.Repositories.Select(option => option.Repository!.FullName));
        Assert.Contains(BorgTexts.Size(5L << 30), picker.Repositories[0].ToString());
        Assert.Contains(Strings.ServerAccessAppendOnly, picker.Repositories[1].ToString());

        picker.SelectedRepository = picker.Repositories[0];
        Assert.Equal("laptop", editor.Name);
        editor.Passphrase = "secret";
        editor.StorageAsk = true;
        _test.Borg.EncryptionMode = "repokey-blake2";
        _test.Dialogs.ConfirmAnswers.Enqueue(true);
        await editor.ConfirmCommand.ExecuteAsync(null);

        Assert.Empty(editor.Errors);
        Assert.Equal(["info"], _test.Borg.Commands);
        Assert.Contains(FakeBorgStudioServer.SshUrl(laptop.Id), _test.Borg.Calls[0].Arguments);
        var saved = editor.Result!.Repository;
        Assert.Equal(ServerProvider.ProviderId, saved.ProviderId);
        Assert.Equal(laptop.Id.ToString(), saved.ProviderValues[ServerProvider.RepositoryIdKey]);
        Assert.Equal("alice/laptop", saved.ProviderValues[ServerProvider.RepositoryNameKey]);
        Assert.Equal(FakeBorgStudioServer.Url, saved.ProviderValues[ServerProvider.ServerKey]);
        Assert.False(saved.SshKeyRestricted);
        Assert.Equal(_test.Vault.GetKey(saved.SshKeyId!.Value)!.PublicKey, Server.Keys[laptop.Id]);

        // Editing shows where it is and keeps the server's values.
        var edit = new RepositoryEditorViewModel(_test.Services, FakeBorg.Installation, saved);
        Assert.False(edit.ShowServerPicker);
        Assert.Equal(BorgTexts.Format(Strings.ServerSummary, "backup.test:8443", "alice/laptop"), edit.ServerSummary);
        edit.Name = "Laptop";
        await edit.ConfirmCommand.ExecuteAsync(null);
        Assert.Equal(laptop.Id.ToString(), edit.Result!.Repository.ProviderValues[ServerProvider.RepositoryIdKey]);
    }

    [Fact]
    public async Task Creating_makes_the_repository_on_the_server_and_sets_it_up_with_borg()
    {
        var empty = Server.AddRepository("from-web");
        var editor = NewEditor();
        var picker = await PairInEditorAsync(editor);
        editor.CreateNew = true;
        Assert.True(picker.CreatesOnServer);
        // An empty repository created in the web interface can be set up too.
        Assert.Equal([null, empty.Id], picker.CreateTargets.Select(option => option.Repository?.Id));
        Assert.True(picker.HasDatastoreChoice);
        Assert.NotNull(picker.AllowanceText);

        editor.Name = "laptop";
        picker.NewQuotaGigabytes = "x";
        editor.Passphrase = editor.PassphraseConfirmation = "secret";
        editor.StorageAsk = true;
        await editor.ConfirmCommand.ExecuteAsync(null);
        Assert.Contains(Strings.ServerQuotaInvalid, editor.Errors);

        picker.NewQuotaGigabytes = "50";
        picker.SelectedDatastore = picker.Datastores[1];
        _test.Dialogs.ConfirmAnswers.Enqueue(true);
        await editor.ConfirmCommand.ExecuteAsync(null);

        Assert.Empty(editor.Errors);
        var created = Server.Repositories.Single(repository => repository.Name == "laptop");
        Assert.Equal(50L << 30, created.QuotaBytes);
        Assert.Equal(["init", "info"], _test.Borg.Commands);
        Assert.Equal(created.Id.ToString(), editor.Result!.Repository.ProviderValues[ServerProvider.RepositoryIdKey]);
        Assert.Equal("", editor.Result.Repository.ProviderValues[ServerProvider.NewNameKey]);

        // An account that may not create: told before anything happens.
        Server.MayCreate = false;
        var refused = NewEditor();
        await refused.Server!.LoadAsync();
        refused.CreateNew = true;
        Assert.True(refused.Server.CannotCreate);
        refused.Name = "other";
        refused.Passphrase = refused.PassphraseConfirmation = "secret";
        await refused.ConfirmCommand.ExecuteAsync(null);
        Assert.Contains(Strings.ServerMayNotCreate, refused.Errors);
    }

    [Fact]
    public async Task An_untrusted_certificate_is_confirmed_once_and_then_pinned()
    {
        var certificate = new ServerCertificate(new string('A', 64), "CN=backup.test", new DateTimeOffset(2031, 1, 1, 0, 0, 0, TimeSpan.Zero));
        Provider.Http.TestCertificate = certificate;
        var connections = new ServerConnections(_test.Services, _test.Dialogs);

        // Declined: nothing is paired.
        _test.Dialogs.ConfirmAnswers.Enqueue(false);
        Assert.Equal(PairingOutcome.Cancelled, await connections.PairAsync("backup.test:8443"));
        Assert.Empty(Provider.Accounts.All());
        Assert.Contains(_test.Dialogs.Messages, message => message.Contains(certificate.Fingerprint));
        Assert.DoesNotContain(Server.Requests, request => request.Path.EndsWith("/pairing", StringComparison.Ordinal));

        _test.Dialogs.ConfirmAnswers.Enqueue(true);
        var outcome = await connections.PairAsync("backup.test:8443");
        Assert.Null(outcome.Problem);
        Assert.Equal(certificate.Sha256, outcome.Account!.PinnedCertificate);

        // The same certificate again: no question; another one: asked with a warning.
        var asked = _test.Dialogs.Messages.Count;
        await connections.PairAsync("backup.test:8443");
        Assert.Equal(asked, _test.Dialogs.Messages.Count);
        var renewed = certificate with { Sha256 = new string('B', 64) };
        Provider.Http.TestCertificate = renewed;
        _test.Dialogs.ConfirmAnswers.Enqueue(false);
        await connections.PairAsync("backup.test:8443");
        Assert.Equal(BorgTexts.Format(Strings.PairingCertificateChanged, "backup.test:8443", renewed.Subject,
            renewed.NotAfter.ToLocalTime().ToString("d"), renewed.Fingerprint), _test.Dialogs.Messages[^1]);
        Assert.Equal(certificate.Sha256, Provider.Accounts.Find(FakeBorgStudioServer.Url)!.PinnedCertificate);
    }

    [Fact]
    public async Task A_denied_or_failed_pairing_is_told()
    {
        var connections = new ServerConnections(_test.Services, _test.Dialogs);
        Server.PairingDecision = "denied";
        Assert.Equal(Strings.PairingDenied, (await connections.PairAsync("backup.test:8443")).Problem);

        Server.PairingDecision = "expired";
        Assert.Equal(Strings.PairingExpired, (await connections.PairAsync("backup.test:8443")).Problem);

        Assert.Equal(Strings.PairingAddressInvalid, (await connections.PairAsync("ftp://backup.test")).Problem);
        Server.Unreachable = true;
        Assert.Contains("backup.test:8443", (await connections.PairAsync("backup.test:8443")).Problem);
        Assert.Empty(Provider.Accounts.All());
    }

    [Fact]
    public async Task The_card_shows_access_usage_and_who_uses_it_and_pairing_again_registers_the_keys()
    {
        var laptop = Server.AddRepository("laptop", used: 5L << 30, quota: 50L << 30);
        var saved = await ConnectAsync(laptop);
        var main = new MainViewModel(_test.Services);
        main.SelectedRepository = main.Repositories.Single(item => item.Config.Id == saved.Id);

        var card = main.ServerCard!;
        await card.LoadAsync();
        Assert.True(card.HasDetails);
        Assert.Equal(Strings.ServerAccessOwner, card.AccessText);
        Assert.Contains(BorgTexts.Size(5L << 30), card.UsageText);
        Assert.Contains("10 %", card.UsageText);
        Assert.Equal($"alice ({Strings.ServerAccessOwner}), carol ({Strings.ServerAccessAppendOnly})", card.ParticipantsText);
        Assert.Contains("bob", card.LastAccessText);
        Assert.Equal($"{FakeBorgStudioServer.Url}repositories/{laptop.Id}", card.WebPage);
        await card.OpenInServerCommand.ExecuteAsync(null);
        Assert.Equal(card.WebPage, _test.Dialogs.OpenedUrls[^1]);

        // Revoked on the server (its keys went with it): told, and pairing again puts the key back.
        Server.Revoked = true;
        Server.Keys.Clear();
        await card.LoadAsync();
        Assert.True(card.CanRepair);
        Assert.Contains("backup.test:8443", card.Problem);
        await card.RepairCommand.ExecuteAsync(null);
        Assert.False(card.HasProblem);
        Assert.Equal(_test.Vault.GetKey(saved.SshKeyId!.Value)!.PublicKey, Server.Keys[laptop.Id]);
    }

    [Fact]
    public async Task A_server_hiding_its_users_shows_only_the_own_access()
    {
        var shared = Server.AddRepository("shared", access: "appendOnly", isOwner: false, participantsHidden: true);
        var saved = await ConnectAsync(shared);
        var card = new ServerRepositoryCard(Provider, saved, () => new ServerConnections(_test.Services, _test.Dialogs), _test.Dialogs);

        await card.LoadAsync();

        Assert.Equal(Strings.ServerAccessAppendOnly, card.AccessText);
        Assert.Equal(Strings.ServerParticipantsHidden, card.ParticipantsText);
        Assert.False(card.HasLastAccess);
    }

    [Fact]
    public async Task Unpairing_ends_the_access_on_the_server_and_here()
    {
        await ConnectAsync(Server.AddRepository("laptop"));
        var settings = new SettingsViewModel(new MainViewModel(_test.Services), _test.Dialogs);
        var item = Assert.Single(settings.PairedServers);
        Assert.Contains("1", item.Details);

        _test.Dialogs.ConfirmAnswers.Enqueue(false);
        await item.UnpairCommand.ExecuteAsync(null);
        Assert.Single(settings.PairedServers);

        _test.Dialogs.ConfirmAnswers.Enqueue(true);
        await item.UnpairCommand.ExecuteAsync(null);
        Assert.True(Server.Unpaired);
        Assert.Empty(settings.PairedServers);
        Assert.Null(_test.Vault.GetSecret(ServerAccounts.SecretPrefix + FakeBorgStudioServer.Url));

        // Paired again from the settings.
        _test.Dialogs.TextAnswers.Enqueue("https://backup.test:8443");
        await settings.PairServerCommand.ExecuteAsync(null);
        Assert.Single(settings.PairedServers);
        Assert.Contains("alice", settings.ServerMessage);
    }

    [Fact]
    public async Task Reports_wait_while_the_server_is_unreachable_and_go_in_order()
    {
        var saved = await ConnectAsync(Server.AddRepository("laptop"));
        var started = new DateTimeOffset(2026, 11, 25, 10, 0, 0, TimeSpan.Zero);
        Server.Unreachable = true;
        _test.Services.Report(saved.Id, new RunReport(RunReportKind.Backup, RunReportOutcome.Succeeded, started, TimeSpan.FromMinutes(3)));
        _test.Services.Report(saved.Id, new RunReport(RunReportKind.Prune, RunReportOutcome.Succeeded, started.AddMinutes(3), TimeSpan.FromSeconds(5)));
        await _test.Services.SendReportsAsync();
        Assert.Equal(2, _test.Services.Reports.Pending);

        // The vault locked (and no key for scheduled runs): still waiting.
        Server.Unreachable = false;
        _test.Vault.Lock();
        await _test.Services.SendReportsAsync();
        Assert.Equal(2, _test.Services.Reports.Pending);

        _test.Vault.Unlock("123456");
        await _test.Services.SendReportsAsync();
        Assert.Equal(0, _test.Services.Reports.Pending);
        Assert.Equal(["backup", "prune"], Server.Reports.Select(report => report["kind"]!.GetValue<string>()));

        // Other repositories' runs are not queued at all.
        Assert.False(_test.Services.Reports.Add(Guid.NewGuid(), new RunReport(RunReportKind.Check, RunReportOutcome.Succeeded, started, TimeSpan.Zero)));
    }
}
