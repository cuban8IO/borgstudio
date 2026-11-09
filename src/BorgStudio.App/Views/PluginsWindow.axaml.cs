using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using BorgStudio.App.ViewModels;

namespace BorgStudio.App.Views;

public partial class PluginsWindow : Window
{
    public PluginsWindow()
    {
        InitializeComponent();
    }

    private async void OpenPluginsFolder_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not PluginsViewModel viewModel)
            return;

        // Created on demand, so users find the place to put plugins.
        var directory = Directory.CreateDirectory(viewModel.PluginsDirectory);
        await Launcher.LaunchDirectoryInfoAsync(directory);
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
}
