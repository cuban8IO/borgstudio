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
using BorgStudio.Core.Notifications;
using BorgStudio.Core.Borg;
using BorgStudio.Core.Plugins;
using BorgStudio.Core.Progress;
using BorgStudio.Core.Repositories;
using BorgStudio.Core.Secrets;
using BorgStudio.Core.Security;
using BorgStudio.Core.Settings;
using BorgStudio.Core.Ssh;
using BorgStudio.Core.Updates;
using BorgStudio.Providers.BorgBase;
using BorgStudio.Providers.Hetzner;
using BorgStudio.Providers.Local;
using BorgStudio.Providers.RsyncNet;
using BorgStudio.Providers.Ssh;
using BorgStudio.Providers.Usb;

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
            var window = new MainWindow();
            var services = CreateServices(new DialogService(window));
            var vault = services.Vault;
            var viewModel = new MainViewModel(services);
            window.DataContext = viewModel;
            // In the background the app outlives its window; it ends when the window really closes (or on "Quit").
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            window.Closed += (_, _) => desktop.Shutdown();
            // Started at login: only the icon in the notification area.
            var hidden = desktop.Args?.Contains(Autostart.BackgroundArgument) == true && viewModel.IsBackgroundMode;
            if (!hidden)
                desktop.MainWindow = window;
            viewModel.CheckBorgCommand.Execute(null);
            StartVaultAutoLock(vault, viewModel);
            StartUpdateChecks(viewModel);
            StartBackground(window, viewModel, desktop);
            window.Opened += async (_, _) =>
            {
                await viewModel.OfferVaultForLegacyKeysAsync();
                await viewModel.ShowWarrantyNoticeIfDueAsync();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>The icon in the notification area, the scheduler's look every half minute, and a second start's call.</summary>
    private void StartBackground(MainWindow window, MainViewModel viewModel, IClassicDesktopStyleApplicationLifetime desktop)
    {
        void Show()
        {
            desktop.MainWindow ??= window;
            window.ShowFromTray();
        }

        _ = new TrayController(this, viewModel, Show, window.Quit);
        if (SingleInstance.Current is { } instance)
            instance.ShowRequested += (_, _) => Dispatcher.UIThread.Post(Show);

        // The first look (after half a minute, when the network is up after login) makes up for missed times.
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        timer.Tick += (_, _) => viewModel.ScheduleTick();
        timer.Start();

        // Attached drives (backing up USB drives when they are attached).
        viewModel.DriveTick();
        var drives = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        drives.Tick += (_, _) => viewModel.DriveTick();
        drives.Start();

        // The incoming webhook (if switched on, in background mode); it ends with the app.
        viewModel.ApplyTrigger();
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lifetime)
            lifetime.Exit += (_, _) => viewModel.StopTrigger();
    }

    /// <summary>The real services – for the window, and for runs without window (scheduled backups).</summary>
    public static AppServices CreateServices(IDialogService dialogs)
    {
        // Built-in providers use the same plugin API as external ones.
        var plugins = PluginCatalog.Load([new LocalProviderPlugin(), new UsbProviderPlugin(), new SshProviderPlugin(), new HetznerProviderPlugin(), new RsyncNetProviderPlugin(), new BorgBaseProviderPlugin()], AppPaths.PluginsDirectory);
        var vault = Vault.CreateDefault();
        return new AppServices(
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
            dialogs,
            Updater.CreateDefault(AppVersion.Current),
            SystemScheduler.CreateDefault(),
            Autostart.CreateDefault(),
            Notifier.CreateDefault(NotificationIcon.Ensure(AppPaths.DataDirectory), Environment.ProcessPath));
    }

    /// <summary>Reports a finished update, then looks for a new release now and once a day.</summary>
    private static void StartUpdateChecks(MainViewModel viewModel)
    {
        viewModel.ShowUpdateOutcome();
        _ = viewModel.CheckForUpdatesInBackgroundAsync();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromHours(24) };
        timer.Tick += async (_, _) => await viewModel.CheckForUpdatesInBackgroundAsync();
        timer.Start();
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
