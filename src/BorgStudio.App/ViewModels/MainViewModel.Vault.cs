using BorgStudio.App.Resources;
using BorgStudio.Core.Repositories;
using BorgStudio.Core.Security;
using BorgStudio.Core.Settings;
using BorgStudio.Core.Ssh;
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

    /// <summary>The key migration in progress (or the last one).</summary>
    private Task? _keyMigration;

    /// <summary>A hint in the status bar, e.g. that key files were moved into the vault.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotice))]
    public partial string? Notice { get; private set; }

    public bool HasNotice => Notice is not null;

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
        RefreshKeyNames();
        _services.Archives.Secure();
        if (_services.Vault.State != VaultState.Unlocked)
            return;

        foreach (var id in _pendingVaultRemovals)
            _services.Vault.RemovePassphrase(id);
        _pendingVaultRemovals.Clear();
        _ = MigrateLegacyKeysAsync();
    }

    /// <summary>Shows the names of the repositories' keys (known only while the vault is open).</summary>
    public void RefreshKeyNames()
    {
        var unlocked = _services.Vault.State == VaultState.Unlocked;
        foreach (var item in Repositories)
            item.SshKeyName = unlocked && item.Config.SshKeyId is { } id ? _services.Vault.GetKey(id)?.Name : null;
    }

    /// <summary>
    /// At startup: repositories still use key files from an older version, but there is no PIN yet. Keys belong in the
    /// vault, so ask for one; setting it up moves the keys right away.
    /// </summary>
    public async Task OfferVaultForLegacyKeysAsync()
    {
        if (_services.Vault.State != VaultState.NotSetUp || !Repositories.Any(item => item.Config.HasLegacySshKey))
            return;

        await _services.Dialogs.ShowMessageAsync(Strings.KeysMigrationTitle, Strings.KeysMigrationNeedsPin);
        try
        {
            await VaultAccess.EnsureUnlockedAsync(_services.Vault, _services.Dialogs);
        }
        catch (SecretBoxException exception)
        {
            Notice = BorgTexts.Format(Strings.VaultError, exception.Message);
        }
    }

    /// <summary>
    /// Moves key files of older versions into the (open) vault; runs by itself after unlocking. While a migration is
    /// running, this waits for it instead of starting another.
    /// </summary>
    public Task MigrateLegacyKeysAsync() =>
        _keyMigration is { IsCompleted: false } running ? running : _keyMigration = RunKeyMigrationAsync();

    private async Task RunKeyMigrationAsync()
    {
        if (_services.Vault.State != VaultState.Unlocked || !Repositories.Any(item => item.Config.HasLegacySshKey))
            return;

        try
        {
            var result = await SshKeyMigration.MigrateAsync([.. Repositories.Select(item => item.Config)], _services.Vault,
                _services.SshKeys, _services.SshKeygen, DateTimeOffset.Now);
            for (var index = 0; index < Repositories.Count && index < result.Repositories.Count; index++)
                Repositories[index].Config = result.Repositories[index];
            SaveRepositories();
            foreach (var file in result.FilesToDelete)
                _services.SshKeys.Delete(file);

            Notice = result.Failed.Count == 0
                ? BorgTexts.Format(Strings.KeysMigrated, result.Moved)
                : BorgTexts.Format(Strings.KeysMigrationFailed, string.Join(", ", result.Failed));
            RefreshKeyNames();
        }
        catch (Exception exception) when (exception is VaultLockedException or IOException or UnauthorizedAccessException)
        {
            Notice = BorgTexts.Format(Strings.KeysMigrationFailed, exception.Message);
        }
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
