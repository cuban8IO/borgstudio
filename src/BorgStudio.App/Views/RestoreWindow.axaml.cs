using Avalonia.Controls;
using Avalonia.Interactivity;
using BorgStudio.App.Services;
using BorgStudio.App.ViewModels;

namespace BorgStudio.App.Views;

/// <summary>Restore: target and structure, then progress, then the outcome.</summary>
public partial class RestoreWindow : Window
{
    public RestoreWindow()
    {
        InitializeComponent();
    }

    private RestoreViewModel? Restore => DataContext as RestoreViewModel;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (Restore is { } restore)
        {
            // Folder picker and questions belong to this window.
            restore.Dialogs = new DialogService(this);
            restore.CloseRequested += (_, _) => Close();
        }
    }

    // Closed while borg runs (e.g. the app locked): stop borg, what was restored stays.
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        Restore?.Abandon();
        base.OnClosing(e);
    }

    private async void OpenFolder_Click(object? sender, RoutedEventArgs e)
    {
        if (Restore?.OpenFolderPath is { } folder && GetTopLevel(this)?.Launcher is { } launcher)
            await launcher.LaunchUriAsync(new Uri(Path.GetFullPath(folder)));
    }
}
