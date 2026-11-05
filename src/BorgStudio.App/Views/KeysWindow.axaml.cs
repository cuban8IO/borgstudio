using Avalonia.Controls;
using Avalonia.Interactivity;
using BorgStudio.App.Services;
using BorgStudio.App.ViewModels;

namespace BorgStudio.App.Views;

public partial class KeysWindow : Window
{
    public KeysWindow()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        // Nested dialogs (import, confirmations) belong to this window.
        if (DataContext is KeysViewModel keys)
            keys.Dialogs = new DialogService(this);
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
}
