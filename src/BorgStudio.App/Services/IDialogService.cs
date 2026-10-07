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

    /// <summary>Asks where to save a file; <c>null</c> if cancelled.</summary>
    Task<string?> PickSaveFileAsync(string title, string suggestedFileName);

    /// <summary>Shows the add/edit dialog; the outcome is in <see cref="RepositoryEditorViewModel.Result"/>.</summary>
    Task ShowRepositoryEditorAsync(RepositoryEditorViewModel editor);
}
