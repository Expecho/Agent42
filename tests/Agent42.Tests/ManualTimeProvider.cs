namespace Agent42.Tests;

/// <summary>A clock that moves only when a test says so, and fires the timers (so <see cref="PeriodicTimer"/>) it created as it passes them.</summary>
public sealed class ManualTimeProvider : TimeProvider
{
    private readonly Lock _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _now = new(2026, 11, 28, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _now;
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        lock (_gate)
        {
            _timers.Add(timer);
            timer.Change(dueTime, period);
        }

        return timer;
    }

    public void Advance(TimeSpan by)
    {
        List<ManualTimer> due = [];
        lock (_gate)
        {
            _now += by;
            foreach (var timer in _timers)
            {
                if (timer.TryFire(_now))
                {
                    due.Add(timer);
                }
            }
        }

        foreach (var timer in due)
        {
            timer.Run();
        }
    }

    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        private DateTimeOffset? _next;
        private TimeSpan _period;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            _next = dueTime == Timeout.InfiniteTimeSpan ? null : owner._now + dueTime;
            _period = period;
            return true;
        }

        public bool TryFire(DateTimeOffset now)
        {
            if (_next is not { } next || next > now)
            {
                return false;
            }

            // One tick per Advance, however far it jumped: a periodic timer coalesces missed ticks.
            _next = _period > TimeSpan.Zero && _period != Timeout.InfiniteTimeSpan ? now + _period : null;
            return true;
        }

        public void Run() => callback(state);

        public void Dispose()
        {
            lock (owner._gate)
            {
                _next = null;
                owner._timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
