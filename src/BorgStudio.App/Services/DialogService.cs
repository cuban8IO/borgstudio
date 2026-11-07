using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using BorgStudio.App.Resources;
using BorgStudio.App.ViewModels;
using BorgStudio.App.Views;

namespace BorgStudio.App.Services;

/// <summary>Dialogs shown modally over <paramref name="owner"/>.</summary>
public sealed class DialogService(Window owner) : IDialogService
{
    public Task<bool> ConfirmAsync(string title, string message, string confirmText, string? cancelText = null) =>
        new MessageWindow(title, message, confirmText, cancelText ?? Strings.Cancel).ShowDialog<bool>(owner);

    public Task ShowMessageAsync(string title, string message) =>
        new MessageWindow(title, message, Strings.Ok, cancelText: null).ShowDialog<bool>(owner);

    public Task<string?> AskPassphraseAsync(string repositoryName) =>
        new PassphraseWindow(repositoryName).ShowDialog<string?>(owner);

    public Task<string?> AskPinAsync(string? error = null) =>
        new PinWindow(PinWindow.Mode.Unlock, error).ShowDialog<string?>(owner);

    public Task<string?> AskNewPinAsync() =>
        new PinWindow(PinWindow.Mode.Create).ShowDialog<string?>(owner);

    public Task<string?> AskSecretAsync(string title, string prompt, string? error = null) =>
        new PassphraseWindow(title, prompt, error).ShowDialog<string?>(owner);

    public async Task<string?> PickSaveFileAsync(string title, string suggestedFileName, string? defaultExtension = "txt")
    {
        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedFileName,
            DefaultExtension = defaultExtension,
            ShowOverwritePrompt = true,
        });
        return file?.TryGetLocalPath();
    }

    public async Task<string?> PickFolderAsync(string title)
    {
        var folders = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickOpenFileAsync(string title)
    {
        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = title, AllowMultiple = false });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public async Task CopyToClipboardAsync(string text)
    {
        if (owner.Clipboard is { } clipboard)
            await clipboard.SetTextAsync(text);
    }

    public Task ShowRepositoryEditorAsync(RepositoryEditorViewModel editor) =>
        new RepositoryEditorWindow { DataContext = editor }.ShowDialog(owner);

    public Task ShowBackupJobEditorAsync(BackupJobEditorViewModel editor) =>
        new BackupJobEditorWindow { DataContext = editor }.ShowDialog(owner);

    public Task<bool> ConfirmPruneAsync(PrunePreviewViewModel preview) =>
        new PrunePreviewWindow { DataContext = preview }.ShowDialog<bool>(owner);

    public Task ShowArchiveBrowserAsync(ArchiveBrowserViewModel browser) =>
        new ArchiveBrowserWindow { DataContext = browser }.ShowDialog(owner);

    public Task ShowRestoreAsync(RestoreViewModel restore) =>
        new RestoreWindow { DataContext = restore }.ShowDialog(owner);

    public Task ShowArchiveSearchAsync(ArchiveSearchViewModel search) =>
        new ArchiveSearchWindow { DataContext = search }.ShowDialog(owner);

    public Task ShowFileVersionsAsync(FileVersionsViewModel versions) =>
        new FileVersionsWindow { DataContext = versions }.ShowDialog(owner);
}
