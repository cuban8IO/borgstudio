using BorgStudio.App.Resources;
using BorgStudio.App.Services;
using BorgStudio.Core.Security;
using BorgStudio.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BorgStudio.App.ViewModels;

/// <summary>The settings window: the vault (PIN, lock, reset) and its automatic lock.</summary>
public sealed partial class SettingsViewModel : ViewModelBase
{
    private readonly MainViewModel _main;
    private readonly Vault _vault;

    public SettingsViewModel(MainViewModel main, IDialogService dialogs)
    {
        _main = main;
        _vault = main.Vault;
        Dialogs = dialogs;

        var minutes = main.Settings.VaultAutoLockMinutes;
        AutoLock = minutes > 0;
        SelectedAutoLockMinutes = AutoLockOptions.FirstOrDefault(option => option.Value == minutes)
            ?? AutoLockOptions.First(option => option.Value == AppSettings.DefaultVaultAutoLockMinutes);

        _vault.StateChanged += OnVaultStateChanged;
        Refresh();
    }

    /// <summary>Where the PIN dialogs appear; set by the settings window.</summary>
    public IDialogService Dialogs { get; set; }

    [ObservableProperty]
    public partial string VaultStatus { get; private set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotSetUp))]
    [NotifyCanExecuteChangedFor(nameof(ChangePinCommand), nameof(ResetVaultCommand), nameof(SetUpPinCommand))]
    public partial bool IsSetUp { get; private set; }

    public bool IsNotSetUp => !IsSetUp;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LockVaultCommand))]
    public partial bool IsUnlocked { get; private set; }

    /// <summary>Outcome of the last action, e.g. "PIN changed".</summary>
    [ObservableProperty]
    public partial string? Message { get; private set; }

    [ObservableProperty]
    public partial bool IsBusy { get; private set; }

    public IReadOnlyList<Option<int>> AutoLockOptions { get; } =
        [.. new[] { 5, 15, 30, 60 }.Select(minutes => new Option<int>(minutes, BorgTexts.Format(Strings.SettingsMinutes, minutes)))];

    [ObservableProperty]
    public partial bool AutoLock { get; set; }

    [ObservableProperty]
    public partial Option<int> SelectedAutoLockMinutes { get; set; }

    partial void OnAutoLockChanged(bool value) => SaveAutoLock();

    partial void OnSelectedAutoLockMinutesChanged(Option<int> value) => SaveAutoLock();

    private void SaveAutoLock()
    {
        // Called while the constructor sets the initial values, before the dependencies are there.
        if (SelectedAutoLockMinutes is null || _main is null)
            return;
        var minutes = AutoLock ? SelectedAutoLockMinutes.Value : 0;
        if (minutes != _main.Settings.VaultAutoLockMinutes)
            _main.UpdateSettings(_main.Settings with { VaultAutoLockMinutes = minutes });
    }

    [RelayCommand(CanExecute = nameof(IsNotSetUp))]
    private async Task SetUpPinAsync()
    {
        if (await Dialogs.AskNewPinAsync() is not { } pin)
            return;
        await RunAsync(() => _vault.SetUp(pin));
        Message = Strings.VaultSetUpDone;
    }

    [RelayCommand(CanExecute = nameof(IsSetUp))]
    private async Task ChangePinAsync()
    {
        if (await Dialogs.AskPinAsync() is not { } current)
            return;
        if (await Dialogs.AskNewPinAsync() is not { } newPin)
            return;

        var changed = false;
        await RunAsync(() => changed = _vault.ChangePin(current, newPin));
        Message = changed ? Strings.PinChanged : Strings.PinWrong;
    }

    [RelayCommand(CanExecute = nameof(IsUnlocked))]
    private void LockVault()
    {
        _vault.Lock();
        Message = null;
    }

    [RelayCommand(CanExecute = nameof(IsSetUp))]
    private async Task ResetVaultAsync()
    {
        if (!await Dialogs.ConfirmAsync(Strings.VaultResetTitle, Strings.VaultResetWarning, Strings.VaultResetConfirm))
            return;
        _vault.Reset();
        Message = Strings.VaultResetDone;
    }

    /// <summary>Called when the window closes.</summary>
    public void Detach() => _vault.StateChanged -= OnVaultStateChanged;

    private async Task RunAsync(Action action)
    {
        IsBusy = true;
        try
        {
            // Key derivation takes a moment.
            await Task.Run(action);
        }
        catch (SecretBoxException exception)
        {
            Message = BorgTexts.Format(Strings.VaultError, exception.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void OnVaultStateChanged(object? sender, EventArgs e) => OnViewModelThread(Refresh);

    private void Refresh()
    {
        var state = _vault.State;
        IsSetUp = state != VaultState.NotSetUp;
        IsUnlocked = state == VaultState.Unlocked;
        VaultStatus = BorgTexts.VaultState(state);
    }
}
