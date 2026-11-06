using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.Markup.Xaml;
using BorgStudio.App.Services;
using BorgStudio.App.ViewModels;
using BorgStudio.App.Views;
using BorgStudio.Core;
using BorgStudio.Core.Backups;
using BorgStudio.Core.Borg;
using BorgStudio.Core.Plugins;
using BorgStudio.Core.Progress;
using BorgStudio.Core.Repositories;
using BorgStudio.Core.Secrets;
using BorgStudio.Core.Security;
using BorgStudio.Core.Settings;
using BorgStudio.Core.Ssh;
using BorgStudio.Providers.Hetzner;
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
            var plugins = PluginCatalog.Load([new LocalProviderPlugin(), new SshProviderPlugin(), new HetznerProviderPlugin()], AppPaths.PluginsDirectory);

            var window = new MainWindow();
            var vault = Vault.CreateDefault();
            var viewModel = new MainViewModel(new AppServices(
                BorgDetector.CreateDefault(),
                plugins,
                RepositoryStore.CreateDefault(),
                SecretStores.CreateDefault(),
                BorgClient.CreateDefault(),
                new SshNetService(),
                SshKeyStore.CreateDefault(),
                SshKeygen.CreateDefault(),
                vault,
                SettingsStore.CreateDefault(),
                BackupJobStore.CreateDefault(),
                ArchiveCache.CreateDefault(vault),
                DurationHistory.CreateDefault(),
                new DialogService(window)));
            window.DataContext = viewModel;
            desktop.MainWindow = window;
            viewModel.CheckBorgCommand.Execute(null);
            StartVaultAutoLock(vault, viewModel);
            window.Opened += async (_, _) => await viewModel.OfferVaultForLegacyKeysAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Any key or click in any window counts as use; a timer locks the vault after the configured idle time.</summary>
    private static void StartVaultAutoLock(Vault vault, MainViewModel viewModel)
    {
        InputElement.KeyDownEvent.AddClassHandler<Window>((_, _) => vault.Touch(), RoutingStrategies.Tunnel, handledEventsToo: true);
        InputElement.PointerPressedEvent.AddClassHandler<Window>((_, _) => vault.Touch(), RoutingStrategies.Tunnel, handledEventsToo: true);
        InputElement.PointerWheelChangedEvent.AddClassHandler<Window>((_, _) => vault.Touch(), RoutingStrategies.Tunnel, handledEventsToo: true);

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        timer.Tick += (_, _) => viewModel.CheckVaultIdle();
        timer.Start();
    }
}
