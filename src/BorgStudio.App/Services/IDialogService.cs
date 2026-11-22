using BorgStudio.App.ViewModels;

namespace BorgStudio.App.Services;

/// <summary>Dialogs the view models need; implemented by the view layer so view models stay testable.</summary>
public interface IDialogService
{
    /// <summary>Asks for confirmation; <c>true</c> if the user chose <paramref name="confirmText"/>.</summary>
    /// <param name="cancelText">Label of the other button; "Cancel" if not given.</param>
    Task<bool> ConfirmAsync(string title, string message, string confirmText, string? cancelText = null);

    /// <param name="buttonText">The button's text; "OK" if not given.</param>
    Task ShowMessageAsync(string title, string message, string? buttonText = null);

    /// <summary>Asks for a repository passphrase; <c>null</c> if cancelled.</summary>
    Task<string?> AskPassphraseAsync(string repositoryName);

    /// <summary>Asks for the vault PIN; <c>null</c> if cancelled. <paramref name="error"/> explains a previous attempt.</summary>
    Task<string?> AskPinAsync(string? error = null);

    /// <summary>Asks for a new vault PIN (entered twice, at least <see cref="Core.Security.Vault.MinimumPinLength"/> characters); <c>null</c> if cancelled.</summary>
    Task<string?> AskNewPinAsync();

    /// <summary>Asks for some other secret (e.g. the password of a key file); <c>null</c> if cancelled.</summary>
    Task<string?> AskSecretAsync(string title, string prompt, string? error = null);

    /// <summary>Asks for a line of text (a name, a comment), filled in with <paramref name="initial"/>; <c>null</c> if cancelled.</summary>
    Task<string?> AskTextAsync(string title, string prompt, string initial);

    /// <summary>Asks where to save a file; <c>null</c> if cancelled.</summary>
    /// <param name="defaultExtension">Added if the name has none; <c>null</c> for files without extension.</param>
    Task<string?> PickSaveFileAsync(string title, string suggestedFileName, string? defaultExtension = "txt");

    /// <summary>Asks for a folder; <c>null</c> if cancelled.</summary>
    Task<string?> PickFolderAsync(string title);

    /// <summary>Asks for a file to open; <c>null</c> if cancelled.</summary>
    Task<string?> PickOpenFileAsync(string title);

    Task CopyToClipboardAsync(string text);

    /// <summary>Opens a folder in the file manager (Explorer, Finder, …).</summary>
    Task OpenFolderAsync(string path);

    /// <summary>
    /// Closes the app like the user would (stopping backups and mounts first) and starts it again – or
    /// <paramref name="instead"/> (the installer of an update).
    /// </summary>
    void RestartApp(System.Diagnostics.ProcessStartInfo? instead = null);

    /// <summary>Shows the update window (check, release notes, download and install).</summary>
    Task ShowUpdateAsync(UpdateViewModel update);

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

    /// <summary>Shows the search over all archives until the user closes it.</summary>
    Task ShowArchiveSearchAsync(ArchiveSearchViewModel search);

    /// <summary>Shows the versions of a file until the user closes it (looking through the archives starts with it).</summary>
    Task ShowFileVersionsAsync(FileVersionsViewModel versions);

    /// <summary>Where to export an archive to: the file type chosen (zip, tar.gz, tar.zst, tar) gives the extension.</summary>
    Task<string?> PickArchiveExportFileAsync(string title, string suggestedName);

    /// <summary>An archive exported; the export starts with the window.</summary>
    Task ShowArchiveExportAsync(ArchiveExportViewModel export);

    /// <summary>Two archives compared; returns when the window is closed.</summary>
    Task ShowArchiveDiffAsync(ArchiveDiffViewModel diff);
}
