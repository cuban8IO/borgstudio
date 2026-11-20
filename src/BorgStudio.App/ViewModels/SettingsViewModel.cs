using System.Globalization;
using BorgStudio.App.Resources;
using BorgStudio.App.Services;
using BorgStudio.Core.Security;
using BorgStudio.Core.Settings;
using BorgStudio.Core.Trigger;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BorgStudio.App.ViewModels;

/// <summary>The settings window: the UI language, the vault (PIN, lock, reset) and its automatic lock.</summary>
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

        LanguageOptions =
        [
            new(null, BorgTexts.Format(Strings.SettingsLanguageSystem, LanguageName(AppLanguages.Resolve(null, AppLanguages.SystemCulture)))),
            .. AppLanguages.Supported.Select(name => new Option<string?>(name, LanguageName(CultureInfo.GetCultureInfo(name)))),
        ];
        SelectedLanguage = LanguageOptions.FirstOrDefault(option => option.Value == main.Settings.Language) ?? LanguageOptions[0];
        RestartNeeded = AppLanguages.NeedsRestart(main.Settings.Language);
        CheckForUpdates = main.Settings.CheckForUpdates;
        VersionText = BorgTexts.Format(Strings.SettingsVersion, main.Updates.CurrentText);

        _vault.StateChanged += OnVaultStateChanged;

        var trigger = main.Settings.Trigger;
        _triggerEnabled = trigger.Enabled;
        _triggerPort = trigger.Port;
        _triggerAllowNetwork = trigger.AllowNetwork;
        _triggerToken = ReadTriggerToken();
        main.PropertyChanged += OnMainPropertyChanged;
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

    /// <summary>"System language (English)", then each language in its own name ("Deutsch").</summary>
    public IReadOnlyList<Option<string?>> LanguageOptions { get; }

    [ObservableProperty]
    public partial Option<string?> SelectedLanguage { get; set; }

    /// <summary>The chosen language is not the one shown: it takes effect after a restart.</summary>
    [ObservableProperty]
    public partial bool RestartNeeded { get; private set; }

    partial void OnSelectedLanguageChanged(Option<string?> value)
    {
        // Called while the constructor sets the initial value.
        if (value is null || _main is null)
            return;
        if (value.Value != _main.Settings.Language)
            _main.UpdateSettings(_main.Settings with { Language = value.Value });
        RestartNeeded = AppLanguages.NeedsRestart(value.Value);
    }

    [RelayCommand]
    private void Restart() => Dialogs.RestartApp();

    public string VersionText { get; }

    [ObservableProperty]
    public partial bool CheckForUpdates { get; set; }

    partial void OnCheckForUpdatesChanged(bool value)
    {
        if (_main is not null && value != _main.Settings.CheckForUpdates)
            _main.UpdateSettings(_main.Settings with { CheckForUpdates = value });
    }

    /// <summary>The system's task scheduler starts the scheduled jobs.</summary>
    public bool RunWithSystem
    {
        get => !_main.IsBackgroundMode;
        set
        {
            if (value)
                SetScheduleRunner(ScheduleRunner.System);
        }
    }

    /// <summary>BorgStudio starts them itself, from the notification area.</summary>
    public bool RunInBackground
    {
        get => _main.IsBackgroundMode;
        set
        {
            if (value)
                SetScheduleRunner(ScheduleRunner.Background);
        }
    }

    private void SetScheduleRunner(ScheduleRunner runner)
    {
        _main.SetScheduleRunner(runner);
        OnPropertyChanged(nameof(RunWithSystem));
        OnPropertyChanged(nameof(RunInBackground));
    }

    [RelayCommand]
    private Task CheckForUpdatesNowAsync() => Dialogs.ShowUpdateAsync(new UpdateViewModel(_main, Dialogs, known: null));

    private static string LanguageName(CultureInfo culture)
    {
        var name = CultureInfo.GetCultureInfo(AppLanguages.Neutral(culture)).NativeName;
        return name.Length > 0 ? char.ToUpper(name[0], culture) + name[1..] : name;
    }

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
    public void Detach()
    {
        _vault.StateChanged -= OnVaultStateChanged;
        _main.PropertyChanged -= OnMainPropertyChanged;
    }

    // --- Incoming webhook ---

    private bool _triggerEnabled;
    private decimal? _triggerPort;
    private bool _triggerAllowNetwork;
    private string? _triggerToken;

    private void OnMainPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.TriggerStatus) or nameof(MainViewModel.IsBackgroundMode))
        {
            OnPropertyChanged(nameof(TriggerStatus));
            OnPropertyChanged(nameof(CanUseTrigger));
        }
    }

    /// <summary>Only BorgStudio in the background can listen.</summary>
    public bool CanUseTrigger => _main.IsBackgroundMode;

    public string? TriggerStatus => _main.TriggerStatus;

    public bool TriggerEnabled
    {
        get => _triggerEnabled;
        set
        {
            if (!SetProperty(ref _triggerEnabled, value))
                return;
            // Switched on the first time: a token to go with it.
            if (value && _triggerToken is null)
                CreateTriggerToken();
            ApplyTriggerSettings();
        }
    }

    public decimal? TriggerPort
    {
        get => _triggerPort;
        set
        {
            if (SetProperty(ref _triggerPort, value))
            {
                ApplyTriggerSettings();
                OnPropertyChanged(nameof(TriggerExample));
            }
        }
    }

    public bool TriggerAllowNetwork
    {
        get => _triggerAllowNetwork;
        set
        {
            if (SetProperty(ref _triggerAllowNetwork, value))
                ApplyTriggerSettings();
        }
    }

    /// <summary>The token (shown in a password box, copyable).</summary>
    public string TriggerToken => _triggerToken ?? "";

    /// <summary>A call to start a job, for copying.</summary>
    public string TriggerExample =>
        $"curl -X POST -H \"Authorization: Bearer <token>\" \"http://localhost:{(int)(_triggerPort ?? TriggerSettings.DefaultPort)}/run/<job>?wait=1\"";

    [ObservableProperty]
    public partial string? TriggerMessage { get; private set; }

    private void ApplyTriggerSettings()
    {
        var port = (int)Math.Clamp(_triggerPort ?? TriggerSettings.DefaultPort, 1, 65535);
        var trigger = new TriggerSettings { Enabled = _triggerEnabled, Port = port, AllowNetwork = _triggerAllowNetwork };
        if (trigger == _main.Settings.Trigger)
            return;
        _main.UpdateSettings(_main.Settings with { Trigger = trigger });
        _main.ApplyTrigger();
    }

    [RelayCommand]
    private void CreateTriggerToken()
    {
        try
        {
            _triggerToken = _main.CreateTriggerToken();
            TriggerMessage = Strings.TriggerTokenCreated;
        }
        catch (Exception exception) when (exception is Core.Secrets.SecretStoreException or VaultLockedException)
        {
            TriggerMessage = BorgTexts.Format(Strings.NotifySecretFailed, exception.Message);
        }
        OnPropertyChanged(nameof(TriggerToken));
    }

    [RelayCommand]
    private async Task CopyTriggerTokenAsync()
    {
        if (_triggerToken is { } token)
        {
            await Dialogs.CopyToClipboardAsync(token);
            TriggerMessage = Strings.TriggerTokenCopied;
        }
    }

    [RelayCommand]
    private async Task CopyTriggerExampleAsync()
    {
        await Dialogs.CopyToClipboardAsync(TriggerExample.Replace("<token>", _triggerToken ?? "<token>", StringComparison.Ordinal));
        TriggerMessage = Strings.TriggerExampleCopied;
    }

    private string? ReadTriggerToken()
    {
        try
        {
            return _main.TriggerToken;
        }
        catch (VaultLockedException)
        {
            return null;
        }
    }

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
