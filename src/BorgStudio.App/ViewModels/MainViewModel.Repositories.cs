using System.Collections.ObjectModel;
using System.Globalization;
using BorgStudio.App.Resources;
using BorgStudio.Core.Borg;
using BorgStudio.Core.Repositories;
using BorgStudio.Core.Secrets;
using BorgStudio.Plugins;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BorgStudio.App.ViewModels;

/// <summary>The repository list and what can be done with a repository.</summary>
public partial class MainViewModel
{
    public ObservableCollection<RepositoryItemViewModel> Repositories { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyCanExecuteChangedFor(nameof(TestRepositoryCommand), nameof(ExportKeyCommand), nameof(EditRepositoryCommand),
        nameof(RemoveRepositoryCommand))]
    public partial RepositoryItemViewModel? SelectedRepository { get; set; }

    public bool HasRepositories => Repositories.Count > 0;

    public bool HasSelection => SelectedRepository is not null;

    /// <summary>Shown when repositories.json could not be read.</summary>
    [ObservableProperty]
    public partial string? StoreWarning { get; private set; }

    /// <summary>A borg command is running for the selected repository.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddRepositoryCommand), nameof(TestRepositoryCommand), nameof(ExportKeyCommand),
        nameof(EditRepositoryCommand), nameof(RemoveRepositoryCommand))]
    public partial bool IsWorking { get; private set; }

    [ObservableProperty]
    public partial string? WorkingText { get; private set; }

    private void LoadRepositories()
    {
        var result = _services.RepositoryStore.Load();
        foreach (var repository in result.Repositories)
            Repositories.Add(new RepositoryItemViewModel(repository, Plugins));
        if (result.BrokenFileBackup is not null)
            StoreWarning = BorgTexts.Format(Strings.StoreBrokenWarning, result.BrokenFileBackup);

        Repositories.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasRepositories));
        SelectedRepository = Repositories.FirstOrDefault();
    }

    private void SaveRepositories() => _services.RepositoryStore.Save(Repositories.Select(item => item.Config));

    // --- Add / edit / remove ---

    [RelayCommand(CanExecute = nameof(CanAdd))]
    private async Task AddRepositoryAsync()
    {
        var editor = new RepositoryEditorViewModel(Plugins, _services.BorgClient, _services.SecretStore, Borg);
        await _services.Dialogs.ShowRepositoryEditorAsync(editor);
        if (editor.Result is not { } result)
            return;

        var item = new RepositoryItemViewModel(result.Repository, Plugins);
        Repositories.Add(item);
        SaveRepositories();
        SelectedRepository = item;
        item.ShowResult(result.Created ? Strings.RepositoryCreated : Strings.RepositoryConnected, isError: false);

        if (result.Created && item.IsEncrypted
            && await _services.Dialogs.ConfirmAsync(Strings.BackupKeyTitle,
                item.IsKeyfile ? Strings.BackupKeyKeyfile : Strings.BackupKeyRepokey,
                Strings.BackupKeyConfirm, Strings.Later))
            await ExportKeyAsync();
    }

    private bool CanAdd() => !IsWorking && Plugins.RepositoryProviders.Count > 0;

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private async Task EditRepositoryAsync()
    {
        var item = SelectedRepository!;
        var editor = new RepositoryEditorViewModel(Plugins, _services.BorgClient, _services.SecretStore, Borg, item.Config);
        await _services.Dialogs.ShowRepositoryEditorAsync(editor);
        if (editor.Result is not { } result)
            return;

        item.Config = result.Repository;
        SaveRepositories();
    }

    private bool CanEdit() => !IsWorking && SelectedRepository?.Provider is not null;

    [RelayCommand(CanExecute = nameof(CanRemove))]
    private async Task RemoveRepositoryAsync()
    {
        var item = SelectedRepository!;
        if (!await _services.Dialogs.ConfirmAsync(Strings.RemoveTitle,
                BorgTexts.Format(Strings.RemoveMessage, item.Name), Strings.Remove))
            return;

        if (item.Config.PassphraseMode == PassphraseMode.Stored)
        {
            try
            {
                _services.SecretStore.Delete(item.Config.SecretKey);
            }
            catch (SecretStoreException exception)
            {
                await _services.Dialogs.ShowMessageAsync(Strings.RemoveTitle,
                    BorgTexts.Format(Strings.KeychainError, exception.Message));
            }
        }

        var index = Repositories.IndexOf(item);
        Repositories.Remove(item);
        SaveRepositories();
        SelectedRepository = Repositories.Count == 0 ? null : Repositories[Math.Min(index, Repositories.Count - 1)];
    }

    private bool CanRemove() => !IsWorking && SelectedRepository is not null;

    // --- borg operations ---

    [RelayCommand(CanExecute = nameof(CanRunBorg))]
    private async Task TestRepositoryAsync()
    {
        var item = SelectedRepository!;
        if (!TryGetLocation(item, out var location))
            return;
        if (await GetPassphraseAsync(item) is not { } passphrase)
            return;

        var result = await RunAsync(Strings.Checking,
            () => _services.BorgClient.InfoAsync(Borg!, location, passphrase.Value));
        if (!result.Succeeded)
        {
            item.ShowResult(BorgTexts.Describe(result.Error!), isError: true);
            return;
        }

        var info = result.Value!;
        var text = info.LastModified is { } modified
            ? BorgTexts.Format(Strings.TestSucceeded, modified.ToLocalTime().ToString("g", CultureInfo.CurrentCulture))
            : Strings.TestSucceededNoDate;
        var otherRepository = item.Config.BorgRepositoryId is { } knownId && knownId != info.Id;
        item.ShowResult(otherRepository ? $"{text} {Strings.RepositoryIdChanged}" : text, isError: otherRepository);

        // Fill in what wasn't known yet (e.g. repositories connected before the first successful test).
        if (item.Config.EncryptionMode is null || item.Config.BorgRepositoryId is null)
        {
            item.Config = item.Config with
            {
                EncryptionMode = item.Config.EncryptionMode ?? info.EncryptionMode,
                BorgRepositoryId = item.Config.BorgRepositoryId ?? info.Id,
            };
            SaveRepositories();
        }
    }

    [RelayCommand(CanExecute = nameof(CanExportKey))]
    private async Task ExportKeyAsync()
    {
        var item = SelectedRepository!;
        if (!TryGetLocation(item, out var location))
            return;

        var target = await _services.Dialogs.PickSaveFileAsync(Strings.ExportKeyTitle, $"{SafeFileName(item.Name)}-borg-key.txt");
        if (target is null)
            return;

        var result = await RunAsync(Strings.ExportingKey,
            () => _services.BorgClient.ExportKeyAsync(Borg!, location, target));
        item.ShowResult(
            result.Succeeded ? BorgTexts.Format(Strings.KeyExported, target) : BorgTexts.Describe(result.Error!),
            isError: !result.Succeeded);
    }

    private bool CanRunBorg() =>
        !IsWorking && SelectedRepository?.Location is not null && Borg is { Support: BorgSupport.Supported };

    private bool CanExportKey() => CanRunBorg() && SelectedRepository!.IsEncrypted;

    /// <summary>The location, if borg 1 can handle it; otherwise the reason is shown on the item.</summary>
    private static bool TryGetLocation(RepositoryItemViewModel item, out RepositoryLocation location)
    {
        location = item.Location!;
        if (RepositoryFormat.Resolve(item.Config.BorgVersion, location) != 2)
            return true;

        item.ShowResult(Strings.Borg2NotSupported, isError: true);
        return false;
    }

    /// <summary>The passphrase to use (<c>Value</c> is <c>null</c> for unencrypted repositories); <c>null</c> if cancelled.</summary>
    private async Task<PassphraseHolder?> GetPassphraseAsync(RepositoryItemViewModel item)
    {
        switch (item.Config.PassphraseMode)
        {
            case PassphraseMode.None:
                return new PassphraseHolder(null);
            case PassphraseMode.Stored:
                try
                {
                    if (_services.SecretStore.Get(item.Config.SecretKey) is { } stored)
                        return new PassphraseHolder(stored);
                }
                catch (SecretStoreException exception)
                {
                    item.ShowResult(BorgTexts.Format(Strings.KeychainError, exception.Message), isError: true);
                }
                break;
        }

        // Asked for every time – or stored but missing from the keychain.
        var entered = await _services.Dialogs.AskPassphraseAsync(item.Name);
        return entered is null ? null : new PassphraseHolder(entered);
    }

    private async Task<BorgResult<T>> RunAsync<T>(string workingText, Func<Task<BorgResult<T>>> operation)
    {
        IsWorking = true;
        WorkingText = workingText;
        try
        {
            return await Task.Run(operation);
        }
        finally
        {
            IsWorking = false;
            WorkingText = null;
        }
    }

    private static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(name.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
    }

    private sealed record PassphraseHolder(string? Value);
}
