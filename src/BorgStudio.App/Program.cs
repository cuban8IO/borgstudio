using Avalonia;
using System;
using BorgStudio.App.Services;
using BorgStudio.Core;
using BorgStudio.Core.Settings;
using BorgStudio.Core.Updates;

namespace BorgStudio.App;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // Started from a downloaded update to install it: no window, the app starts again when done.
        if (UpdateApplier.IsApplyCall(args))
        {
            Environment.ExitCode = UpdateApplier.Run(args);
            return;
        }

        // Texts are bound once when the windows load, so the language is set before anything else.
        AppLanguages.Apply(SettingsStore.CreateDefault().Load().Language);

        // Started by a schedule: one backup job, no window.
        if (ScheduledRun.IsRunCall(args))
        {
            Environment.ExitCode = ScheduledRun.Run(args);
            return;
        }

        // One BorgStudio per user: a second start brings the first one's window forward.
        if (SingleInstance.TryClaim(AppPaths.DataDirectory) is null && SingleInstance.ShowExisting(AppPaths.DataDirectory))
            return;

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            SingleInstance.Current?.Dispose();
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
