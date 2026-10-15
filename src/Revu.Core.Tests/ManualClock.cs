namespace Revu.Core.Tests;

/// <summary>
/// Time that moves only when a test moves it. Event-processing sessions run their
/// callback and finalization deadlines on this clock in tests, so a detector is late
/// because the test said so, never because the runner is slow. Only timestamps and
/// one-shot timers are manual; a timer fires when <see cref="Advance"/> reaches it.
/// </summary>
internal sealed class ManualClock : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private long _now;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() { lock (_gate) return _now; }

    /// <summary>Moves time forward and fires the timers that came due, on the calling thread.</summary>
    public void Advance(TimeSpan by)
    {
        ManualTimer[] due;
        lock (_gate)
        {
            _now += by.Ticks;
            due = _timers.Where(t => t.Due <= _now).OrderBy(t => t.Due).ToArray();
            _timers.RemoveAll(t => t.Due <= _now);
        }
        foreach (var timer in due) timer.Fire();
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    private sealed class ManualTimer(ManualClock clock, TimerCallback callback, object? state) : ITimer
    {
        public long Due;
        public void Fire() => callback(state);

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            if (period != Timeout.InfiniteTimeSpan) throw new NotSupportedException("ManualClock has one-shot timers only");
            lock (clock._gate)
            {
                clock._timers.Remove(this);
                if (dueTime == Timeout.InfiniteTimeSpan) return true;
                Due = clock._now + dueTime.Ticks;
                clock._timers.Add(this);
            }
            return true;
        }

        public void Dispose() { lock (clock._gate) clock._timers.Remove(this); }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
