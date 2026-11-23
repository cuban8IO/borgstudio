using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using BorgStudio.App.Resources;
using BorgStudio.App.ViewModels;

namespace BorgStudio.App.Services;

/// <summary>
/// The icon in the notification area while BorgStudio runs in the background: a dot shows a running backup (blue), a
/// failed one (red) or paused schedules (grey); the menu opens the window, shows the next backup, starts a job, pauses
/// the schedules and quits.
/// </summary>
public sealed class TrayController
{
    private readonly Application _application;
    private readonly MainViewModel _main;
    private readonly Action _show;
    private readonly Action _quit;
    private readonly Dictionary<TrayState, WindowIcon> _icons = [];

    private TrayIcon? _icon;
    private NativeMenuItem? _next;
    private NativeMenuItem? _runJob;
    private NativeMenuItem? _pause;
    private string? _jobsShown;
    private TrayState? _stateShown;

    public TrayController(Application application, MainViewModel main, Action show, Action quit)
    {
        _application = application;
        _main = main;
        _show = show;
        _quit = quit;
        main.BackgroundStateChanged += (_, _) => Dispatcher.UIThread.Post(Update);
        Update();
    }

    private void Update()
    {
        // Always there while BorgStudio runs: quick actions, and where a closed window goes.
        if (_icon is null)
            Create();
        var state = _main.TrayState;
        if (state != _stateShown)
        {
            _icon!.Icon = Icon(state);
            _stateShown = state;
        }
        _icon!.ToolTipText = _main.TrayToolTip;
        _next!.Header = _main.NextScheduledText;
        _pause!.IsChecked = _main.SchedulesPaused;
        UpdateJobs();
    }

    private void Create()
    {
        _next = new NativeMenuItem { IsEnabled = false };
        _runJob = new NativeMenuItem(Strings.TrayRunJob) { Menu = [] };
        _pause = new NativeMenuItem(Strings.TrayPause) { ToggleType = MenuItemToggleType.CheckBox };
        _pause.Click += (_, _) => _main.SetSchedulesPaused(!_main.SchedulesPaused);
        var open = new NativeMenuItem(Strings.TrayOpen);
        open.Click += (_, _) => _show();
        var quit = new NativeMenuItem(Strings.TrayQuit);
        quit.Click += (_, _) => _quit();

        _icon = new TrayIcon
        {
            Menu = [open, new NativeMenuItemSeparator(), _next, _runJob, _pause, new NativeMenuItemSeparator(), quit],
        };
        _icon.Clicked += (_, _) => _show();
        TrayIcon.SetIcons(_application, [_icon]);
        _jobsShown = null;
        _stateShown = null;
    }

    /// <summary>One entry per job; rebuilt only when the jobs changed (not while the menu may be open for nothing).</summary>
    private void UpdateJobs()
    {
        var jobs = _main.AllJobs.OrderBy(job => job.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        var key = string.Join('|', jobs.Select(job => $"{job.Id}:{job.Name}"));
        if (key == _jobsShown)
            return;
        _jobsShown = key;

        var menu = _runJob!.Menu!;
        menu.Items.Clear();
        foreach (var job in jobs)
        {
            var item = new NativeMenuItem(job.Name);
            var id = job.Id;
            item.Click += async (_, _) =>
            {
                _show();
                await _main.RunJobNowAsync(id);
            };
            menu.Items.Add(item);
        }
        if (jobs.Count == 0)
            menu.Items.Add(new NativeMenuItem(Strings.TrayNoJobs) { IsEnabled = false });
    }

    private WindowIcon Icon(TrayState state)
    {
        if (_icons.TryGetValue(state, out var icon))
            return icon;

        using var logo = new Bitmap(AssetLoader.Open(new Uri("avares://BorgStudio/Assets/borgstudio.png")));
        const int size = 64;
        var target = new RenderTargetBitmap(new PixelSize(size, size), new Vector(96, 96));
        using (var context = target.CreateDrawingContext())
        {
            context.DrawImage(logo, new Rect(0, 0, size, size));
            Color? dot = state switch
            {
                TrayState.Running => Color.Parse("#2F80ED"),
                TrayState.Failed => Color.Parse("#D64545"),
                TrayState.Paused => Color.Parse("#8A9099"),
                _ => null,
            };
            if (dot is { } color)
            {
                var center = new Point(size - 15, size - 15);
                context.DrawEllipse(Brushes.White, null, center, 15, 15);
                context.DrawEllipse(new SolidColorBrush(color), null, center, 12, 12);
            }
        }
        return _icons[state] = new WindowIcon(target);
    }
}
