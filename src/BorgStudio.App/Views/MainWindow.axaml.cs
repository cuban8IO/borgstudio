using Avalonia.Controls;
using BorgStudio.App.ViewModels;
using BorgStudio.Core;

namespace BorgStudio.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private async void OpenPlugins_Click(object? sender, EventArgs e)
    {
        if (DataContext is not MainViewModel viewModel)
            return;

        var window = new PluginsWindow { DataContext = new PluginsViewModel(viewModel.Plugins, AppPaths.PluginsDirectory) };
        await window.ShowDialog(this);
    }
}
