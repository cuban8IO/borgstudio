using System.Collections.ObjectModel;
using BorgStudio.App.Resources;
using BorgStudio.Core.Borg;
using BorgStudio.Core.Plugins;
using BorgStudio.Core.Repositories;
using BorgStudio.Core.Secrets;
using BorgStudio.Plugins;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BorgStudio.App.ViewModels;

public sealed record RepositoryEditorResult(RepositoryConfig Repository, bool Created);

/// <summary>
/// Adds a repository (pick a provider, then connect an existing or create a new one) or edits an existing one.
/// Adding only succeeds once borg could open the repository.
/// </summary>
public sealed partial class RepositoryEditorViewModel : ViewModelBase
{
    private readonly BorgClient _borgClient;
    private readonly ISecretStore _secretStore;
    private readonly BorgInstallation? _borg;
    private readonly RepositoryConfig? _existing;

    /// <summary>Add a repository.</summary>
    public RepositoryEditorViewModel(PluginCatalog plugins, BorgClient borgClient, ISecretStore secretStore, BorgInstallation? borg)
    {
        _borgClient = borgClient;
        _secretStore = secretStore;
        _borg = borg;
        Providers = plugins.RepositoryProviders.Select(provider => new ProviderOptionViewModel(provider)).ToList();
        SelectedProvider = Providers.FirstOrDefault();
        SelectedEncryption = EncryptionOptions[0];
        SelectedBorgVersion = BorgVersionOptions[0];
        StorePassphrase = secretStore.IsAvailable;
        Page = Providers.Count == 1 ? EditorPage.Details : EditorPage.SelectProvider;
        if (Page == EditorPage.Details)
            CreateFields(SelectedProvider!.Provider, values: null);
    }

    /// <summary>Edit <paramref name="existing"/>; its provider must be available.</summary>
    public RepositoryEditorViewModel(
        PluginCatalog plugins, BorgClient borgClient, ISecretStore secretStore, BorgInstallation? borg, RepositoryConfig existing)
        : this(plugins, borgClient, secretStore, borg)
    {
        _existing = existing;
        SelectedProvider = Providers.FirstOrDefault(option => option.Provider.Id == existing.ProviderId)
            ?? throw new InvalidOperationException($"Provider '{existing.ProviderId}' is not available.");
        Name = existing.Name;
        StorePassphrase = existing.PassphraseMode == PassphraseMode.Stored;
        SelectedBorgVersion = BorgVersionOptions.First(option => option.Value == existing.BorgVersion);
        CreateFields(SelectedProvider.Provider, existing.ProviderValues);
        Page = EditorPage.Details;
    }

    public enum EditorPage
    {
        SelectProvider,
        Details,
    }

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

    public bool CanStorePassphrase => _secretStore.IsAvailable;

    public bool KeychainUnavailable => !_secretStore.IsAvailable;

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

        if (_existing is not null)
        {
            SaveChanges(_existing, values);
            return;
        }

        IsBusy = true;
        try
        {
            await AddAsync(provider, values);
        }
        finally
        {
            IsBusy = false;
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

        var passphrase = UsesPassphrase && Passphrase.Length > 0 ? Passphrase : null;
        if (CreateNew)
        {
            BusyText = Strings.Creating;
            var created = await _borgClient.InitAsync(borg, location, SelectedEncryption.Value, passphrase);
            if (!created.Succeeded)
            {
                Errors.Add(BorgTexts.Describe(created.Error!));
                return;
            }
        }

        BusyText = Strings.Checking;
        var info = await _borgClient.InfoAsync(borg, location, passphrase);
        if (!info.Succeeded)
        {
            Errors.Add(BorgTexts.Describe(info.Error!));
            return;
        }

        var encrypted = info.Value!.EncryptionMode != "none";
        var repository = new RepositoryConfig
        {
            Id = Guid.NewGuid(),
            Name = Name.Trim(),
            ProviderId = provider.Id,
            ProviderValues = values,
            PassphraseMode = !encrypted ? PassphraseMode.None
                : StorePassphrase && CanStorePassphrase ? PassphraseMode.Stored
                : PassphraseMode.Ask,
            BorgVersion = borgVersion,
            EncryptionMode = info.Value.EncryptionMode,
            BorgRepositoryId = info.Value.Id,
        };

        if (repository.PassphraseMode == PassphraseMode.Stored && !TryStorePassphrase(repository, Passphrase))
            return;

        Close(new RepositoryEditorResult(repository, CreateNew));
    }

    private void SaveChanges(RepositoryConfig existing, IReadOnlyDictionary<string, string> values)
    {
        var mode = existing.PassphraseMode == PassphraseMode.None ? PassphraseMode.None
            : StorePassphrase && CanStorePassphrase ? PassphraseMode.Stored
            : PassphraseMode.Ask;
        var repository = existing with
        {
            Name = Name.Trim(),
            ProviderValues = values,
            PassphraseMode = mode,
            BorgVersion = SelectedBorgVersion.Value,
        };

        try
        {
            if (mode == PassphraseMode.Stored && Passphrase.Length > 0)
                _secretStore.Set(repository.SecretKey, SecretLabel(repository), Passphrase);
            else if (mode != PassphraseMode.Stored && existing.PassphraseMode == PassphraseMode.Stored)
                _secretStore.Delete(repository.SecretKey);
        }
        catch (SecretStoreException exception)
        {
            Errors.Add(BorgTexts.Format(Strings.KeychainError, exception.Message));
            return;
        }

        Close(new RepositoryEditorResult(repository, Created: false));
    }

    private bool TryStorePassphrase(RepositoryConfig repository, string passphrase)
    {
        try
        {
            _secretStore.Set(repository.SecretKey, SecretLabel(repository), passphrase);
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
