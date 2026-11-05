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
            editor.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(RepositoryEditorViewModel.IsBusy) && !editor.IsBusy && _closeWhenIdle)
                    Close();
            };
        }
    }

    /// <summary>Closing was asked for (e.g. by locking the app) while borg was working: close once it is done.</summary>
    private bool _closeWhenIdle;

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // borg is still working on the repository: don't leave it half-created.
        if (DataContext is RepositoryEditorViewModel { IsBusy: true })
        {
            e.Cancel = true;
            _closeWhenIdle = true;
        }

        base.OnClosing(e);
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
}
