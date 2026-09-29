using System.Runtime.CompilerServices;

namespace SimForge;

/// <summary>
/// <see cref="TimeProvider"/> backed by an environment's <see cref="SimulationScheduler"/>. Pass it to application code
/// so that <c>GetUtcNow</c>, <c>Task.Delay(TimeSpan, TimeProvider)</c>, timers, and timed cancellation follow virtual time.
/// </summary>
/// <remarks>
/// Timer callbacks run as scheduled work when the scheduler is driven, never on background threads. Continuations of
/// awaited delays may still resume on the thread pool, as with any awaited task. The local time zone is UTC so that
/// results do not depend on the host machine.
/// </remarks>
public sealed class VirtualClock : TimeProvider
{
    private readonly SimulationScheduler _scheduler;
    private readonly DateTimeOffset _origin;

    internal VirtualClock(SimulationScheduler scheduler)
    {
        _scheduler = scheduler;
        _origin = scheduler.Now;
    }

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override DateTimeOffset GetUtcNow() => _scheduler.Now;

    public override long GetTimestamp() => (_scheduler.Now - _origin).Ticks;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
        _scheduler.CreateTimer(callback, state, dueTime, period);
}

/// <summary>Timer whose callbacks are scheduler work items. Periodic occurrences are scheduled from the previous due time, so they do not drift.</summary>
internal sealed class VirtualTimer : ITimer
{
    private readonly Lock _gate = new();
    private readonly SimulationScheduler _scheduler;
    private readonly TimerCallback _callback;
    private readonly object? _state;
    private ScheduledWork? _next;
    private TimeSpan _period = Timeout.InfiniteTimeSpan;
    private bool _disposed;

    public VirtualTimer(SimulationScheduler scheduler, TimerCallback callback, object? state)
    {
        _scheduler = scheduler;
        _callback = callback;
        _state = state;
    }

    public bool Change(TimeSpan dueTime, TimeSpan period)
    {
        ValidateInterval(dueTime, nameof(dueTime));
        ValidateInterval(period, nameof(period));
        lock (_gate)
        {
            if (_disposed)
            {
                return false;
            }

            _next?.Cancel();
            _next = null;
            _period = period;
            if (dueTime != Timeout.InfiniteTimeSpan)
            {
                ScheduleLocked(_scheduler.Now + dueTime);
            }

            return true;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _next?.Cancel();
            _next = null;
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private void ScheduleLocked(DateTimeOffset dueTime)
    {
        // The holder is assigned under _gate, and Fire reads it under _gate, so a concurrent driver cannot observe it unset.
        var holder = new StrongBox<ScheduledWork?>();
        holder.Value = _scheduler.ScheduleTimerAt(dueTime, _ =>
        {
            Fire(holder);
            return ValueTask.CompletedTask;
        });
        _next = holder.Value;
    }

    private void Fire(StrongBox<ScheduledWork?> holder)
    {
        lock (_gate)
        {
            var occurrence = holder.Value;
            if (_disposed || occurrence is null || !ReferenceEquals(occurrence, _next))
            {
                return;
            }

            _next = null;
            if (_period > TimeSpan.Zero && _period != Timeout.InfiniteTimeSpan)
            {
                ScheduleLocked(occurrence.DueTime + _period);
            }
        }

        _callback(_state);
    }

    private static void ValidateInterval(TimeSpan value, string parameterName)
    {
        if (value < TimeSpan.Zero && value != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(parameterName, value, "The interval must be non-negative or Timeout.InfiniteTimeSpan.");
        }
    }
}
