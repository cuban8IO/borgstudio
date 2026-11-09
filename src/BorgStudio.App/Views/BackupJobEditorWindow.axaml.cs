using Avalonia.Controls;
using BorgStudio.App.Services;
using BorgStudio.App.ViewModels;

namespace BorgStudio.App.Views;

public partial class BackupJobEditorWindow : Window
{
    public BackupJobEditorWindow()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is BackupJobEditorViewModel editor)
        {
            // Folder dialogs belong to this window.
            editor.Dialogs = new DialogService(this);
            editor.CloseRequested += (_, _) => Close();
        }
    }
}
