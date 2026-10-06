using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using BorgStudio.App.ViewModels;
using BorgStudio.App.Views;
using BorgStudio.Core;
using BorgStudio.Core.Borg;
using BorgStudio.Core.Plugins;
using BorgStudio.Providers.Local;

namespace BorgStudio.App;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Built-in providers use the same plugin API as external ones.
            var plugins = PluginCatalog.Load([new LocalProviderPlugin()], AppPaths.PluginsDirectory);

            var viewModel = new MainViewModel(BorgDetector.CreateDefault(), plugins);
            desktop.MainWindow = new MainWindow
            {
                DataContext = viewModel,
            };
            viewModel.CheckBorgCommand.Execute(null);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
