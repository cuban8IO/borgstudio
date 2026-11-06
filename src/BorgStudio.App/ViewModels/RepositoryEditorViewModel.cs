using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using BorgStudio.App.Resources;
using BorgStudio.App.Services;
using BorgStudio.Core.Borg;
using BorgStudio.Core.Progress;
using BorgStudio.Core.Repositories;
using BorgStudio.Core.Secrets;
using BorgStudio.Core.Security;
using BorgStudio.Core.Ssh;
using BorgStudio.Plugins;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BorgStudio.App.ViewModels;

public sealed record RepositoryEditorResult(RepositoryConfig Repository, bool Created);

/// <summary>How BorgStudio logs in to an SSH repository.</summary>
public enum SshKeyChoice
{
    /// <summary>Editing: keep the current key.</summary>
    Keep,

    /// <summary>Create a key for this repository and install it with the server password.</summary>
    Generate,

    /// <summary>Use a key from BorgStudio's key list (the vault).</summary>
    Existing,
}

/// <summary>
/// Adds a repository (pick a provider, then connect an existing or create a new one) or edits an existing one.
/// Adding only succeeds once borg could open the repository. SSH repositories get a confirmed host key and a login key.
/// </summary>
public sealed partial class RepositoryEditorViewModel : ViewModelBase
{
    private readonly AppServices _services;
    private readonly BorgInstallation? _borg;
    private readonly RepositoryConfig? _existing;
    private readonly Guid _id;
    private readonly PassphraseStore _passphrases;
    private readonly IReadOnlyList<RepositoryConfig> _otherRepositories;

    /// <summary>A key created and installed in this dialog: reused for retries, saved with the repository.</summary>
    private InstalledKey? _newKey;

    /// <summary>Add a repository, or edit <paramref name="existing"/> (its provider must be available).</summary>
    /// <param name="otherRepositories">The other repositories, to warn about keys shared in a way that cannot work.</param>
    public RepositoryEditorViewModel(AppServices services, BorgInstallation? borg, RepositoryConfig? existing = null,
        IReadOnlyList<RepositoryConfig>? otherRepositories = null)
    {
        _services = services;
        _borg = borg;
        _existing = existing;
        _otherRepositories = otherRepositories ?? [];
        _id = existing?.Id ?? Guid.NewGuid();
        _passphrases = new PassphraseStore(services);
        Dialogs = services.Dialogs;
        Providers = services.Plugins.RepositoryProviders.Select(provider => new ProviderOptionViewModel(provider)).ToList();
        SelectedEncryption = EncryptionOptions[0];
        SelectedBorgVersion = BorgVersionOptions[0];

        if (existing is null)
        {
            SelectedProvider = Providers.FirstOrDefault();
            PassphraseStorage = PassphraseMode.Vault;
            KeyChoice = SshKeyChoice.Generate;
            Page = Providers.Count == 1 ? EditorPage.Details : EditorPage.SelectProvider;
            if (Page == EditorPage.Details)
                CreateFields(SelectedProvider!.Provider, values: null);
            RefreshKeys();
            return;
        }

        SelectedProvider = Providers.FirstOrDefault(option => option.Provider.Id == existing.ProviderId)
            ?? throw new InvalidOperationException($"Provider '{existing.ProviderId}' is not available.");
        Name = existing.Name;
        PassphraseStorage = existing.PassphraseMode == PassphraseMode.None ? PassphraseMode.Vault : existing.PassphraseMode;
        SelectedBorgVersion = BorgVersionOptions.First(option => option.Value == existing.BorgVersion);
        KeyChoice = existing.SshKeyId is null ? SshKeyChoice.Generate : SshKeyChoice.Keep;
        RestrictKey = existing.SshKeyId is null || existing.SshKeyRestricted;
        CreateFields(SelectedProvider.Provider, existing.ProviderValues);
        Page = EditorPage.Details;
        RefreshKeys();
    }

    public enum EditorPage
    {
        SelectProvider,
        Details,
    }

    /// <summary>Where nested dialogs (host key confirmation) appear; set by the editor window.</summary>
    public IDialogService Dialogs { get; set; }

    public RepositoryEditorResult? Result { get; private set; }

    public event EventHandler? CloseRequested;

    public bool IsEditing => _existing is not null;

    public string Title => IsEditing ? Strings.EditorTitleEdit : Strings.EditorTitleAdd;

    // --- Page 1: provider ---

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsProviderPage), nameof(IsDetailsPage), nameof(CanGoBack))]
    public partial EditorPage Page { get; set; }

    public bool IsProviderPage => Page == EditorPage.SelectProvider;

    public bool IsDetailsPage => Page == EditorPage.Details;

    public IReadOnlyList<ProviderOptionViewModel> Providers { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NextCommand))]
    [NotifyPropertyChangedFor(nameof(IsSsh))]
    public partial ProviderOptionViewModel? SelectedProvider { get; set; }

    // --- Page 2: details ---

    [ObservableProperty]
    public partial string Name { get; set; } = "";

    [ObservableProperty]
    public partial IReadOnlyList<FieldViewModel> Fields { get; set; } = [];

    /// <summary>Adding: create a new repository instead of connecting an existing one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEncryption), nameof(UsesPassphrase), nameof(ShowPassphraseConfirmation),
        nameof(PassphraseHint), nameof(ConfirmText))]
    public partial bool CreateNew { get; set; }

    public bool ShowModeChoice => !IsEditing;

    public IReadOnlyList<Option<BorgEncryption>> EncryptionOptions { get; } =
    [
        new(BorgEncryption.RepokeyBlake2, Strings.EncryptionRepokey),
        new(BorgEncryption.KeyfileBlake2, Strings.EncryptionKeyfile),
        new(BorgEncryption.None, Strings.EncryptionNone),
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UsesPassphrase), nameof(ShowPassphraseConfirmation), nameof(EncryptionHint))]
    public partial Option<BorgEncryption> SelectedEncryption { get; set; }

    public bool ShowEncryption => !IsEditing && CreateNew;

    public string? EncryptionHint => SelectedEncryption.Value switch
    {
        BorgEncryption.KeyfileBlake2 => Strings.EncryptionKeyfileHint,
        BorgEncryption.None => Strings.EncryptionNoneHint,
        _ => null,
    };

    /// <summary>Whether a passphrase is involved at all (not for unencrypted repositories).</summary>
    public bool UsesPassphrase => _existing is not null
        ? _existing.PassphraseMode != PassphraseMode.None
        : !CreateNew || SelectedEncryption.Value != BorgEncryption.None;

    [ObservableProperty]
    public partial string Passphrase { get; set; } = "";

    [ObservableProperty]
    public partial string PassphraseConfirmation { get; set; } = "";

    public bool ShowPassphraseConfirmation => !IsEditing && CreateNew && UsesPassphrase;

    public string PassphraseHint =>
        IsEditing ? Strings.EditorPassphraseEditHint
        : CreateNew ? Strings.EditorPassphraseCreateHint
        : Strings.EditorPassphraseConnectHint;

    /// <summary>Where the passphrase is kept: <see cref="PassphraseMode.Vault"/>, <see cref="PassphraseMode.Stored"/> (keychain) or asked for.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StorageVault), nameof(StorageKeychain), nameof(StorageAsk))]
    public partial PassphraseMode PassphraseStorage { get; set; }

    public bool StorageVault
    {
        get => PassphraseStorage == PassphraseMode.Vault;
        set { if (value) PassphraseStorage = PassphraseMode.Vault; }
    }

    public bool StorageKeychain
    {
        get => PassphraseStorage == PassphraseMode.Stored;
        set { if (value) PassphraseStorage = PassphraseMode.Stored; }
    }

    public bool StorageAsk
    {
        get => PassphraseStorage == PassphraseMode.Ask;
        set { if (value) PassphraseStorage = PassphraseMode.Ask; }
    }

    public bool CanUseKeychain => _services.SecretStore.IsAvailable;

    public bool KeychainUnavailable => !_services.SecretStore.IsAvailable;

    /// <summary>The storage that will actually be used (the keychain only where there is one).</summary>
    private PassphraseMode EffectiveStorage =>
        PassphraseStorage == PassphraseMode.Stored && !CanUseKeychain ? PassphraseMode.Ask : PassphraseStorage;

    // --- SSH login ---

    public bool IsSsh => SelectedProvider?.Provider.UsesSsh == true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KeyChoiceKeep), nameof(KeyChoiceGenerate), nameof(KeyChoiceExisting), nameof(ShowInstallOptions))]
    public partial SshKeyChoice KeyChoice { get; set; }

    public bool ShowKeepKeyOption => _existing?.SshKeyId is not null;

    public bool KeyChoiceKeep
    {
        get => KeyChoice == SshKeyChoice.Keep;
        set { if (value) KeyChoice = SshKeyChoice.Keep; }
    }

    public bool KeyChoiceGenerate
    {
        get => KeyChoice == SshKeyChoice.Generate;
        set { if (value) KeyChoice = SshKeyChoice.Generate; }
    }

    public bool KeyChoiceExisting
    {
        get => KeyChoice == SshKeyChoice.Existing;
        set { if (value) KeyChoice = SshKeyChoice.Existing; }
    }

    /// <summary>Used once to install a key; never stored.</summary>
    [ObservableProperty]
    public partial string ServerPassword { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SharedKeyWarning), nameof(HasSharedKeyWarning))]
    public partial bool RestrictKey { get; set; } = true;

    /// <summary>The keys in the vault to choose from (empty while it is not set up).</summary>
    public ObservableCollection<SshKeyOption> AvailableKeys { get; } = [];

    public bool HasNoKeys => AvailableKeys.Count == 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SharedKeyWarning), nameof(HasSharedKeyWarning))]
    public partial SshKeyOption? SelectedKey { get; set; }

    /// <summary>A key from the list: also add it to the server's authorized_keys (with the server password).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SharedKeyWarning), nameof(HasSharedKeyWarning), nameof(ShowInstallOptions))]
    public partial bool InstallExistingKey { get; set; }

    /// <summary>Server password and restriction: for a generated key, or a key from the list that is to be installed.</summary>
    public bool ShowInstallOptions => KeyChoice == SshKeyChoice.Generate || (KeyChoice == SshKeyChoice.Existing && InstallExistingKey);

    /// <summary>
    /// The chosen key is already used by another repository on the same account and one of them is restricted to its
    /// repository: the server only uses the first matching line of authorized_keys, so one of them would fail.
    /// </summary>
    public string? SharedKeyWarning
    {
        get
        {
            if (KeyChoice != SshKeyChoice.Existing || SelectedKey is null || CurrentEndpoint() is not { } endpoint)
                return null;
            var restrictedHere = InstallExistingKey && RestrictKey;
            var shared = _otherRepositories.Where(other => other.Id != _id && other.SshKeyId == SelectedKey.Key.Id)
                .Select(other => (Repository: other, Endpoint: EndpointOf(other)))
                .Where(other => other.Endpoint is { } e && e.Host == endpoint.Host && e.Port == endpoint.Port && e.User == endpoint.User)
                .ToList();
            return shared.Count > 0 && (restrictedHere || shared.Any(other => other.Repository.SshKeyRestricted))
                ? BorgTexts.Format(Strings.EditorSharedRestrictedKey, shared[0].Repository.Name)
                : null;
        }
    }

    public bool HasSharedKeyWarning => SharedKeyWarning is not null;

    partial void OnKeyChoiceChanged(SshKeyChoice value) => OnPropertyChanged(nameof(SharedKeyWarning));

    /// <summary>Imports a private key file into the key list and selects it.</summary>
    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task ImportKeyAsync()
    {
        Errors.Clear();
        if (!await UnlockVaultAsync())
            return;
        if (await Dialogs.PickOpenFileAsync(Strings.KeysImportTitle) is not { } file)
            return;

        var (key, error) = await SshKeyImport.ImportAsync(_services, Dialogs, file);
        if (error is not null)
            Errors.Add(error);
        if (key is null)
            return;

        RefreshKeys();
        SelectedKey = AvailableKeys.FirstOrDefault(option => option.Key.Id == key.Id);
        KeyChoice = SshKeyChoice.Existing;
    }

    /// <summary>Fills <see cref="AvailableKeys"/> from the vault (if it is open).</summary>
    private void RefreshKeys()
    {
        var selected = SelectedKey?.Key.Id;
        AvailableKeys.Clear();
        if (_services.Vault.State == VaultState.Unlocked)
            foreach (var key in _services.Vault.Keys)
                AvailableKeys.Add(new SshKeyOption(key));
        SelectedKey = AvailableKeys.FirstOrDefault(option => option.Key.Id == selected) ?? AvailableKeys.FirstOrDefault();
        OnPropertyChanged(nameof(HasNoKeys));
    }

    private SshEndpoint? CurrentEndpoint()
    {
        try
        {
            return SelectedProvider?.Provider.GetLocation(Fields.ToDictionary(field => field.Key, field => field.Value.Trim())).Ssh;
        }
        catch (Exception exception) when (exception is KeyNotFoundException or ArgumentException or FormatException)
        {
            return null;
        }
    }

    private SshEndpoint? EndpointOf(RepositoryConfig repository)
    {
        try
        {
            return _services.Plugins.FindRepositoryProvider(repository.ProviderId)?.GetLocation(repository.ProviderValues).Ssh;
        }
        catch (Exception exception) when (exception is KeyNotFoundException or ArgumentException or FormatException)
        {
            return null;
        }
    }

    // --- Advanced ---

    public IReadOnlyList<Option<BorgVersionPreference>> BorgVersionOptions { get; } =
    [
        new(BorgVersionPreference.Auto, Strings.BorgVersionAuto),
        new(BorgVersionPreference.Borg1, Strings.BorgVersionBorg1),
        new(BorgVersionPreference.Borg2, Strings.BorgVersionBorg2),
    ];

    [ObservableProperty]
    public partial Option<BorgVersionPreference> SelectedBorgVersion { get; set; }

    public ObservableCollection<string> Errors { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand), nameof(BackCommand), nameof(CancelCommand))]
    public partial bool IsBusy { get; set; }

    /// <summary>The step being worked on, with elapsed time and – from last time – roughly how long it takes.</summary>
    public ProgressViewModel Progress { get; } = new();

    /// <summary>Under which key the running step's duration is remembered; <c>null</c> if not worth it.</summary>
    private string? _busyKey;

    public string ConfirmText => IsEditing ? Strings.EditorSave : CreateNew ? Strings.EditorCreate : Strings.EditorConnect;

    public bool CanGoBack => !IsEditing && IsDetailsPage && Providers.Count > 1;

    // --- Commands ---

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private void Next()
    {
        CreateFields(SelectedProvider!.Provider, values: null);
        Errors.Clear();
        Page = EditorPage.Details;
    }

    private bool CanGoNext() => SelectedProvider is not null;

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private void Back()
    {
        Errors.Clear();
        Page = EditorPage.SelectProvider;
    }

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private void Cancel() => CloseRequested?.Invoke(this, EventArgs.Empty);


    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task ConfirmAsync()
    {
        Errors.Clear();
        var provider = SelectedProvider!.Provider;
        var values = Fields.ToDictionary(field => field.Key, field => field.Value.Trim());

        var problems = Validate(provider, values);
        if (problems.Count > 0)
        {
            ShowErrors(problems);
            return;
        }

        IsBusy = true;
        try
        {
            if (_existing is not null)
                await SaveChangesAsync(_existing, provider, values);
            else
                await AddAsync(provider, values);
        }
        finally
        {
            FinishBusyStep();
            Progress.Stop();
            IsBusy = false;
        }

        // Only once idle: the window refuses to close while busy.
        if (Result is not null)
            CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private bool IsIdle() => !IsBusy;

    /// <summary>The next step starts; the previous one's duration is remembered if it went well.</summary>
    private void Busy(string text, string? operation)
    {
        FinishBusyStep();
        _busyKey = operation is null ? null
            : DurationHistory.Key(_existing is { } existing ? existing.Id.ToString("D") : $"provider-{SelectedProvider?.Provider.Id}", operation);
        Progress.Start(text, _busyKey is null ? null : _services.Durations.Get(_busyKey));
    }

    private void FinishBusyStep()
    {
        if (_busyKey is not null && Progress.IsActive && Errors.Count == 0)
            _services.Durations.Record(_busyKey, Progress.Elapsed);
        _busyKey = null;
    }

    private List<string> Validate(IRepositoryProvider provider, IReadOnlyDictionary<string, string> values)
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(Name))
            problems.Add(Strings.EditorNameRequired);
        problems.AddRange(provider.Validate(values));

        if (ShowPassphraseConfirmation)
        {
            if (Passphrase.Length == 0)
                problems.Add(Strings.EditorPassphraseRequired);
            else if (Passphrase != PassphraseConfirmation)
                problems.Add(Strings.EditorPassphraseMismatch);
        }

        // Switching an existing repository to "stored" needs the passphrase to store.
        if (_existing is { PassphraseMode: PassphraseMode.Ask } && EffectiveStorage != PassphraseMode.Ask && Passphrase.Length == 0)
            problems.Add(Strings.EditorPassphraseRequiredToStore);

        if (IsSsh)
        {
            if (KeyChoice == SshKeyChoice.Generate && ServerPassword.Length == 0 && !HasInstalledKeyFor(provider, values))
                problems.Add(Strings.EditorServerPasswordRequired);
            if (KeyChoice == SshKeyChoice.Existing && SelectedKey is null)
                problems.Add(Strings.EditorKeyRequired);
            else if (KeyChoice == SshKeyChoice.Existing && InstallExistingKey && ServerPassword.Length == 0)
                problems.Add(Strings.EditorServerPasswordRequired);
        }

        return problems;
    }

    private async Task AddAsync(IRepositoryProvider provider, IReadOnlyDictionary<string, string> values)
    {
        if (_borg is not { Support: BorgSupport.Supported } borg)
        {
            Errors.Add(Strings.BorgNotReady);
            return;
        }

        var location = provider.GetLocation(values);
        var borgVersion = SelectedBorgVersion.Value;
        if (RepositoryFormat.Resolve(borgVersion, location) == 2)
        {
            Errors.Add(Strings.Borg2NotSupported);
            return;
        }

        // Open (or set up) the vault before anything happens on the server, so cancelling changes nothing.
        // SSH keys live in the vault, so SSH repositories always need it.
        if ((IsSsh || (UsesPassphrase && EffectiveStorage == PassphraseMode.Vault)) && !await UnlockVaultAsync())
            return;

        var login = IsSsh ? await PrepareSshLoginAsync(location) : SshLogin.None;
        if (login is null)
            return;

        var passphrase = UsesPassphrase && Passphrase.Length > 0 ? Passphrase : null;
        if (CreateNew)
        {
            Busy(Strings.Creating, "init");
            var created = await _services.BorgClient.InitAsync(borg, location, SelectedEncryption.Value, passphrase, login.Access);
            if (!created.Succeeded)
            {
                Errors.Add(BorgTexts.Describe(created.Error!));
                return;
            }
        }

        Busy(Strings.Checking, "info");
        var info = await _services.BorgClient.InfoAsync(borg, location, passphrase, login.Access);
        if (!info.Succeeded)
        {
            Errors.Add(BorgTexts.Describe(info.Error!));
            return;
        }

        var encrypted = info.Value!.EncryptionMode != "none";
        var repository = new RepositoryConfig
        {
            Id = _id,
            Name = Name.Trim(),
            ProviderId = provider.Id,
            ProviderValues = values,
            PassphraseMode = encrypted ? EffectiveStorage : PassphraseMode.None,
            BorgVersion = borgVersion,
            EncryptionMode = info.Value.EncryptionMode,
            BorgRepositoryId = info.Value.Id,
            SshKeyId = login.KeyId,
            SshKeyRestricted = login.Restricted,
        };

        if (repository.PassphraseMode is PassphraseMode.Vault or PassphraseMode.Stored
            && await _passphrases.SetAsync(repository, repository.PassphraseMode, Passphrase, Dialogs) is { } error)
        {
            Errors.Add(error);
            return;
        }

        if (!SaveNewKey(login))
            return;
        Result = new RepositoryEditorResult(repository, CreateNew);
    }

    private async Task SaveChangesAsync(RepositoryConfig existing, IRepositoryProvider provider, IReadOnlyDictionary<string, string> values)
    {
        var repository = existing with
        {
            Name = Name.Trim(),
            ProviderValues = values,
            BorgVersion = SelectedBorgVersion.Value,
        };

        SshLogin? login = null;
        if (IsSsh && KeyChoice != SshKeyChoice.Keep)
        {
            if (!await UnlockVaultAsync())
                return;
            login = await PrepareSshLoginAsync(provider.GetLocation(values));
            if (login is null)
                return;
            repository = repository with { SshKeyId = login.KeyId, SshKeyRestricted = login.Restricted };
        }

        var oldMode = existing.PassphraseMode;
        var mode = oldMode == PassphraseMode.None ? PassphraseMode.None : EffectiveStorage;
        repository = repository with { PassphraseMode = mode };
        if (!await StorePassphraseAsync(existing, repository, oldMode, mode))
            return;

        // A replaced key stays in the key list (other repositories may use it); it can be deleted there.
        if (login is not null && !SaveNewKey(login))
            return;

        Result = new RepositoryEditorResult(repository, Created: false);
    }

    /// <summary>
    /// Confirms the server's host key and sets up the login key as chosen; <c>null</c> if that failed
    /// (the reason is in <see cref="Errors"/>).
    /// </summary>
    private async Task<SshLogin?> PrepareSshLoginAsync(RepositoryLocation location)
    {
        var endpoint = location.Ssh ?? throw new InvalidOperationException("SSH provider without SSH endpoint.");
        var knownHosts = _services.SshKeys.KnownHostsFile;

        // Includes confirming the host key: the user's time, nothing to remember.
        Busy(Strings.ContactingServer, operation: null);
        var (hostKey, problem) = await SshTrustDialog.EnsureTrustedAsync(_services.SshTrust, endpoint, Dialogs);
        if (hostKey is null)
        {
            Errors.Add(problem!);
            return null;
        }

        var remoteBorg = SshTrustDialog.RemoteBorg(location);
        if (KeyChoice == SshKeyChoice.Existing)
        {
            var chosen = SelectedKey!.Key;
            if (InstallExistingKey && !await InstallAsync(endpoint, hostKey, chosen.PublicKey, remoteBorg))
                return null;
            // Restricted only if installed that way here; otherwise it is unknown how the key was set up.
            var restricted = InstallExistingKey ? RestrictKey : _existing?.SshKeyId == chosen.Id && _existing.SshKeyRestricted;
            return new SshLogin(new SshAccess(chosen.PrivateKey, knownHosts), chosen.Id, restricted, NewKey: null);
        }

        if (_newKey is { } created && created.Fits(location, RestrictKey))
            return new SshLogin(new SshAccess(created.Key.PrivateKey, knownHosts), created.Key.Id, created.Restricted, created.Key);

        // A new key per attempt with different settings; the previous one was never saved.
        _newKey = null;
        var key = SshKeyEntry.Generate(Name.Trim(), KeyComment(), DateTimeOffset.Now);
        if (!await InstallAsync(endpoint, hostKey, key.PublicKey, remoteBorg))
            return null;

        _newKey = new InstalledKey(endpoint, remoteBorg, key, RestrictKey);
        return new SshLogin(new SshAccess(key.PrivateKey, knownHosts), key.Id, RestrictKey, key);
    }

    /// <summary>Adds <paramref name="publicKey"/> to the server with the server password; <c>false</c> with the reason in <see cref="Errors"/>.</summary>
    private async Task<bool> InstallAsync(SshEndpoint endpoint, SshHostKey hostKey, string publicKey, string remoteBorg)
    {
        Busy(Strings.InstallingKey, "install-key");
        try
        {
            await SshKeyInstaller.InstallAsync(_services.Ssh, endpoint, ServerPassword, hostKey, publicKey, RestrictKey, remoteBorg);
            return true;
        }
        catch (SshOperationException exception)
        {
            Errors.Add(SshTrustDialog.Describe(exception));
            return false;
        }
    }

    /// <summary>Puts a key created in this dialog into the vault; <c>false</c> with the reason in <see cref="Errors"/>.</summary>
    private bool SaveNewKey(SshLogin login)
    {
        if (login.NewKey is not { } key)
            return true;
        try
        {
            _services.Vault.SetKey(key);
            return true;
        }
        catch (VaultLockedException)
        {
            Errors.Add(Strings.VaultNotUnlocked);
            return false;
        }
    }

    private bool HasInstalledKeyFor(IRepositoryProvider provider, IReadOnlyDictionary<string, string> values) =>
        _newKey is { } created && created.Fits(provider.GetLocation(values), RestrictKey);


    private string KeyComment() =>
        $"borgstudio-{UnsafeCommentCharacters().Replace(Environment.MachineName, "_")}-{_id.ToString("N")[..8]}";

    /// <summary>
    /// Editing: stores a newly entered passphrase, or moves the stored one when only the storage changed (vault ↔ keychain),
    /// and removes it from where it no longer belongs. <c>false</c> if that failed (the reason is in <see cref="Errors"/>).
    /// </summary>
    private async Task<bool> StorePassphraseAsync(RepositoryConfig existing, RepositoryConfig repository,
        PassphraseMode oldMode, PassphraseMode mode)
    {
        if (mode is PassphraseMode.Vault or PassphraseMode.Stored)
        {
            string? secret = Passphrase.Length > 0 ? Passphrase : null;
            if (secret is null && mode != oldMode && oldMode is PassphraseMode.Vault or PassphraseMode.Stored)
            {
                var (stored, cancelled, readError) = await _passphrases.GetAsync(existing, oldMode, Dialogs);
                if (readError is not null || cancelled)
                {
                    Errors.Add(readError ?? Strings.VaultNotUnlocked);
                    return false;
                }

                secret = stored;
            }

            if (secret is not null && await _passphrases.SetAsync(repository, mode, secret, Dialogs) is { } error)
            {
                Errors.Add(error);
                return false;
            }
        }

        if (mode != oldMode && _passphrases.Delete(existing, oldMode) is { } deleteError)
        {
            Errors.Add(deleteError);
            return false;
        }

        return true;
    }

    /// <summary>Opens the vault (asking for the PIN or to set one up); <c>false</c> with the reason in <see cref="Errors"/>.</summary>
    private async Task<bool> UnlockVaultAsync()
    {
        try
        {
            if (await VaultAccess.EnsureUnlockedAsync(_services.Vault, Dialogs))
                return true;
            Errors.Add(Strings.VaultNotUnlocked);
        }
        catch (SecretBoxException exception)
        {
            Errors.Add(BorgTexts.Format(Strings.VaultError, exception.Message));
        }

        return false;
    }

    private void CreateFields(IRepositoryProvider provider, IReadOnlyDictionary<string, string>? values) =>
        Fields = provider.Fields
            .Select(field => new FieldViewModel(field, values?.GetValueOrDefault(field.Key)))
            .ToList();

    private void ShowErrors(IEnumerable<string> problems)
    {
        foreach (var problem in problems)
            Errors.Add(problem);
    }

    [GeneratedRegex("[^A-Za-z0-9._-]")]
    private static partial Regex UnsafeCommentCharacters();

    /// <summary>How borg will log in: nothing for local repositories, a key for SSH ones.</summary>
    /// <param name="NewKey">A key created in this dialog, to be saved with the repository.</param>
    private sealed record SshLogin(SshAccess? Access, Guid? KeyId, bool Restricted, SshKeyEntry? NewKey)
    {
        public static SshLogin None { get; } = new(null, null, false, null);
    }

    /// <summary>A key installed on a server, and what it was installed for.</summary>
    private sealed record InstalledKey(SshEndpoint Endpoint, string RemoteBorg, SshKeyEntry Key, bool Restricted)
    {
        /// <summary>Whether the key works for <paramref name="location"/> as is: same server, account and restriction.</summary>
        public bool Fits(RepositoryLocation location, bool restricted) =>
            location.Ssh is { } endpoint
            && endpoint.Host == Endpoint.Host && endpoint.Port == Endpoint.Port && endpoint.User == Endpoint.User
            && restricted == Restricted
            && (!restricted || (endpoint.RepositoryPath == Endpoint.RepositoryPath && SshTrustDialog.RemoteBorg(location) == RemoteBorg));
    }
}

/// <summary>A key in the editor's key list.</summary>
public sealed record SshKeyOption(SshKeyEntry Key)
{
    public override string ToString() => $"{Key.Name}  ·  {Key.Type}  ·  {Key.Fingerprint}";
}

public sealed class ProviderOptionViewModel(IRepositoryProvider provider)
{
    public IRepositoryProvider Provider { get; } = provider;

    public string DisplayName => Provider.DisplayName;

    public string Description => Provider.Description;
}

/// <summary>One input of the selected provider.</summary>
public sealed partial class FieldViewModel(ProviderField providerField, string? value) : ObservableObject
{
    public string Key => providerField.Key;

    public string Label => providerField.Required ? providerField.Label : $"{providerField.Label} ({Strings.Optional})";

    public string? Hint => providerField.Hint;

    public bool HasHint => !string.IsNullOrEmpty(providerField.Hint);

    public bool IsFolder => providerField.Kind == ProviderFieldKind.FolderPath;

    public bool IsChoice => providerField.Kind == ProviderFieldKind.Choice;

    public bool IsTextInput => !IsChoice;

    public IReadOnlyList<Option<string>> Options { get; } =
        providerField.Options.Select(option => new Option<string>(option.Value, option.Label)).ToList();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedOption))]
    public partial string Value { get; set; } = value ?? providerField.DefaultValue
        ?? (providerField.Kind == ProviderFieldKind.Choice ? providerField.Options.FirstOrDefault()?.Value : null) ?? "";

    /// <summary>The option matching <see cref="Value"/> (choice fields).</summary>
    public Option<string>? SelectedOption
    {
        get => Options.FirstOrDefault(option => option.Value == Value);
        set
        {
            if (value is not null)
                Value = value.Value;
        }
    }
}
