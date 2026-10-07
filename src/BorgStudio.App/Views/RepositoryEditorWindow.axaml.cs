using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
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
            editor.CloseRequested += (_, _) => Close();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // borg is still working on the repository: don't leave it half-created.
        if (DataContext is RepositoryEditorViewModel { IsBusy: true })
            e.Cancel = true;
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
