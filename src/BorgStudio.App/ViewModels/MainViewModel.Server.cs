using BorgStudio.App.Services;
using BorgStudio.Core.Borg;
using BorgStudio.Core.Repositories;
using BorgStudio.Plugins;
using BorgStudio.Providers.Server;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BorgStudio.App.ViewModels;

/// <summary>Repositories on a BorgStudio Server: their card on the repository's page, pairing again, and the reports of runs.</summary>
public partial class MainViewModel
{
    /// <summary>The selected repository's state on its BorgStudio Server; <c>null</c> for other repositories.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasServerCard))]
    public partial ServerRepositoryCard? ServerCard { get; private set; }

    public bool HasServerCard => ServerCard is not null;

    /// <summary>Pairing and unpairing BorgStudio Servers, with changed repositories kept in this window's list.</summary>
    internal ServerConnections ServerConnections(IDialogService? dialogs = null) =>
        new(_services, dialogs ?? _services.Dialogs, KeepRepository);

    private void RefreshServerCard()
    {
        if (SelectedRepository is not { Provider: ServerProvider provider } item)
        {
            ServerCard = null;
            return;
        }
        var card = new ServerRepositoryCard(provider, item.Config, () => ServerConnections(), _services.Dialogs);
        card.Repaired += (_, _) => _ = _services.SendReportsAsync();
        ServerCard = card;
        _ = card.LoadAsync();
    }

    /// <summary>A repository whose values changed elsewhere (e.g. its address after pairing again): in the list and the store.</summary>
    private void KeepRepository(RepositoryConfig repository)
    {
        OnViewModelThread(() =>
        {
            if (Repositories.FirstOrDefault(item => item.Config.Id == repository.Id) is not { } item)
                return;
            item.Config = repository;
            SaveRepositories();
        });
    }

    /// <summary>A compact by hand, for the server's statistics.</summary>
    private void ReportCompact(Guid repositoryId, DateTimeOffset started, BorgError? error) =>
        _services.Report(repositoryId, RunReports.Cleanup(RunReportKind.Compact, started, DateTimeOffset.Now - started,
            error?.Kind == BorgErrorKind.Cancelled, error is null || error.Kind == BorgErrorKind.Cancelled ? null : BorgTexts.Describe(error)));

    /// <summary>Reports that could not be delivered before: at the start, after unlocking the vault and every hour.</summary>
    public void SendWaitingReports() => _ = _services.SendReportsAsync();
}
