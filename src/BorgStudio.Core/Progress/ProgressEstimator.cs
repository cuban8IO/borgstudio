namespace BorgStudio.Core.Progress;

/// <summary>
/// Works out how far an operation is and how long it will still take. With reported progress the remaining time comes
/// from the pace of the last few seconds (so it follows changes, e.g. many small files, then large ones); without, from
/// how long the same operation took last time (<see cref="Expect"/>). Between reports the remaining time counts down.
/// </summary>
public sealed class ProgressEstimator
{
    /// <summary>The pace is measured over this window.</summary>
    public static readonly TimeSpan RateWindow = TimeSpan.FromSeconds(10);

    private readonly TimeProvider _time;
    private readonly Queue<(DateTimeOffset At, double Done)> _samples = new();
    private (DateTimeOffset At, double Done)? _first;
    private TimeSpan? _expected;
    private TimeSpan? _remainingAtReport;
    private DateTimeOffset _reportedAt;

    public ProgressEstimator(TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        Started = _time.GetUtcNow();
    }

    public DateTimeOffset Started { get; }

    public TimeSpan Elapsed => _time.GetUtcNow() - Started;

    /// <summary>0..1; <c>null</c> while unknown (no progress reported and nothing to expect).</summary>
    public double? Fraction { get; private set; }

    /// <summary>borg reported progress (as opposed to an estimate from last time, or nothing).</summary>
    public bool HasReports => _first is not null;

    /// <summary>The fraction and remaining time are guessed from the last duration of the same operation.</summary>
    public bool IsFromHistory => !HasReports && _expected is not null;

    /// <summary>Takes longer than last time; there is no remaining time to tell then.</summary>
    public bool IsOverdue => IsFromHistory && Elapsed > _expected!.Value;

    /// <summary>The recent pace in units per second (e.g. bytes/s); <c>null</c> while unknown.</summary>
    public double? Rate { get; private set; }

    /// <summary>How long it will still take; <c>null</c> while unknown.</summary>
    public TimeSpan? Remaining
    {
        get
        {
            if (HasReports)
            {
                if (_remainingAtReport is not { } remaining)
                    return null;
                if (remaining == TimeSpan.Zero)
                    return TimeSpan.Zero; // done
                // Counted down without news from borg: stalled, so unknown again rather than "0:00 left".
                var left = remaining - (_time.GetUtcNow() - _reportedAt);
                return left > TimeSpan.Zero ? left : null;
            }
            if (_expected is { } expected && Elapsed <= expected)
                return expected - Elapsed;
            return null;
        }
    }

    /// <summary>How long the same operation took last time: used until real progress arrives.</summary>
    public void Expect(TimeSpan? expected)
    {
        _expected = expected is { Ticks: > 0 } ? expected : null;
        Update();
    }

    /// <summary><paramref name="done"/> of <paramref name="total"/> (any unit: bytes, files, archives, …).</summary>
    public void Report(double done, double total)
    {
        if (total <= 0)
            return;

        var now = _time.GetUtcNow();
        done = Math.Clamp(done, 0, total);
        Fraction = done / total;
        _first ??= (now, done);
        _samples.Enqueue((now, done));
        while (_samples.Count > 2 && now - _samples.Peek().At > RateWindow)
            _samples.Dequeue();

        // The recent pace once it spans a moment; until then (or with sparse reports) the pace since the first report.
        var oldest = _samples.Peek();
        var (fromAt, fromDone) = now - oldest.At >= TimeSpan.FromSeconds(1) ? oldest : _first.Value;
        var seconds = (now - fromAt).TotalSeconds;
        var rate = seconds >= 1 ? (done - fromDone) / seconds : 0;
        Rate = seconds >= 1 ? rate : null;
        _remainingAtReport = done >= total ? TimeSpan.Zero
            : rate > 0 ? TimeSpan.FromSeconds(Math.Ceiling((total - done) / rate))
            : null;
        _reportedAt = now;
    }

    /// <summary>Moves an estimate from history on (call it every second).</summary>
    public void Update()
    {
        if (!HasReports && _expected is { } expected)
            Fraction = IsOverdue ? null : Math.Min(Elapsed / expected, 0.99);
    }
}
