using BorgStudio.App.Resources;
using BorgStudio.Core.Repositories;
using BorgStudio.Core.Security;
using BorgStudio.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BorgStudio.App.ViewModels;

/// <summary>
/// The vault in the main window. With a PIN set up, the vault doubles as the app lock: BorgStudio starts locked, shows
/// only the lock screen while locked, and unlocking the app unlocks the vault – so passphrases are not asked for again.
/// </summary>
public partial class MainViewModel
{
    /// <summary>Repositories removed while the vault was locked: their passphrases go once it is unlocked again.</summary>
    private readonly HashSet<Guid> _pendingVaultRemovals = [];

    public AppSettings Settings { get; private set; } = new();

    public Vault Vault => _services.Vault;

    [ObservableProperty]
    public partial string VaultStatus { get; private set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLocked), nameof(IsAppVisible))]
    [NotifyCanExecuteChangedFor(nameof(LockVaultCommand))]
    public partial bool IsVaultUnlocked { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLocked), nameof(IsAppVisible))]
    public partial bool IsVaultSetUp { get; private set; }

    /// <summary>The app is locked: only the lock screen is shown.</summary>
    public bool IsLocked => IsVaultSetUp && !IsVaultUnlocked;

    public bool IsAppVisible => !IsLocked;

    /// <summary>The PIN typed on the lock screen.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UnlockCommand))]
    public partial string LockScreenPin { get; set; } = "";

    [ObservableProperty]
    public partial string? LockScreenError { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UnlockCommand), nameof(ForgotPinCommand))]
    public partial bool IsUnlocking { get; private set; }

    [RelayCommand(CanExecute = nameof(CanUnlock))]
    private async Task UnlockAsync()
    {
        IsUnlocking = true;
        LockScreenError = null;
        try
        {
            var pin = LockScreenPin;
            // Key derivation takes a moment: keep the UI responsive.
            if (await Task.Run(() => _services.Vault.Unlock(pin)))
                LockScreenPin = "";
            else
                LockScreenError = Strings.PinWrong;
        }
        catch (SecretBoxException exception)
        {
            LockScreenError = BorgTexts.Format(Strings.VaultError, exception.Message);
        }
        finally
        {
            IsUnlocking = false;
        }
    }

    private bool CanUnlock() => !IsUnlocking && LockScreenPin.Length > 0;

    /// <summary>Forgotten PIN: delete the vault (after a warning) – that also lifts the lock.</summary>
    [RelayCommand(CanExecute = nameof(IsNotUnlocking))]
    private async Task ForgotPinAsync()
    {
        if (!await _services.Dialogs.ConfirmAsync(Strings.VaultResetTitle, Strings.VaultResetWarning, Strings.VaultResetConfirm))
            return;
        _services.Vault.Reset();
        LockScreenPin = "";
        LockScreenError = null;
    }

    private bool IsNotUnlocking() => !IsUnlocking;

    private void InitializeVault()
    {
        // Nothing to do for the lock itself: a vault with a PIN starts locked, so the app does too.
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
