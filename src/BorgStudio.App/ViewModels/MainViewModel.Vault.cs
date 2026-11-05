using BorgStudio.App.Resources;
using BorgStudio.Core.Repositories;
using BorgStudio.Core.Security;
using BorgStudio.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BorgStudio.App.ViewModels;

/// <summary>The vault in the main window: status, locking, auto-lock and settings.</summary>
public partial class MainViewModel
{
    /// <summary>Repositories removed while the vault was locked: their passphrases go once it is unlocked again.</summary>
    private readonly HashSet<Guid> _pendingVaultRemovals = [];

    public AppSettings Settings { get; private set; } = new();

    public Vault Vault => _services.Vault;

    [ObservableProperty]
    public partial string VaultStatus { get; private set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LockVaultCommand))]
    public partial bool IsVaultUnlocked { get; private set; }

    [ObservableProperty]
    public partial bool IsVaultSetUp { get; private set; }

    private void InitializeVault()
    {
        Settings = _services.Settings.Load();
        _services.Vault.StateChanged += (_, _) => OnViewModelThread(OnVaultStateChanged);
        RefreshVaultStatus();
    }

    [RelayCommand(CanExecute = nameof(IsVaultUnlocked))]
    private void LockVault() => _services.Vault.Lock();

    /// <summary>Called regularly by the app: locks the vault after the configured time without use.</summary>
    public void CheckVaultIdle()
    {
        if (Settings.VaultAutoLockMinutes > 0)
            _services.Vault.LockIfIdle(TimeSpan.FromMinutes(Settings.VaultAutoLockMinutes));
    }

    public void UpdateSettings(AppSettings settings)
    {
        Settings = settings;
        _services.Settings.Save(settings);
    }

    private void OnVaultStateChanged()
    {
        RefreshVaultStatus();
        if (_services.Vault.State != VaultState.Unlocked || _pendingVaultRemovals.Count == 0)
            return;

        foreach (var id in _pendingVaultRemovals)
            _services.Vault.RemovePassphrase(id);
        _pendingVaultRemovals.Clear();
    }

    private void RefreshVaultStatus()
    {
        var state = _services.Vault.State;
        IsVaultUnlocked = state == VaultState.Unlocked;
        IsVaultSetUp = state != VaultState.NotSetUp;
        VaultStatus = BorgTexts.VaultState(state);
    }

    /// <summary>Removes the passphrase of a repository being deleted, wherever it is kept.</summary>
    private async Task RemovePassphraseAsync(RepositoryConfig repository)
    {
        if (repository.PassphraseMode == PassphraseMode.Vault && _services.Vault.State != VaultState.Unlocked)
        {
            _pendingVaultRemovals.Add(repository.Id);
            return;
        }

        if (new PassphraseStore(_services).Delete(repository, repository.PassphraseMode) is { } error)
            await _services.Dialogs.ShowMessageAsync(Strings.RemoveTitle, error);
    }
}
