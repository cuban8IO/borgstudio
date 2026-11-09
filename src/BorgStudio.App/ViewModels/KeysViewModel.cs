using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using BorgStudio.App.Resources;
using BorgStudio.App.Services;
using BorgStudio.Core.Security;
using BorgStudio.Core.Ssh;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BorgStudio.App.ViewModels;

/// <summary>The SSH keys window: all keys in the vault, which repositories use them, and what can be done with them.</summary>
public sealed partial class KeysViewModel : ViewModelBase
{
    private readonly MainViewModel _main;
    private readonly AppServices _services;

    public KeysViewModel(MainViewModel main, IDialogService dialogs)
    {
        _main = main;
        _services = main.Services;
        Dialogs = dialogs;
        Refresh();
    }

    /// <summary>Where nested dialogs appear; set by the keys window.</summary>
    public IDialogService Dialogs { get; set; }

    public ObservableCollection<KeyItemViewModel> Keys { get; } = [];

    /// <summary>Keys live in the vault: without a PIN there is none yet.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsVaultOpen))]
    [NotifyCanExecuteChangedFor(nameof(GenerateCommand), nameof(ImportCommand))]
    public partial bool NeedsPin { get; private set; }

    public bool IsVaultOpen => !NeedsPin;

    public bool HasNoKeys => IsVaultOpen && Keys.Count == 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyCanExecuteChangedFor(nameof(RenameCommand), nameof(CopyPublicKeyCommand), nameof(SavePrivateKeyCommand), nameof(DeleteCommand))]
    public partial KeyItemViewModel? SelectedKey { get; set; }

    public bool HasSelection => SelectedKey is not null;

    /// <summary>The name of the selected key, as edited.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RenameCommand))]
    public partial string EditName { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GenerateCommand))]
    public partial string NewKeyName { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    public partial string? Message { get; private set; }

    [ObservableProperty]
    public partial bool MessageIsError { get; private set; }

    public bool HasMessage => Message is not null;

    partial void OnSelectedKeyChanged(KeyItemViewModel? value) => EditName = value?.Name ?? "";

    [RelayCommand]
    private async Task SetUpPinAsync()
    {
        try
        {
            await VaultAccess.EnsureUnlockedAsync(_services.Vault, Dialogs);
        }
        catch (SecretBoxException exception)
        {
            Show(BorgTexts.Format(Strings.VaultError, exception.Message), isError: true);
        }
        Refresh();
    }

    [RelayCommand(CanExecute = nameof(CanGenerate))]
    private void Generate()
    {
        var comment = $"borgstudio-{Environment.MachineName}";
        var key = SshKeyEntry.Generate(NewKeyName.Trim(), new string(comment.Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' ? c : '_').ToArray()),
            DateTimeOffset.Now);
        _services.Vault.SetKey(key);
        NewKeyName = "";
        Refresh(select: key.Id);
        Show(BorgTexts.Format(Strings.KeysGenerated, key.Name), isError: false);
    }

    private bool CanGenerate() => IsVaultOpen && NewKeyName.Trim().Length > 0;

    [RelayCommand(CanExecute = nameof(IsVaultOpen))]
    private async Task ImportAsync()
    {
        if (await Dialogs.PickOpenFileAsync(Strings.KeysImportTitle) is not { } file)
            return;

        var (key, error) = await SshKeyImport.ImportAsync(_services, Dialogs, file);
        Refresh(select: key?.Id);
        if (error is not null)
            Show(error, isError: key is null);
        else if (key is not null)
            Show(BorgTexts.Format(Strings.KeysImported, key.Name), isError: false);
    }

    [RelayCommand(CanExecute = nameof(CanRename))]
    private void Rename()
    {
        var key = SelectedKey!.Key with { Name = EditName.Trim() };
        _services.Vault.SetKey(key);
        Refresh(select: key.Id);
    }

    private bool CanRename() => SelectedKey is not null && EditName.Trim().Length > 0 && EditName.Trim() != SelectedKey.Name;

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task CopyPublicKeyAsync()
    {
        await Dialogs.CopyToClipboardAsync(SelectedKey!.Key.PublicKey);
        Show(Strings.KeysPublicKeyCopied, isError: false);
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task SavePrivateKeyAsync()
    {
        var key = SelectedKey!.Key;
        if (!await Dialogs.ConfirmAsync(Strings.KeysSavePrivateTitle, Strings.KeysSavePrivateWarning, Strings.KeysSavePrivateConfirm))
            return;
        if (await Dialogs.PickSaveFileAsync(Strings.KeysSavePrivateTitle, SafeFileName(key.Name), defaultExtension: null) is not { } file)
            return;

        try
        {
            WritePrivate(file, key.PrivateKey);
            Show(BorgTexts.Format(Strings.KeysPrivateKeySaved, file), isError: false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Show(exception.Message, isError: true);
        }
    }

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private async Task DeleteAsync()
    {
        var key = SelectedKey!.Key;
        if (!await Dialogs.ConfirmAsync(Strings.KeysDeleteTitle, BorgTexts.Format(Strings.KeysDeleteMessage, key.Name), Strings.Delete))
            return;
        _services.Vault.RemoveKey(key.Id);
        Refresh();
        Message = null;
    }

    private bool CanDelete() => SelectedKey is { IsUsed: false };

    /// <summary>Reloads the list from the vault (and the key names shown in the main window).</summary>
    public void Refresh(Guid? select = null)
    {
        var selected = select ?? SelectedKey?.Key.Id;
        Keys.Clear();
        NeedsPin = _services.Vault.State != VaultState.Unlocked;
        if (!NeedsPin)
        {
            var repositories = _main.Repositories.Select(item => item.Config).ToList();
            foreach (var key in _services.Vault.Keys)
                Keys.Add(new KeyItemViewModel(key, [.. repositories.Where(repository => repository.SshKeyId == key.Id).Select(repository => repository.Name)]));
        }

        SelectedKey = Keys.FirstOrDefault(item => item.Key.Id == selected) ?? Keys.FirstOrDefault();
        OnPropertyChanged(nameof(HasNoKeys));
        _main.RefreshKeyNames();
    }

    private void Show(string message, bool isError)
    {
        MessageIsError = isError;
        Message = message;
    }

    private static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(name.Select(character => invalid.Contains(character) || character == ' ' ? '_' : character).ToArray());
    }

    /// <summary>Writes a private key readable only by the user (OpenSSH refuses others).</summary>
    private static void WritePrivate(string path, string content)
    {
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using var stream = new FileStream(path, options);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(content.EndsWith('\n') ? content : content + "\n");
    }
}

/// <summary>A key in the keys window.</summary>
public sealed class KeyItemViewModel(SshKeyEntry key, IReadOnlyList<string> usedBy)
{
    public SshKeyEntry Key { get; } = key;

    public string Name => Key.Name;

    public string Type => Key.Type;

    public string Fingerprint => Key.Fingerprint;

    public string PublicKey => Key.PublicKey;

    public string Summary => $"{Key.Type} · {Key.Fingerprint}";

    public string OriginText => BorgTexts.Format(Key.Generated ? Strings.KeysOriginGenerated : Strings.KeysOriginImported,
        Key.Created.ToLocalTime().ToString("g", CultureInfo.CurrentCulture));

    public bool IsUsed => usedBy.Count > 0;

    public string UsedByText => IsUsed ? string.Join(", ", usedBy) : Strings.KeysUnused;
}
