using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using BorgStudio.App.Services;
using BorgStudio.App.ViewModels;
using BorgStudio.App.Views;
using BorgStudio.Core;
using BorgStudio.Core.Borg;
using BorgStudio.Core.Plugins;
using BorgStudio.Core.Repositories;
using BorgStudio.Core.Secrets;
using BorgStudio.Core.Ssh;
using BorgStudio.Providers.Local;
using BorgStudio.Providers.Ssh;

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
            var plugins = PluginCatalog.Load([new LocalProviderPlugin(), new SshProviderPlugin()], AppPaths.PluginsDirectory);

            var window = new MainWindow();
            var viewModel = new MainViewModel(new AppServices(
                BorgDetector.CreateDefault(),
                plugins,
                RepositoryStore.CreateDefault(),
                SecretStores.CreateDefault(),
                BorgClient.CreateDefault(),
                new SshNetService(),
                SshKeyStore.CreateDefault(),
                new DialogService(window)));
            window.DataContext = viewModel;
            desktop.MainWindow = window;
            viewModel.CheckBorgCommand.Execute(null);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
