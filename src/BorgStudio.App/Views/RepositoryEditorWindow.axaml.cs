using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using BorgStudio.App.Resources;
using BorgStudio.App.Services;
using BorgStudio.App.ViewModels;

namespace BorgStudio.App.Views;

public partial class RepositoryEditorWindow : Window
{
    public RepositoryEditorWindow()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is RepositoryEditorViewModel editor)
        {
            // Nested dialogs (host key confirmation) belong to this window, not to the main window.
            editor.Dialogs = new DialogService(this);
            editor.CloseRequested += (_, _) => Close();
        }
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // borg is still working on the repository: don't leave it half-created.
        if (DataContext is RepositoryEditorViewModel { IsBusy: true })
            e.Cancel = true;
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        (DataContext as RepositoryEditorViewModel)?.OnClosed();
        base.OnClosed(e);
    }

    private async void BrowseFolder_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: FieldViewModel field })
            return;

        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = field.Label,
            AllowMultiple = false,
        });
        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path)
            field.Value = path;
    }

    private async void BrowseKeyFile_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not RepositoryEditorViewModel editor)
            return;

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Strings.EditorKeyFile,
            AllowMultiple = false,
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
            editor.ExistingKeyFile = path;
    }
}
