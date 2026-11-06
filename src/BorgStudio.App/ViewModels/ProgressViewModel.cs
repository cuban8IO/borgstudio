using BorgStudio.App.Resources;
using BorgStudio.Core.Progress;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BorgStudio.App.ViewModels;

/// <summary>
/// Progress of a wait the user sees: bar, percent, elapsed and remaining time, the current item – so it is always
/// clear the app is working and not stuck. Without progress from borg the bar runs on what the same operation took
/// last time, or as a running light with the elapsed time counting up. In the app the times move on every second.
/// </summary>
public sealed partial class ProgressViewModel : ViewModelBase
{
    private readonly TimeProvider _time;
    private ProgressEstimator _estimator;
    private CancellationTokenSource? _ticking;
    private Action? _cancel;

    public ProgressViewModel(TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        _estimator = new ProgressEstimator(_time);
    }

    /// <summary>What is going on, e.g. "Prüfe Verbindung …"; <c>null</c> if the surroundings say it already.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasText))]
    public partial string? Text { get; set; }

    public bool HasText => !string.IsNullOrEmpty(Text);

    /// <summary>The item being worked on, e.g. a file.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDetail))]
    public partial string? Detail { get; private set; }

    public bool HasDetail => !string.IsNullOrEmpty(Detail);

    [ObservableProperty]
    public partial bool IsActive { get; private set; }

    /// <summary>0..1 for the bar.</summary>
    [ObservableProperty]
    public partial double Fraction { get; private set; }

    [ObservableProperty]
    public partial bool IsIndeterminate { get; private set; } = true;

    /// <summary>"45 % · 0:12 vergangen · noch 0:30".</summary>
    [ObservableProperty]
    public partial string SummaryText { get; private set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    public partial bool CanCancel { get; private set; }

    public TimeSpan Elapsed => _estimator.Elapsed;

    public TimeSpan? Remaining => _estimator.Remaining;

    /// <summary>Starts anew.</summary>
    /// <param name="expected">How long the same operation took last time (used until real progress arrives).</param>
    /// <param name="cancel">Offers "Cancel", which calls this.</param>
    public void Start(string? text, TimeSpan? expected = null, Action? cancel = null)
    {
        _estimator = new ProgressEstimator(_time);
        _estimator.Expect(expected);
        _cancel = cancel;
        CanCancel = cancel is not null;
        Text = text;
        Detail = null;
        IsActive = true;
        Refresh();
        StartTicking();
    }

    /// <summary><paramref name="done"/> of <paramref name="total"/>, in any unit.</summary>
    public void Report(double done, double total, string? detail = null)
    {
        if (!IsActive)
            return;
        _estimator.Report(done, total);
        if (detail is not null)
            Detail = detail;
        Refresh();
    }

    /// <summary>0..1.</summary>
    public void Report(double fraction) => Report(fraction, 1);

    /// <summary>Tells what is going on without a fraction (e.g. "12.345 entries read").</summary>
    public void SetDetail(string detail)
    {
        if (IsActive)
            Detail = detail;
    }

    /// <summary>Moves the times on; called every second while active.</summary>
    public void Tick()
    {
        if (!IsActive)
            return;
        _estimator.Update();
        Refresh();
    }

    public void Stop()
    {
        _ticking?.Cancel();
        _ticking = null;
        _cancel = null;
        CanCancel = false;
        IsActive = false;
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        CanCancel = false;
        _cancel?.Invoke();
    }

    private void Refresh()
    {
        Fraction = _estimator.Fraction ?? 0;
        IsIndeterminate = _estimator.Fraction is null;
        SummaryText = Summary(_estimator);
    }

    internal static string Summary(ProgressEstimator estimator)
    {
        var parts = new List<string>();
        if (estimator is { Fraction: { } fraction, IsFromHistory: false })
            parts.Add(BorgTexts.Format(Strings.ProgressPercent, (int)Math.Floor(fraction * 100)));
        parts.Add(BorgTexts.Format(Strings.ProgressElapsed, BorgTexts.Duration(estimator.Elapsed)));
        if (estimator.IsOverdue)
            parts.Add(Strings.ProgressOverdue);
        else if (estimator.Remaining is { } remaining)
            parts.Add(BorgTexts.Format(estimator.IsFromHistory ? Strings.ProgressRemainingAbout : Strings.ProgressRemaining,
                BorgTexts.Duration(remaining)));
        else if (estimator.HasReports)
            parts.Add(Strings.ProgressCalculating);
        return string.Join(" · ", parts);
    }

    // Only in the app (with a UI thread): tests tick by hand.
    private void StartTicking()
    {
        _ticking?.Cancel();
        if (!HasUiThread)
            return;
        var ticking = _ticking = new CancellationTokenSource();
        _ = TickAsync(ticking.Token);
    }

    private async Task TickAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
                OnViewModelThread(Tick);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
