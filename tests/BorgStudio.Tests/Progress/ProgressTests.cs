using BorgStudio.App.Resources;
using BorgStudio.App.ViewModels;
using BorgStudio.Core.Progress;
using BorgStudio.Tests.TestSupport;

namespace BorgStudio.Tests.Progress;

/// <summary>Remaining time from the pace or from last time, remembered durations, and how progress reads.</summary>
public sealed class ProgressTests : IDisposable
{
    private readonly TemporaryDirectory _directory = new();
    private readonly ManualTime _time = new();

    public void Dispose() => _directory.Dispose();

    private static string Duration(int seconds) => BorgTexts.Duration(TimeSpan.FromSeconds(seconds));

    [Fact]
    public void The_remaining_time_follows_the_recent_pace_and_counts_down_between_reports()
    {
        var estimator = new ProgressEstimator(_time);
        Assert.Null(estimator.Fraction);
        Assert.Null(estimator.Remaining);

        estimator.Report(0, 1000);
        Assert.Equal(0, estimator.Fraction);
        Assert.True(estimator.HasReports);
        Assert.Null(estimator.Remaining); // no pace yet

        _time.Advance(TimeSpan.FromSeconds(10));
        estimator.Report(100, 1000); // 10/s
        Assert.Equal(0.1, estimator.Fraction);
        Assert.Equal(TimeSpan.FromSeconds(90), estimator.Remaining);

        _time.Advance(TimeSpan.FromSeconds(5)); // no report: counts down
        Assert.Equal(TimeSpan.FromSeconds(85), estimator.Remaining);
        _time.Advance(TimeSpan.FromSeconds(90)); // stalled for longer than expected: unknown again, not "0:00"
        Assert.Null(estimator.Remaining);
        _time.Advance(TimeSpan.FromSeconds(-90));

        // Faster now (50/s over the last 10 s): the estimate follows.
        for (var step = 1; step <= 10; step++)
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            estimator.Report(100 + step * 50, 1000);
        }
        Assert.Equal(TimeSpan.FromSeconds(8), estimator.Remaining); // 400 left at 50/s

        estimator.Report(1000, 1000);
        Assert.Equal(TimeSpan.Zero, estimator.Remaining);
        Assert.Equal(1, estimator.Fraction);
    }

    [Fact]
    public void Without_progress_last_time_tells_roughly_how_long_it_takes()
    {
        var estimator = new ProgressEstimator(_time);
        estimator.Expect(TimeSpan.FromSeconds(40));
        Assert.True(estimator.IsFromHistory);
        Assert.Equal(0, estimator.Fraction);
        Assert.Equal(TimeSpan.FromSeconds(40), estimator.Remaining);

        _time.Advance(TimeSpan.FromSeconds(10));
        estimator.Update();
        Assert.Equal(0.25, estimator.Fraction);
        Assert.Equal(TimeSpan.FromSeconds(30), estimator.Remaining);

        _time.Advance(TimeSpan.FromSeconds(31));
        estimator.Update();
        Assert.True(estimator.IsOverdue);
        Assert.Null(estimator.Fraction); // running light again
        Assert.Null(estimator.Remaining);

        // Real progress replaces the guess.
        estimator.Report(1, 2);
        Assert.False(estimator.IsFromHistory);
        Assert.False(estimator.IsOverdue);
        Assert.Equal(0.5, estimator.Fraction);
    }

    [Fact]
    public void Durations_are_remembered_per_repository_and_operation()
    {
        var file = _directory.Combine("durations.json");
        var history = new DurationHistory(file);
        var repository = Guid.NewGuid();
        var other = Guid.NewGuid();
        var key = DurationHistory.Key(repository, "list");
        Assert.Null(history.Get(key));

        history.Record(key, TimeSpan.FromSeconds(10));
        history.Record(key, TimeSpan.FromSeconds(20)); // mixed with the previous one
        history.Record(DurationHistory.Key(other, "list"), TimeSpan.FromSeconds(3));
        history.Record(DurationHistory.Key(repository, "info"), TimeSpan.Zero); // nothing to learn from

        var reloaded = new DurationHistory(file);
        Assert.Equal(TimeSpan.FromSeconds(15), reloaded.Get(key));
        Assert.Null(reloaded.Get(DurationHistory.Key(repository, "info")));

        reloaded.Remove(repository);
        Assert.Null(new DurationHistory(file).Get(key));
        Assert.Equal(TimeSpan.FromSeconds(3), new DurationHistory(file).Get(DurationHistory.Key(other, "list")));

        File.WriteAllText(file, "{ broken");
        Assert.Null(new DurationHistory(file).Get(key)); // only estimates: starts afresh
    }

    [Fact]
    public void Progress_reads_with_percent_elapsed_and_remaining_time()
    {
        var progress = new ProgressViewModel(_time);
        var cancelled = false;

        progress.Start("Prüfe …", cancel: () => cancelled = true);
        Assert.True(progress.IsActive);
        Assert.True(progress.IsIndeterminate);
        Assert.Equal(BorgTexts.Format(Strings.ProgressElapsed, Duration(0)), progress.SummaryText);

        progress.Report(0, 100);
        _time.Advance(TimeSpan.FromSeconds(4));
        progress.Report(40, 100, "home/me/a.txt");
        Assert.False(progress.IsIndeterminate);
        Assert.Equal(0.4, progress.Fraction);
        Assert.Equal("home/me/a.txt", progress.Detail);
        Assert.Equal(string.Join(" · ", BorgTexts.Format(Strings.ProgressPercent, 40), BorgTexts.Format(Strings.ProgressElapsed, Duration(4)),
            BorgTexts.Format(Strings.ProgressRemaining, Duration(6))), progress.SummaryText);

        _time.Advance(TimeSpan.FromSeconds(1));
        progress.Tick();
        Assert.EndsWith(BorgTexts.Format(Strings.ProgressRemaining, Duration(5)), progress.SummaryText);

        Assert.True(progress.CancelCommand.CanExecute(null));
        progress.CancelCommand.Execute(null);
        Assert.True(cancelled);
        Assert.False(progress.CanCancel); // once

        // From last time: "about", no percent.
        progress.Start(null, expected: TimeSpan.FromSeconds(20));
        Assert.Null(progress.Detail);
        Assert.False(progress.IsIndeterminate);
        _time.Advance(TimeSpan.FromSeconds(5));
        progress.Tick();
        Assert.Equal(0.25, progress.Fraction);
        Assert.Equal(string.Join(" · ", BorgTexts.Format(Strings.ProgressElapsed, Duration(5)),
            BorgTexts.Format(Strings.ProgressRemainingAbout, Duration(15))), progress.SummaryText);

        _time.Advance(TimeSpan.FromSeconds(20));
        progress.Tick();
        Assert.True(progress.IsIndeterminate);
        Assert.Equal(string.Join(" · ", BorgTexts.Format(Strings.ProgressElapsed, Duration(25)), Strings.ProgressOverdue), progress.SummaryText);

        progress.Stop();
        Assert.False(progress.IsActive);
        progress.Report(1); // after the end: ignored
        Assert.True(progress.IsIndeterminate);
    }

    [Fact]
    public void A_report_without_a_pace_yet_says_the_remaining_time_is_being_worked_out()
    {
        var progress = new ProgressViewModel(_time);
        progress.Start(null);
        progress.Report(0.3);
        Assert.Equal(string.Join(" · ", BorgTexts.Format(Strings.ProgressPercent, 30), BorgTexts.Format(Strings.ProgressElapsed, Duration(0)),
            Strings.ProgressCalculating), progress.SummaryText);
    }
}
