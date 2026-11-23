using Avalonia.Controls;
using Avalonia.Interactivity;

namespace BorgStudio.App.Views;

/// <summary>Picks the two archives to compare; the dialog result is <c>true</c> for "Vergleichen".</summary>
public partial class ArchiveCompareWindow : Window
{
    public ArchiveCompareWindow()
    {
        InitializeComponent();
    }

    private void Compare_Click(object? sender, RoutedEventArgs e) => Close(true);

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);
}
