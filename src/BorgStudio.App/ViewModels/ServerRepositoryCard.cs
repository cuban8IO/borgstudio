using System.Globalization;
using BorgStudio.App.Resources;
using BorgStudio.App.Services;
using BorgStudio.Core.Repositories;
using BorgStudio.Providers.Server;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BorgStudio.App.ViewModels;

/// <summary>
/// What the BorgStudio Server says about a repository on the repository's page: the server, this account's access, usage
/// and quota, who else uses it and the last access (unless the server hides those), a link to its page in the web
/// interface – and, when the pairing no longer works, "pair again".
/// </summary>
public sealed partial class ServerRepositoryCard : ObservableObject
{
    private readonly ServerProvider _provider;
    private readonly RepositoryConfig _repository;
    private readonly Func<ServerConnections> _connections;
    private readonly IDialogService _dialogs;

    public ServerRepositoryCard(ServerProvider provider, RepositoryConfig repository, Func<ServerConnections> connections, IDialogService dialogs)
    {
        _provider = provider;
        _repository = repository;
        _connections = connections;
        _dialogs = dialogs;
        Url = ServerAddress.Normalize(repository.ProviderValues.GetValueOrDefault(ServerProvider.ServerKey)) ?? "";
        var account = provider.Accounts.Find(Url);
        ServerText = account?.ToString() ?? ServerAddress.Display(Url);
        RepositoryText = repository.ProviderValues.GetValueOrDefault(ServerProvider.RepositoryNameKey) ?? "";
        WebPage = ServerProvider.WebPage(repository.ProviderValues);
    }

    public string Url { get; }

    public string ServerText { get; }

    [ObservableProperty]
    public partial string RepositoryText { get; private set; }

    public string? WebPage { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDetails))]
    public partial bool IsLoaded { get; private set; }

    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    [ObservableProperty]
    public partial string? AccessText { get; private set; }

    [ObservableProperty]
    public partial string? UsageText { get; private set; }

    /// <summary>The owner's quota over all their repositories, if they have one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOwnerQuota))]
    public partial string? OwnerQuotaText { get; private set; }

    public bool HasOwnerQuota => OwnerQuotaText is not null;

    [ObservableProperty]
    public partial string? DatastoreText { get; private set; }

    /// <summary>Who uses it ("alice (owner), bob (append only)"), or that the server does not tell.</summary>
    [ObservableProperty]
    public partial string? ParticipantsText { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLastAccess))]
    public partial string? LastAccessText { get; private set; }

    public bool HasLastAccess => LastAccessText is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMoving))]
    public partial bool IsMoving { get; private set; }

    public bool HasMoving => IsMoving;

    public bool HasDetails => IsLoaded && Problem is null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblem), nameof(HasDetails))]
    public partial string? Problem { get; private set; }

    public bool HasProblem => Problem is not null;

    /// <summary>Pairing again helps (revoked, certificate changed, not paired).</summary>
    [ObservableProperty]
    public partial bool CanRepair { get; private set; }

    /// <summary>Raised after pairing again worked (the main window reloads its repositories' state).</summary>
    public event EventHandler? Repaired;

    public async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            var repository = await _provider.GetRepositoryAsync(_repository.ProviderValues, CancellationToken.None);
            Show(repository);
            Problem = null;
            CanRepair = false;
        }
        catch (ServerException exception)
        {
            Problem = exception.Message;
            CanRepair = exception.Problem is ServerProblem.Revoked or ServerProblem.CertificateChanged
                || (exception.Problem == ServerProblem.NotPaired && _provider.Accounts.Find(Url) is null);
        }
        finally
        {
            IsLoading = false;
            IsLoaded = true;
        }
    }

    private void Show(ServerRepository repository)
    {
        RepositoryText = repository.FullName;
        AccessText = ServerTexts.Access(repository);
        UsageText = repository.QuotaBytes is { } quota
            ? BorgTexts.Format(Strings.ServerUsageQuota, BorgTexts.Size(repository.UsedBytes), BorgTexts.Size(quota),
                (int)Math.Round(100.0 * repository.UsedBytes / Math.Max(1, quota)))
            : BorgTexts.Format(Strings.ServerUsageNoQuota, BorgTexts.Size(repository.UsedBytes));
        OwnerQuotaText = repository.OwnerQuota is { } owner
            ? BorgTexts.Format(Strings.ServerOwnerQuota, repository.Owner, BorgTexts.Size(owner.UsedBytes), BorgTexts.Size(owner.QuotaBytes))
            : null;
        DatastoreText = repository.DatastoreFreeBytes is { } free
            ? BorgTexts.Format(Strings.ServerDatastoreFree, repository.Datastore, BorgTexts.Size(free))
            : repository.Datastore;
        ParticipantsText = repository.ParticipantsHidden
            ? Strings.ServerParticipantsHidden
            : string.Join(", ", repository.Participants.Select(participant => $"{participant.UserName} ({ServerTexts.ParticipantAccess(participant)})"));
        LastAccessText = repository.LastAccess is { } last
            ? BorgTexts.Format(Strings.ServerLastAccess, last.At.ToLocalTime().ToString("g", CultureInfo.CurrentCulture), last.UserName, last.By)
            : repository.ParticipantsHidden ? null : Strings.ServerNeverAccessed;
        IsMoving = repository.IsMoving;
    }

    [RelayCommand]
    private Task RefreshAsync() => LoadAsync();

    [RelayCommand]
    private Task OpenInServerAsync() => WebPage is null ? Task.CompletedTask : _dialogs.OpenUrlAsync(WebPage);

    [RelayCommand]
    private async Task RepairAsync()
    {
        var outcome = await _connections().PairAsync(Url);
        if (outcome.Account is not null)
            Repaired?.Invoke(this, EventArgs.Empty);
        if (outcome.Problem is not null)
            Problem = outcome.Problem;
        else if (outcome.Account is not null)
            await LoadAsync();
    }
}
