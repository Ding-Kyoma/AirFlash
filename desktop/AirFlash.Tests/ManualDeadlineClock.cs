using Xunit;

namespace AirFlash.Tests;

// One-shot timers wake only when the fixture advances the monotonic clock.
internal sealed class ManualDeadlineClock : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private long _ticks;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => Interlocked.Read(ref _ticks);
    public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch + TimeSpan.FromTicks(GetTimestamp());
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        lock (_gate)
        {
            var timer = new ManualTimer(this, callback, state);
            _timers.Add(timer); timer.Change(dueTime, period); return timer;
        }
    }
    public void WakeAfter(TimeSpan elapsed, TimeSpan? expectedDue = null)
    {
        ManualTimer timer;
        lock (_gate)
        {
            Interlocked.Add(ref _ticks, elapsed.Ticks);
            timer = Assert.Single(_timers, t => t.Armed);
            if (expectedDue is { } due) Assert.Equal(due, timer.DueTime);
            timer.Armed = false;
        }
        timer.Fire();
    }
    private sealed class ManualTimer(ManualDeadlineClock owner, TimerCallback callback, object? state) : ITimer
    {
        public bool Armed;
        public TimeSpan DueTime;
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            Assert.Equal(Timeout.InfiniteTimeSpan, period);
            lock (owner._gate)
            {
                if (!owner._timers.Contains(this)) return false;
                DueTime = dueTime;
                Armed = dueTime != Timeout.InfiniteTimeSpan; return true;
            }
        }
        public void Fire() => callback(state);
        public void Dispose() { lock (owner._gate) { Armed = false; owner._timers.Remove(this); } }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
