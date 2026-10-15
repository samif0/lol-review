namespace Revu.Sidecar.Tests;

/// <summary>
/// Time that never moves. Event-processing sessions given this clock can never pass a
/// callback or finalization deadline, so their output never depends on runner speed.
/// </summary>
internal sealed class StoppedClock : TimeProvider
{
    public override long GetTimestamp() => 0;
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => new Never();

    private sealed class Never : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => true;
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
