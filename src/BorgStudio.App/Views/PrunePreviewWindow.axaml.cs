using Avalonia.Controls;
using Avalonia.Interactivity;

namespace BorgStudio.App.Views;

/// <summary>The archives a clean-up would delete; the dialog result is <c>true</c> if the user confirms.</summary>
public partial class PrunePreviewWindow : Window
{
    public PrunePreviewWindow()
    {
        InitializeComponent();
    }

    private void Confirm_Click(object? sender, RoutedEventArgs e) => Close(true);

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);
}
