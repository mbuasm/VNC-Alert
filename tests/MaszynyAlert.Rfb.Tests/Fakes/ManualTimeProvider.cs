namespace MaszynyAlert.Rfb.Tests.Fakes;

/// <summary>
/// Zegar przesuwany ręcznie: timery odpalają dopiero w <see cref="Advance"/>, nigdy same z siebie.
/// Test limitu czasu nie ściga się wtedy z obciążeniem runnera.
/// </summary>
public sealed class ManualTimeProvider : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private TimeSpan _elapsed;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>Przesuwa zegar i synchronicznie odpala timery, których termin minął.</summary>
    public void Advance(TimeSpan by)
    {
        List<ManualTimer> due;
        lock (_gate)
        {
            _elapsed += by;
            due = _timers.Where(t => t.DueAt <= _elapsed).ToList();
            foreach (ManualTimer timer in due)
            {
                _timers.Remove(timer);
            }
        }

        foreach (ManualTimer timer in due)
        {
            timer.Fire();
        }
    }

    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        public TimeSpan DueAt { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            if (period != Timeout.InfiniteTimeSpan && period != TimeSpan.Zero)
            {
                throw new NotSupportedException("ManualTimeProvider obsługuje tylko timery jednorazowe.");
            }

            lock (owner._gate)
            {
                owner._timers.Remove(this);
                if (dueTime != Timeout.InfiniteTimeSpan)
                {
                    DueAt = owner._elapsed + dueTime;
                    owner._timers.Add(this);
                }
            }

            return true;
        }

        public void Fire() => callback(state);

        public void Dispose()
        {
            lock (owner._gate)
            {
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
