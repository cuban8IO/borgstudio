using BorgStudio.App.ViewModels;

namespace BorgStudio.App.Services;

/// <summary>Dialogs the view models need; implemented by the view layer so view models stay testable.</summary>
public interface IDialogService
{
    /// <summary>Asks for confirmation; <c>true</c> if the user chose <paramref name="confirmText"/>.</summary>
    /// <param name="cancelText">Label of the other button; "Cancel" if not given.</param>
    Task<bool> ConfirmAsync(string title, string message, string confirmText, string? cancelText = null);

    Task ShowMessageAsync(string title, string message);

    /// <summary>Asks for a repository passphrase; <c>null</c> if cancelled.</summary>
    Task<string?> AskPassphraseAsync(string repositoryName);

    /// <summary>Asks for the vault PIN; <c>null</c> if cancelled. <paramref name="error"/> explains a previous attempt.</summary>
    Task<string?> AskPinAsync(string? error = null);

    /// <summary>Asks for a new vault PIN (entered twice, at least <see cref="Core.Security.Vault.MinimumPinLength"/> characters); <c>null</c> if cancelled.</summary>
    Task<string?> AskNewPinAsync();

    /// <summary>Asks for some other secret (e.g. the password of a key file); <c>null</c> if cancelled.</summary>
    Task<string?> AskSecretAsync(string title, string prompt, string? error = null);

    /// <summary>Asks where to save a file; <c>null</c> if cancelled.</summary>
    /// <param name="defaultExtension">Added if the name has none; <c>null</c> for files without extension.</param>
    Task<string?> PickSaveFileAsync(string title, string suggestedFileName, string? defaultExtension = "txt");

    /// <summary>Asks for a folder; <c>null</c> if cancelled.</summary>
    Task<string?> PickFolderAsync(string title);

    /// <summary>Asks for a file to open; <c>null</c> if cancelled.</summary>
    Task<string?> PickOpenFileAsync(string title);

    Task CopyToClipboardAsync(string text);

    /// <summary>Shows the add/edit dialog; the outcome is in <see cref="RepositoryEditorViewModel.Result"/>.</summary>
    Task ShowRepositoryEditorAsync(RepositoryEditorViewModel editor);

    /// <summary>Shows the backup job editor; the outcome is in <see cref="BackupJobEditorViewModel.Result"/>.</summary>
    Task ShowBackupJobEditorAsync(BackupJobEditorViewModel editor);

    /// <summary>Shows which archives a clean-up would delete; <c>true</c> if the user confirms deleting them.</summary>
    Task<bool> ConfirmPruneAsync(PrunePreviewViewModel preview);

    /// <summary>Shows the contents of an archive until the user closes it.</summary>
    Task ShowArchiveBrowserAsync(ArchiveBrowserViewModel browser);

    /// <summary>Shows the restore dialog until the user closes it.</summary>
    Task ShowRestoreAsync(RestoreViewModel restore);
}
