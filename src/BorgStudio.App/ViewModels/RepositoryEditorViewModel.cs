using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using BorgStudio.App.Resources;
using BorgStudio.App.Services;
using BorgStudio.Core.Borg;
using BorgStudio.Core.Repositories;
using BorgStudio.Core.Secrets;
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

    /// <summary>Use a key file the user already has.</summary>
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

    /// <summary>A key created in this dialog: reused for retries, deleted if the dialog is cancelled.</summary>
    private (SshEndpoint Endpoint, SshGeneratedKey Key, bool Restricted)? _newKey;

    /// <summary>Add a repository, or edit <paramref name="existing"/> (its provider must be available).</summary>
    public RepositoryEditorViewModel(AppServices services, BorgInstallation? borg, RepositoryConfig? existing = null)
    {
        _services = services;
        _borg = borg;
        _existing = existing;
        _id = existing?.Id ?? Guid.NewGuid();
        Dialogs = services.Dialogs;
        Providers = services.Plugins.RepositoryProviders.Select(provider => new ProviderOptionViewModel(provider)).ToList();
        SelectedEncryption = EncryptionOptions[0];
        SelectedBorgVersion = BorgVersionOptions[0];

        if (existing is null)
        {
            SelectedProvider = Providers.FirstOrDefault();
            StorePassphrase = services.SecretStore.IsAvailable;
            KeyChoice = SshKeyChoice.Generate;
            Page = Providers.Count == 1 ? EditorPage.Details : EditorPage.SelectProvider;
            if (Page == EditorPage.Details)
                CreateFields(SelectedProvider!.Provider, values: null);
            return;
        }

        SelectedProvider = Providers.FirstOrDefault(option => option.Provider.Id == existing.ProviderId)
            ?? throw new InvalidOperationException($"Provider '{existing.ProviderId}' is not available.");
        Name = existing.Name;
        StorePassphrase = existing.PassphraseMode == PassphraseMode.Stored;
        SelectedBorgVersion = BorgVersionOptions.First(option => option.Value == existing.BorgVersion);
        KeyChoice = existing.SshKeyFile is null ? SshKeyChoice.Generate : SshKeyChoice.Keep;
        RestrictKey = existing.SshKeyFile is null || existing.SshKeyRestricted;
        CreateFields(SelectedProvider.Provider, existing.ProviderValues);
        Page = EditorPage.Details;
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

    [ObservableProperty]
    public partial bool StorePassphrase { get; set; }

    public bool CanStorePassphrase => _services.SecretStore.IsAvailable;

    public bool KeychainUnavailable => !_services.SecretStore.IsAvailable;

    // --- SSH login ---

    public bool IsSsh => SelectedProvider?.Provider.UsesSsh == true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KeyChoiceKeep), nameof(KeyChoiceGenerate), nameof(KeyChoiceExisting))]
    public partial SshKeyChoice KeyChoice { get; set; }

    public bool ShowKeepKeyOption => _existing?.SshKeyFile is not null;

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

    /// <summary>Used once to install the generated key; never stored.</summary>
    [ObservableProperty]
    public partial string ServerPassword { get; set; } = "";

    [ObservableProperty]
    public partial bool RestrictKey { get; set; } = true;

    [ObservableProperty]
    public partial string ExistingKeyFile { get; set; } = "";

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

    [ObservableProperty]
    public partial string? BusyText { get; set; }

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

    /// <summary>Called when the dialog is gone: a key created for a repository that was never saved is deleted.</summary>
    public void OnClosed()
    {
        if (Result is null && _newKey is { } created)
            _services.SshKeys.Delete(created.Key.PrivateKeyFile);
    }

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
            IsBusy = false;
            BusyText = null;
        }
    }

    private bool IsIdle() => !IsBusy;

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
        if (_existing is { PassphraseMode: PassphraseMode.Ask } && StorePassphrase && Passphrase.Length == 0)
            problems.Add(Strings.EditorPassphraseRequiredToStore);

        if (IsSsh)
        {
            if (KeyChoice == SshKeyChoice.Generate && ServerPassword.Length == 0 && !HasInstalledKeyFor(provider, values))
                problems.Add(Strings.EditorServerPasswordRequired);
            if (KeyChoice == SshKeyChoice.Existing && string.IsNullOrWhiteSpace(ExistingKeyFile))
                problems.Add(Strings.EditorKeyFileRequired);
            else if (KeyChoice == SshKeyChoice.Existing && !File.Exists(ExistingKeyFile.Trim()))
                problems.Add(Strings.EditorKeyFileMissing);
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

        var login = IsSsh ? await PrepareSshLoginAsync(location) : SshLogin.None;
        if (login is null)
            return;

        var passphrase = UsesPassphrase && Passphrase.Length > 0 ? Passphrase : null;
        if (CreateNew)
        {
            BusyText = Strings.Creating;
            var created = await _services.BorgClient.InitAsync(borg, location, SelectedEncryption.Value, passphrase, login.Access);
            if (!created.Succeeded)
            {
                Errors.Add(BorgTexts.Describe(created.Error!));
                return;
            }
        }

        BusyText = Strings.Checking;
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
            PassphraseMode = !encrypted ? PassphraseMode.None
                : StorePassphrase && CanStorePassphrase ? PassphraseMode.Stored
                : PassphraseMode.Ask,
            BorgVersion = borgVersion,
            EncryptionMode = info.Value.EncryptionMode,
            BorgRepositoryId = info.Value.Id,
            SshKeyFile = login.Access?.PrivateKeyFile,
            SshKeyManaged = login.Managed,
            SshKeyRestricted = login.Restricted,
        };

        if (repository.PassphraseMode == PassphraseMode.Stored && !TryStorePassphrase(repository, Passphrase))
            return;

        Close(new RepositoryEditorResult(repository, CreateNew));
    }

    private async Task SaveChangesAsync(RepositoryConfig existing, IRepositoryProvider provider, IReadOnlyDictionary<string, string> values)
    {
        var repository = existing with
        {
            Name = Name.Trim(),
            ProviderValues = values,
            BorgVersion = SelectedBorgVersion.Value,
        };

        if (IsSsh && KeyChoice != SshKeyChoice.Keep)
        {
            var login = await PrepareSshLoginAsync(provider.GetLocation(values));
            if (login is null)
                return;
            repository = repository with
            {
                SshKeyFile = login.Access!.PrivateKeyFile,
                SshKeyManaged = login.Managed,
                SshKeyRestricted = login.Restricted,
            };
        }

        var mode = existing.PassphraseMode == PassphraseMode.None ? PassphraseMode.None
            : StorePassphrase && CanStorePassphrase ? PassphraseMode.Stored
            : PassphraseMode.Ask;
        repository = repository with { PassphraseMode = mode };

        try
        {
            if (mode == PassphraseMode.Stored && Passphrase.Length > 0)
                _services.SecretStore.Set(repository.SecretKey, SecretLabel(repository), Passphrase);
            else if (mode != PassphraseMode.Stored && existing.PassphraseMode == PassphraseMode.Stored)
                _services.SecretStore.Delete(repository.SecretKey);
        }
        catch (SecretStoreException exception)
        {
            Errors.Add(BorgTexts.Format(Strings.KeychainError, exception.Message));
            return;
        }

        // The old key is no longer used once a new one is in place.
        if (existing is { SshKeyManaged: true, SshKeyFile: { } oldKey } && repository.SshKeyFile != oldKey)
            _services.SshKeys.Delete(oldKey);

        Close(new RepositoryEditorResult(repository, Created: false));
    }

    /// <summary>
    /// Confirms the server's host key and sets up the login key as chosen; <c>null</c> if that failed
    /// (the reason is in <see cref="Errors"/>).
    /// </summary>
    private async Task<SshLogin?> PrepareSshLoginAsync(RepositoryLocation location)
    {
        var endpoint = location.Ssh ?? throw new InvalidOperationException("SSH provider without SSH endpoint.");
        var knownHosts = _services.SshKeys.KnownHostsFile;

        BusyText = Strings.ContactingServer;
        var (hostKey, problem) = await SshTrustDialog.EnsureTrustedAsync(_services.SshTrust, endpoint, Dialogs);
        if (hostKey is null)
        {
            Errors.Add(problem!);
            return null;
        }

        if (KeyChoice == SshKeyChoice.Existing)
            return new SshLogin(new SshAccess(ExistingKeyFile.Trim(), knownHosts), Managed: false, Restricted: false);

        if (_newKey is { } created && created.Endpoint == endpoint && created.Restricted == RestrictKey)
            return new SshLogin(new SshAccess(created.Key.PrivateKeyFile, knownHosts), Managed: true, created.Restricted);

        // A new key per attempt with different settings; the previous one was never used.
        if (_newKey is { } previous)
        {
            _services.SshKeys.Delete(previous.Key.PrivateKeyFile);
            _newKey = null;
        }

        var key = _services.SshKeys.Create(KeyFileName(), KeyComment());
        var line = AuthorizedKeys.Line(key.PublicKeyLine,
            RestrictKey ? endpoint.RepositoryPath : null, SshTrustDialog.RemoteBorg(location));

        BusyText = Strings.InstallingKey;
        try
        {
            await _services.Ssh.RunWithPasswordAsync(endpoint, ServerPassword, hostKey, AuthorizedKeys.AppendCommand(line));
        }
        catch (SshOperationException exception)
        {
            _services.SshKeys.Delete(key.PrivateKeyFile);
            Errors.Add(SshTrustDialog.Describe(exception));
            return null;
        }

        _newKey = (endpoint, key, RestrictKey);
        return new SshLogin(new SshAccess(key.PrivateKeyFile, knownHosts), Managed: true, RestrictKey);
    }

    private bool HasInstalledKeyFor(IRepositoryProvider provider, IReadOnlyDictionary<string, string> values) =>
        _newKey is { } created && created.Restricted == RestrictKey && provider.GetLocation(values).Ssh == created.Endpoint;

    // Unique per key: editing may replace a repository's key while the old one is still in use.
    private string KeyFileName() => $"{_id:N}-{DateTime.UtcNow:yyyyMMddHHmmss}";

    private string KeyComment() =>
        $"borgstudio-{UnsafeCommentCharacters().Replace(Environment.MachineName, "_")}-{_id.ToString("N")[..8]}";

    private bool TryStorePassphrase(RepositoryConfig repository, string passphrase)
    {
        try
        {
            _services.SecretStore.Set(repository.SecretKey, SecretLabel(repository), passphrase);
            return true;
        }
        catch (SecretStoreException exception)
        {
            Errors.Add(BorgTexts.Format(Strings.KeychainError, exception.Message));
            return false;
        }
    }

    private static string SecretLabel(RepositoryConfig repository) => BorgTexts.Format(Strings.SecretLabel, repository.Name);

    private void CreateFields(IRepositoryProvider provider, IReadOnlyDictionary<string, string>? values) =>
        Fields = provider.Fields
            .Select(field => new FieldViewModel(field, values?.GetValueOrDefault(field.Key)))
            .ToList();

    private void ShowErrors(IEnumerable<string> problems)
    {
        foreach (var problem in problems)
            Errors.Add(problem);
    }

    private void Close(RepositoryEditorResult result)
    {
        Result = result;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [GeneratedRegex("[^A-Za-z0-9._-]")]
    private static partial Regex UnsafeCommentCharacters();

    /// <summary>How borg will log in: nothing for local repositories, a key for SSH ones.</summary>
    private sealed record SshLogin(SshAccess? Access, bool Managed, bool Restricted)
    {
        public static SshLogin None { get; } = new(null, false, false);
    }
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

    [ObservableProperty]
    public partial string Value { get; set; } = value ?? providerField.DefaultValue ?? "";
}
