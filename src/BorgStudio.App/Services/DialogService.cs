using Avalonia.Controls;
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

    public async Task<string?> PickSaveFileAsync(string title, string suggestedFileName)
    {
        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedFileName,
            DefaultExtension = "txt",
            ShowOverwritePrompt = true,
        });
        return file?.TryGetLocalPath();
    }

    public Task ShowRepositoryEditorAsync(RepositoryEditorViewModel editor) =>
        new RepositoryEditorWindow { DataContext = editor }.ShowDialog(owner);
}
