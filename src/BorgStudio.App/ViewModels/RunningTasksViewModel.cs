using System.Collections.ObjectModel;
using System.ComponentModel;
using BorgStudio.App.Resources;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BorgStudio.App.ViewModels;

public enum RunningTaskState
{
    /// <summary>Working now, in this app.</summary>
    Running,

    /// <summary>Queued, or held back by a condition of its job.</summary>
    Waiting,

    /// <summary>A run the system's task scheduler started in a process of its own.</summary>
    Elsewhere,
}

/// <summary>How far a task is, read from what it reports.</summary>
/// <param name="IsActive">Working now; a tracked task that is not is left out of the list.</param>
/// <param name="Fraction">0..1; <c>null</c> when it cannot tell (running light).</param>
/// <param name="Status">"45 % · 0:12 vergangen · noch 0:30", "In der Warteschlange" …</param>
public readonly record struct TaskProgress(bool IsActive, double? Fraction, string Status, TimeSpan? Remaining = null, bool CanCancel = false);

/// <summary>One entry of the "running tasks" list in the status bar.</summary>
public sealed partial class RunningTaskViewModel : ObservableObject, IDisposable
{
    private readonly Func<TaskProgress> _read;
    private readonly Action? _cancel;
    private readonly Action? _show;
    private readonly INotifyPropertyChanged[] _sources;

    /// <param name="read">What it reports now; read again whenever one of <paramref name="sources"/> changes.</param>
    /// <param name="cancel">"Abbrechen" (offered while <see cref="TaskProgress.CanCancel"/>); <c>null</c>: it cannot be stopped from here.</param>
    /// <param name="show">Shows the task (its repository, its window).</param>
    public RunningTaskViewModel(string title, string? detail, RunningTaskState state, Func<TaskProgress> read, Action? cancel = null,
        Action? show = null, params INotifyPropertyChanged[] sources)
    {
        Title = title;
        Detail = detail;
        State = state;
        _read = read;
        _cancel = cancel;
        _show = show;
        _sources = sources;
        foreach (var source in sources)
            source.PropertyChanged += OnSourceChanged;
        Refresh();
    }

    /// <summary>A task that only says what it is doing (waiting, or running elsewhere).</summary>
    public static RunningTaskViewModel Fixed(string title, string? detail, RunningTaskState state, string status, Action? cancel = null,
        Action? show = null) =>
        new(title, detail, state, () => new TaskProgress(true, null, status, CanCancel: cancel is not null), cancel, show);

    public string Title { get; }

    public string? Detail { get; }

    public bool HasDetail => !string.IsNullOrEmpty(Detail);

    public RunningTaskState State { get; }

    public bool IsRunning => State == RunningTaskState.Running;

    [ObservableProperty]
    public partial bool IsActive { get; private set; }

    [ObservableProperty]
    public partial double Fraction { get; private set; }

    [ObservableProperty]
    public partial bool IsIndeterminate { get; private set; }

    [ObservableProperty]
    public partial string Status { get; private set; } = "";

    [ObservableProperty]
    public partial TimeSpan? Remaining { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    public partial bool CanCancel { get; private set; }

    public bool CanShow => _show is not null;

    /// <summary>Raised when what it reports changed (the list's summary follows).</summary>
    public event EventHandler? Changed;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        CanCancel = false;
        _cancel?.Invoke();
    }

    [RelayCommand(CanExecute = nameof(CanShow))]
    private void Show() => _show?.Invoke();

    /// <summary>Reads what the task reports now.</summary>
    public void Refresh()
    {
        var progress = _read();
        IsActive = progress.IsActive;
        Fraction = progress.Fraction ?? 0;
        IsIndeterminate = progress.Fraction is null;
        Status = progress.Status;
        Remaining = progress.Remaining;
        CanCancel = progress.CanCancel && _cancel is not null;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnSourceChanged(object? sender, PropertyChangedEventArgs e) => Refresh();

    public void Dispose()
    {
        foreach (var source in _sources)
            source.PropertyChanged -= OnSourceChanged;
    }
}

/// <summary>
/// "Laufende Aufgaben" in the status bar: what this app is doing (backups, checks, the work of open windows), what waits
/// (queued, held back by a condition) and what the system's task scheduler runs in processes of its own – with how long it
/// will still take, roughly.
/// </summary>
public sealed partial class RunningTasksViewModel : ObservableObject
{
    private readonly List<RunningTaskViewModel> _tracked = [];
    private List<RunningTaskViewModel> _waiting = [];
    private List<RunningTaskViewModel> _elsewhere = [];

    /// <summary>The tasks shown: running ones first, then waiting ones, then those of the task scheduler.</summary>
    public ObservableCollection<RunningTaskViewModel> Items { get; } = [];

    [ObservableProperty]
    public partial string SummaryText { get; private set; } = Strings.TasksNone;

    [ObservableProperty]
    public partial bool HasTasks { get; private set; }

    /// <summary>Something works now (not only waits): the status bar shows it busy.</summary>
    [ObservableProperty]
    public partial bool IsBusy { get; private set; }

    /// <summary>Shows <paramref name="task"/> whenever it is active, until the returned registration is disposed.</summary>
    public IDisposable Track(RunningTaskViewModel task)
    {
        _tracked.Add(task);
        task.Changed += OnTaskChanged;
        Update();
        return new Registration(() =>
        {
            task.Changed -= OnTaskChanged;
            task.Dispose();
            _tracked.Remove(task);
            Update();
        });
    }

    /// <summary>
    /// Shows <paramref name="task"/> while it is active and lets it go once it has been active and stops – for work that runs
    /// once (a restore, an export) where nobody hands back a registration.
    /// </summary>
    public void TrackOnce(RunningTaskViewModel task)
    {
        var started = false;
        IDisposable? registration = null;
        void Follow(object? sender, EventArgs e)
        {
            if (task.IsActive)
                started = true;
            else if (started)
            {
                task.Changed -= Follow;
                registration?.Dispose();
            }
        }
        task.Changed += Follow;
        registration = Track(task);
        Follow(task, EventArgs.Empty);
    }

    /// <summary>Replaces the queued and held back runs.</summary>
    public void SetWaiting(IEnumerable<RunningTaskViewModel> waiting)
    {
        _waiting = [.. waiting];
        Update();
    }

    /// <summary>Replaces the runs of the system's task scheduler.</summary>
    public void SetElsewhere(IEnumerable<RunningTaskViewModel> elsewhere)
    {
        _elsewhere = [.. elsewhere];
        Update();
    }

    private void OnTaskChanged(object? sender, EventArgs e) => Update();

    private void Update()
    {
        var shown = _tracked.Where(task => task.IsActive).Concat(_waiting).Concat(_elsewhere).ToList();
        if (!shown.SequenceEqual(Items))
        {
            Items.Clear();
            foreach (var task in shown)
                Items.Add(task);
        }

        HasTasks = shown.Count > 0;
        IsBusy = shown.Any(task => task.State != RunningTaskState.Waiting);
        var count = shown.Count switch
        {
            0 => Strings.TasksNone,
            1 => Strings.TasksOne,
            var many => BorgTexts.Format(Strings.TasksMany, many),
        };
        // The one that takes longest says when all is done.
        SummaryText = shown.Where(task => task.IsRunning).Select(task => task.Remaining).Max() is { } remaining
            ? BorgTexts.Format(Strings.TasksRemaining, count, BorgTexts.Duration(remaining))
            : count;
    }

    private sealed class Registration(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;

        public void Dispose()
        {
            _dispose?.Invoke();
            _dispose = null;
        }
    }
}
