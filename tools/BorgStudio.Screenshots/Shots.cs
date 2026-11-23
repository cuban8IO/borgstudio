using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.VisualTree;
using BorgStudio.App.Resources;
using BorgStudio.App.Services;
using BorgStudio.App.ViewModels;
using BorgStudio.App.Views;
using BorgStudio.Core.Plugins;
using BorgStudio.Providers.BorgBase;
using BorgStudio.Providers.Hetzner;
using BorgStudio.Providers.Local;
using BorgStudio.Providers.NetworkDisk;
using BorgStudio.Providers.RsyncNet;
using BorgStudio.Providers.Sftp;
using BorgStudio.Providers.Ssh;
using BorgStudio.Providers.Usb;

namespace BorgStudio.Screenshots;

/// <summary>
/// One picture: its file name (without .png) and how to bring the window into shape. A shot that has to take the picture
/// itself while something runs saves it with <see cref="Scene.Save"/> and returns <c>null</c>.
/// </summary>
public sealed record Shot(string Name, Func<Scene, Task<Window?>> Render);

public static class Shots
{
    public static IReadOnlyList<Shot> All { get; } =
    [
        // The main window.
        new("dashboard", async scene => MainShot(await scene.MainAsync(), 1180, 820)),
        new("main-overview", async scene => MainShot(await scene.MainAsync(scene.Laptop, MainViewModel.OverviewTab), 1180, 760)),
        new("main-jobs", async scene => MainShot(await scene.MainAsync(scene.Laptop, MainViewModel.JobsTab), 1180, 760)),
        new("main-archives", async scene => MainShot(await scene.MainAsync(scene.Laptop, MainViewModel.ArchivesTab), 1180, 720)),
        new("statistics", async scene => MainShot(await scene.MainAsync(scene.Laptop, MainViewModel.StatisticsTab), 1180, 1180)),
        new("mounts", Mounts),
        new("tasks", RunningTasks),

        // Windows of their own.
        new("job-editor", JobEditor),
        new("providers", scene => Providers(scene, untested: false)),
        new("add-repository", scene => Providers(scene, untested: true)),
        new("archive-browser", ArchiveBrowser),
        new("restore", Restore),
        new("search", Search),
        new("compare", Compare),
        new("notifications", async scene => Show(new NotificationsWindow { DataContext = new NotificationsViewModel(await scene.MainAsync()) },
            640, 1000)),
        new("settings", async scene =>
        {
            var window = new SettingsWindow();
            window.DataContext = new SettingsViewModel(await scene.MainAsync(), scene.Test.Dialogs);
            return Show(window, 580, 860);
        }),
    ];

    private static Window MainShot(MainViewModel main, double width, double height) =>
        Show(new MainWindow { DataContext = main }, width, height);

    private static Window Show(Window window, double width, double height)
    {
        window.Width = width;
        window.Height = height;
        window.Show();
        Program.Settle();
        return window;
    }

    /// <summary>Opens the flyout of the main window's button with this accessible name or tooltip.</summary>
    private static void OpenFlyout(Window window, string name)
    {
        Program.Settle();
        var button = window.GetVisualDescendants().OfType<Button>()
            .First(button => button.Flyout is not null
                && (AutomationProperties.GetName(button) == name || ToolTip.GetTip(button) as string == name));
        button.Flyout!.ShowAt(button);
        Program.Settle();
    }

    private static async Task<Window?> Mounts(Scene scene)
    {
        var main = await scene.MainAsync(scene.Laptop, MainViewModel.ArchivesTab);
        foreach (var archive in main.Archives.Take(2))
        {
            main.SelectedArchive = archive;
            await main.MountArchiveCommand.ExecuteAsync(null);
        }
        var window = MainShot(main, 1180, 720);
        OpenFlyout(window, Strings.MountsTip);
        return window;
    }

    /// <summary>The list of running tasks while a backup works (taken in the middle of fake borg's progress).</summary>
    private static async Task<Window?> RunningTasks(Scene scene)
    {
        var main = await scene.MainAsync(scene.Laptop, MainViewModel.JobsTab);
        var window = MainShot(main, 1180, 720);
        main.SelectedJob = main.RepositoryJobs.First();
        scene.Test.Borg.AfterCreateProgress = step =>
        {
            if (step != 2)
                return;
            // Behind the list: the repository's overview rather than the backup's own card.
            main.SelectedDetailsTab = MainViewModel.OverviewTab;
            OpenFlyout(window, Strings.TasksHeader);
            scene.Save(window);
        };
        await main.RunJobCommand.ExecuteAsync(null);
        window.Close();
        return null;
    }

    private static async Task<Window?> JobEditor(Scene scene)
    {
        var main = await scene.MainAsync(scene.Laptop, MainViewModel.JobsTab);
        var job = main.RepositoryJobs.First().Job;
        var editor = new BackupJobEditorViewModel(scene.Laptop, scene.Test.Borg.Detector(wsl: true).DetectAsync().Result, scene.Test.Dialogs, job);
        return Show(new BackupJobEditorWindow { DataContext = editor }, 640, 1020);
    }

    /// <summary>"Where is the repository?" – for the product page without the providers not tested yet.</summary>
    private static async Task<Window?> Providers(Scene scene, bool untested)
    {
        var plugins = PluginCatalog.Load([new LocalProviderPlugin(), new UsbProviderPlugin(scene.Test.Drives),
            new NetworkDiskProviderPlugin(scene.Test.NetworkDisks, scene.Test.Drives), new SshProviderPlugin(), new SftpProviderPlugin(),
            new HetznerProviderPlugin(), new RsyncNetProviderPlugin(), new BorgBaseProviderPlugin(scene.Test.BorgBaseApi)], "no-plugins-here");
        if (!untested)
            plugins = PluginCatalog.Load([new LocalProviderPlugin(), new UsbProviderPlugin(scene.Test.Drives), new SshProviderPlugin(),
                new SftpProviderPlugin(), new HetznerProviderPlugin(), new BorgBaseProviderPlugin(scene.Test.BorgBaseApi)], "no-plugins-here");
        var services = scene.Services with { Plugins = plugins };
        var main = new MainViewModel(services);
        await main.CheckBorgCommand.ExecuteAsync(null);
        RepositoryEditorViewModel? editor = null;
        scene.Test.Dialogs.Editor = shown =>
        {
            editor = shown;
            return Task.CompletedTask;
        };
        await main.AddRepositoryCommand.ExecuteAsync(null);
        return Show(new RepositoryEditorWindow { DataContext = editor }, 640, 820);
    }

    private static async Task<ArchiveBrowserViewModel> BrowserAsync(Scene scene)
    {
        var main = await scene.MainAsync(scene.Laptop, MainViewModel.ArchivesTab);
        main.SelectedArchive = main.Archives.First();
        scene.Test.Dialogs.Browser = async _ => await main.BrowserLoad;
        await main.OpenArchiveCommand.ExecuteAsync(null);
        return scene.Test.Dialogs.Browsers.Last();
    }

    private static async Task<Window?> ArchiveBrowser(Scene scene)
    {
        var browser = await BrowserAsync(scene);
        return Show(new ArchiveBrowserWindow { DataContext = browser }, 1000, 640);
    }

    private static async Task<Window?> Restore(Scene scene)
    {
        var browser = await BrowserAsync(scene);
        foreach (var entry in browser.Entries.Where(entry => !entry.IsFolder).Take(2))
            entry.IsSelected = true;
        await browser.RestoreSelectionCommand.ExecuteAsync(null);
        return Show(new RestoreWindow { DataContext = scene.Test.Dialogs.Restores.Last() }, 640, 400);
    }

    private static async Task<Window?> Search(Scene scene)
    {
        var main = await scene.MainAsync(scene.Laptop, MainViewModel.ArchivesTab);
        await main.SearchArchivesCommand.ExecuteAsync(null);
        var search = scene.Test.Dialogs.Searches.Last();
        // Read every archive's file list once, then search them all.
        await search.LoadMissingCommand.ExecuteAsync(null);
        await search.Searching;
        search.SearchText = Texts.Pick("*invoice*", "*rechnung*");
        await search.SearchCommand.ExecuteAsync(null);
        await search.Searching;
        return Show(new ArchiveSearchWindow { DataContext = search }, 900, 560);
    }

    private static async Task<Window?> Compare(Scene scene)
    {
        var main = await scene.MainAsync(scene.Laptop, MainViewModel.ArchivesTab);
        main.SelectedArchive = main.Archives.First();
        scene.Test.Dialogs.PickCompare = _ => true;
        await main.CompareArchivesCommand.ExecuteAsync(null);
        var diff = scene.Test.Dialogs.Diffs.Last();
        // The app keeps its way to stop borg while the window is open; here the comparison is over already.
        diff.Cancel = null;
        return Show(new ArchiveDiffWindow { DataContext = diff }, 1000, 640);
    }
}
