using Avalonia.Controls;
using Avalonia.Interactivity;
using BorgStudio.App.Services;
using BorgStudio.App.ViewModels;

namespace BorgStudio.App.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        // PIN dialogs belong to this window, not to the main window.
        if (DataContext is SettingsViewModel settings)
            settings.Dialogs = new DialogService(this);
    }

    protected override void OnClosed(EventArgs e)
    {
        (DataContext as SettingsViewModel)?.Detach();
        base.OnClosed(e);
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
}
