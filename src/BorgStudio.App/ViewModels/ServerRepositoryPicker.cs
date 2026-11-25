using System.Collections.ObjectModel;
using System.Globalization;
using BorgStudio.App.Resources;
using BorgStudio.App.Services;
using BorgStudio.Providers.Server;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BorgStudio.App.ViewModels;

/// <summary>A repository on the server, as the editor lists it.</summary>
public sealed record ServerRepositoryOption(ServerRepository? Repository)
{
    /// <summary>Nothing in it yet (created on the server, never backed up to): it can be set up with borg.</summary>
    public bool IsEmpty => Repository is { UsedBytes: 0, IsAppendOnly: false };

    public override string ToString() => Repository is not { } repository
        ? Strings.ServerNewRepository
        : $"{repository.FullName}  ·  {ServerTexts.Access(repository)}  ·  {ServerTexts.Usage(repository)}";
}

/// <summary>
/// The editor's part for the BorgStudio Server: which paired server (or pair a new one), then a repository there – one of
/// those the account may use, or a new one (name, datastore, quota) created on the server.
/// </summary>
public sealed partial class ServerRepositoryPicker : ObservableObject
{
    private readonly AppServices _services;
    private readonly ServerProvider _provider;
    private readonly Func<IDialogService> _dialogs;
    private CancellationTokenSource? _loading;

    public ServerRepositoryPicker(AppServices services, ServerProvider provider, Func<IDialogService> dialogs)
    {
        _services = services;
        _provider = provider;
        _dialogs = dialogs;
        RefreshAccounts();
    }

    public ObservableCollection<ServerAccount> Accounts { get; } = [];

    public bool HasNoAccounts => Accounts.Count == 0;

    public bool HasAccounts => Accounts.Count > 0;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RepairCommand), nameof(RefreshCommand))]
    public partial ServerAccount? SelectedAccount { get; set; }

    partial void OnSelectedAccountChanged(ServerAccount? value) => _ = LoadAsync();

    // --- Connecting: the repositories there ---

    /// <summary>All repositories the account may use (connecting).</summary>
    public ObservableCollection<ServerRepositoryOption> Repositories { get; } = [];

    [ObservableProperty]
    public partial ServerRepositoryOption? SelectedRepository { get; set; }

    partial void OnSelectedRepositoryChanged(ServerRepositoryOption? value)
    {
        if (value?.Repository is { } repository)
            RepositoryChosen?.Invoke(this, repository.Name);
    }

    public bool HasNoRepositories => !IsLoading && Problem is null && SelectedAccount is not null && Repositories.Count == 0;

    // --- Creating: a new one, or an empty one created in the web interface ---

    /// <summary>"New repository on the server" and the empty ones the account may set up.</summary>
    public ObservableCollection<ServerRepositoryOption> CreateTargets { get; } = [new(null)];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CreatesOnServer))]
    public partial ServerRepositoryOption? SelectedCreateTarget { get; set; }

    partial void OnSelectedCreateTargetChanged(ServerRepositoryOption? value)
    {
        if (value?.Repository is { } repository)
            RepositoryChosen?.Invoke(this, repository.Name);
    }

    public ObservableCollection<ServerDatastore> Datastores { get; } = [];

    [ObservableProperty]
    public partial ServerDatastore? SelectedDatastore { get; set; }

    public bool HasDatastoreChoice => Datastores.Count > 1;

    [ObservableProperty]
    public partial string NewName { get; set; } = "";

    /// <summary>GB; empty: no quota of its own.</summary>
    [ObservableProperty]
    public partial string NewQuotaGigabytes { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CreatesOnServer), nameof(CannotCreate))]
    public partial bool MayCreate { get; private set; } = true;

    /// <summary>The account's own quota on the server, if it has one ("12 GB of 100 GB used").</summary>
    [ObservableProperty]
    public partial string? AllowanceText { get; private set; }

    /// <summary>Mirrors the editor's mode: connect (list) or create.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CreatesOnServer), nameof(CannotCreate), nameof(IsConnecting))]
    public partial bool CreateNew { get; set; }

    public bool IsConnecting => !CreateNew;

    /// <summary>A new repository is created on the server (name, datastore, quota).</summary>
    public bool CreatesOnServer => CreateNew && MayCreate && SelectedCreateTarget?.Repository is null;

    public bool CannotCreate => CreateNew && !MayCreate;

    // --- State ---

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoRepositories))]
    public partial bool IsLoading { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblem), nameof(HasNoRepositories))]
    public partial string? Problem { get; private set; }

    public bool HasProblem => Problem is not null;

    /// <summary>The pairing no longer works (revoked, certificate changed): pairing again helps.</summary>
    [ObservableProperty]
    public partial bool CanRepair { get; private set; }

    /// <summary>A repository was chosen: the editor takes its name if none is entered yet.</summary>
    public event EventHandler<string>? RepositoryChosen;

    [RelayCommand]
    private async Task PairAsync()
    {
        var outcome = await new ServerConnections(_services, _dialogs()).PairNewAsync();
        AfterPairing(outcome);
    }

    [RelayCommand(CanExecute = nameof(HasSelectedAccount))]
    private async Task RepairAsync()
    {
        var outcome = await new ServerConnections(_services, _dialogs()).PairAsync(SelectedAccount!.Url);
        AfterPairing(outcome);
    }

    /// <summary>Loads again – after opening the vault, if the token is in it and it is locked.</summary>
    [RelayCommand(CanExecute = nameof(HasSelectedAccount))]
    private async Task RefreshAsync()
    {
        if (_services.Vault.State == Core.Security.VaultState.Locked && !await VaultAccess.EnsureUnlockedAsync(_services.Vault, _dialogs()))
            return;
        await LoadAsync();
    }

    private bool HasSelectedAccount() => SelectedAccount is not null;

    private void AfterPairing(PairingOutcome outcome)
    {
        RefreshAccounts(outcome.Account?.Url);
        if (outcome.Problem is not null)
            Problem = outcome.Problem;
        else if (outcome.Account is not null)
            _ = LoadAsync();
    }

    private void RefreshAccounts(string? select = null)
    {
        var selected = select ?? SelectedAccount?.Url;
        Accounts.Clear();
        foreach (var account in _provider.Accounts.All())
            Accounts.Add(account);
        OnPropertyChanged(nameof(HasNoAccounts));
        OnPropertyChanged(nameof(HasAccounts));
        var next = Accounts.FirstOrDefault(account => account.Url == selected) ?? Accounts.FirstOrDefault();
        if (!ReferenceEquals(next, SelectedAccount))
            SelectedAccount = next;
    }

    /// <summary>Reads the account's repositories and datastores from the selected server.</summary>
    public async Task LoadAsync()
    {
        _loading?.Cancel();
        var loading = _loading = new CancellationTokenSource();
        Repositories.Clear();
        CreateTargets.Clear();
        CreateTargets.Add(new ServerRepositoryOption(null));
        SelectedCreateTarget = CreateTargets[0];
        Datastores.Clear();
        Problem = null;
        CanRepair = false;
        AllowanceText = null;
        if (SelectedAccount is not { } account)
            return;

        IsLoading = true;
        try
        {
            var client = _provider.ClientFor(account.Url);
            var repositories = await client.GetRepositoriesAsync(loading.Token);
            var datastores = await client.GetDatastoresAsync(loading.Token);
            if (loading.IsCancellationRequested)
                return;
            foreach (var repository in repositories.OrderBy(repository => !repository.IsOwner).ThenBy(repository => repository.FullName, StringComparer.CurrentCultureIgnoreCase))
            {
                var option = new ServerRepositoryOption(repository);
                Repositories.Add(option);
                if (option.IsEmpty)
                    CreateTargets.Add(option);
            }
            foreach (var datastore in datastores.Datastores)
                Datastores.Add(datastore);
            SelectedDatastore = Datastores.FirstOrDefault();
            MayCreate = datastores.MayCreate;
            AllowanceText = datastores.QuotaBytes is { } quota
                ? BorgTexts.Format(Strings.ServerAllowance, BorgTexts.Size(datastores.UsedBytes), BorgTexts.Size(quota))
                : null;
            SelectedRepository = null;
        }
        catch (ServerException exception)
        {
            if (loading.IsCancellationRequested)
                return;
            Problem = exception.Message;
            CanRepair = exception.Problem is ServerProblem.Revoked or ServerProblem.CertificateChanged or ServerProblem.NotPaired;
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (ReferenceEquals(_loading, loading))
                IsLoading = false;
            OnPropertyChanged(nameof(HasDatastoreChoice));
            OnPropertyChanged(nameof(HasNoRepositories));
        }
    }

    /// <summary>What is missing or wrong (besides what the provider checks).</summary>
    public IReadOnlyList<string> Validate()
    {
        if (SelectedAccount is null)
            return [Strings.ServerChooseServer];
        if (CreateNew && !MayCreate && SelectedCreateTarget?.Repository is null)
            return [Strings.ServerMayNotCreate];
        if (CreatesOnServer && QuotaBytes == -1)
            return [Strings.ServerQuotaInvalid];
        return [];
    }

    /// <summary>The quota typed in, in bytes; <c>null</c> if empty, -1 if it is no number.</summary>
    private long? QuotaBytes =>
        NewQuotaGigabytes.Trim() is { Length: > 0 } text
            ? double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var gigabytes) && gigabytes > 0
                ? (long)(gigabytes * 1024 * 1024 * 1024)
                : -1
            : null;

    /// <summary>The provider values for the chosen server and repository (or the one to create, named <paramref name="name"/> if no name is typed).</summary>
    public Dictionary<string, string> Values(string name)
    {
        var values = new Dictionary<string, string> { [ServerProvider.ServerKey] = SelectedAccount?.Url ?? "" };
        var chosen = CreateNew ? SelectedCreateTarget?.Repository : SelectedRepository?.Repository;
        if (chosen is not null)
        {
            values[ServerProvider.RepositoryIdKey] = chosen.Id.ToString();
            values[ServerProvider.RepositoryNameKey] = chosen.FullName;
        }
        else if (CreatesOnServer)
        {
            values[ServerProvider.NewNameKey] = NewName.Trim() is { Length: > 0 } typed ? typed : name.Trim();
            values[ServerProvider.NewDatastoreKey] = SelectedDatastore?.Id.ToString() ?? "";
            values[ServerProvider.NewQuotaKey] = QuotaBytes is > 0 and var quota ? quota.ToString(CultureInfo.InvariantCulture) : "";
        }
        return values;
    }
}

/// <summary>Texts about repositories on a BorgStudio Server.</summary>
internal static class ServerTexts
{
    public static string Access(ServerRepository repository) =>
        repository.IsOwner ? Strings.ServerAccessOwner
        : repository.IsAppendOnly ? Strings.ServerAccessAppendOnly
        : Strings.ServerAccessFull;

    public static string ParticipantAccess(ServerParticipant participant) =>
        participant.IsOwner ? Strings.ServerAccessOwner
        : participant.Access == "appendOnly" ? Strings.ServerAccessAppendOnly
        : Strings.ServerAccessFull;

    /// <summary>"1.2 GB" or "1.2 GB of 50 GB".</summary>
    public static string Usage(ServerRepository repository) =>
        repository.QuotaBytes is { } quota
            ? BorgTexts.Format(Strings.ServerUsageOf, BorgTexts.Size(repository.UsedBytes), BorgTexts.Size(quota))
            : BorgTexts.Size(repository.UsedBytes);
}
