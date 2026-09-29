using System.Collections.Immutable;
using System.Globalization;

namespace SimForge;

/// <summary>Lifecycle of one scheduled work item.</summary>
public enum ScheduledWorkStatus
{
    Pending,
    Running,
    Completed,
    Failed,
    Canceled,
    Discarded,
}

/// <summary>Immutable description of work that has not run yet.</summary>
public readonly record struct PendingWork(string Name, DateTimeOffset DueTime, long Sequence)
{
    public override string ToString() =>
        $"#{Sequence} '{Name}' due {DueTime.ToString("O", CultureInfo.InvariantCulture)}";
}

/// <summary>Handle to work queued on a <see cref="SimulationScheduler"/>.</summary>
public sealed class ScheduledWork
{
    private readonly SimulationScheduler _scheduler;
    private int _status;

    internal ScheduledWork(SimulationScheduler scheduler, string name, DateTimeOffset dueTime, long sequence, Func<CancellationToken, ValueTask> callback)
    {
        _scheduler = scheduler;
        Name = name;
        DueTime = dueTime;
        Sequence = sequence;
        Callback = callback;
    }

    public string Name { get; }

    public DateTimeOffset DueTime { get; }

    /// <summary>Stable insertion sequence; it orders work that is due at the same virtual instant.</summary>
    public long Sequence { get; }

    public ScheduledWorkStatus Status
    {
        get => (ScheduledWorkStatus)Volatile.Read(ref _status);
        internal set => Volatile.Write(ref _status, (int)value);
    }

    internal Func<CancellationToken, ValueTask> Callback { get; }

    /// <summary>Cancels the work if it has not started. Returns false when it already ran, is running, or was canceled.</summary>
    public bool Cancel() => _scheduler.Cancel(this);

    public override string ToString() => new PendingWork(Name, DueTime, Sequence).ToString();
}

/// <summary>
/// Environment-owned deterministic scheduler and the source of virtual time.
/// </summary>
/// <remarks>
/// <para>Work is ordered by due time, then by insertion sequence. Work runs only while a caller drives the scheduler
/// through <see cref="RunNextAsync"/>, <see cref="RunUntilIdleAsync"/>, <see cref="AdvanceByAsync"/>, or
/// <see cref="AdvanceToAsync"/>. There are no background workers. One caller drives at a time, and scheduled work
/// cannot drive the scheduler re-entrantly.</para>
/// <para>Virtual time changes only through the advance operations and never moves backwards. Determinism covers work
/// scheduled here. It does not cover <c>Task.Run</c>, thread-pool continuations, real clocks, or external I/O started
/// by application code.</para>
/// </remarks>
public sealed class SimulationScheduler
{
    private const string JournalProvider = "simforge";
    private const string JournalResource = "scheduler";

    private readonly Lock _gate = new();
    private readonly SortedSet<ScheduledWork> _pending = new(WorkOrder.Instance);
    private readonly AsyncLocal<bool> _insideWork = new();
    private readonly SimulationEnvironment _environment;
    private DateTimeOffset _now;
    private long _nextSequence;
    private bool _driving;
    private bool _closed;
    private ScheduledWork? _activeItem;
    private Task? _activeWork;

    internal SimulationScheduler(SimulationEnvironment environment, DateTimeOffset startTime, int maxStepsPerDrive)
    {
        _environment = environment;
        _now = startTime;
        MaxStepsPerDrive = maxStepsPerDrive;
    }

    /// <summary>Current virtual instant (UTC).</summary>
    public DateTimeOffset Now
    {
        get
        {
            lock (_gate)
            {
                return _now;
            }
        }
    }

    /// <summary>Maximum number of work items one drive operation executes before failing with <see cref="SimulationLimitExceededException"/>.</summary>
    public int MaxStepsPerDrive { get; }

    public int PendingCount
    {
        get
        {
            lock (_gate)
            {
                return _pending.Count;
            }
        }
    }

    /// <summary>True when the calling asynchronous flow is currently executing scheduled work.</summary>
    public bool IsExecutingWork => _insideWork.Value;

    public ImmutableArray<PendingWork> GetPendingWork()
    {
        lock (_gate)
        {
            return SnapshotPending();
        }
    }

    /// <summary>Queues work to run <paramref name="delay"/> after the current virtual instant.</summary>
    public ScheduledWork Schedule(string name, TimeSpan delay, Func<CancellationToken, ValueTask> work)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(delay, TimeSpan.Zero);
        lock (_gate)
        {
            return ScheduleLocked(name, _now + delay, work);
        }
    }

    /// <summary>Queues synchronous work to run <paramref name="delay"/> after the current virtual instant.</summary>
    public ScheduledWork Schedule(string name, TimeSpan delay, Action work)
    {
        ArgumentNullException.ThrowIfNull(work);
        return Schedule(name, delay, _ =>
        {
            work();
            return ValueTask.CompletedTask;
        });
    }

    /// <summary>Queues work for an absolute virtual instant, which must not be earlier than <see cref="Now"/>.</summary>
    public ScheduledWork ScheduleAt(string name, DateTimeOffset dueTime, Func<CancellationToken, ValueTask> work)
    {
        lock (_gate)
        {
            if (dueTime < _now)
            {
                throw new ArgumentOutOfRangeException(nameof(dueTime), dueTime, $"Work cannot be scheduled before the current virtual time {_now:O}.");
            }

            return ScheduleLocked(name, dueTime.ToUniversalTime(), work);
        }
    }

    /// <summary>
    /// Runs exactly one work item that is due at or before the current instant. It never advances time.
    /// Returns false when no work is runnable now.
    /// </summary>
    public async ValueTask<bool> RunNextAsync(CancellationToken cancellationToken = default) =>
        await DriveAsync(target: null, single: true, cancellationToken).ConfigureAwait(false) == 1;

    /// <summary>
    /// Runs work due at or before the current instant, including work that it schedules for the same instant,
    /// until none remains. It never advances time. Returns the number of items executed.
    /// </summary>
    public ValueTask<int> RunUntilIdleAsync(CancellationToken cancellationToken = default) =>
        DriveAsync(target: null, single: false, cancellationToken);

    /// <summary>
    /// Advances virtual time by <paramref name="delta"/>. Each work item due within the window runs at its own due
    /// instant, in due-time/sequence order. That includes work scheduled during the advance, whether for the same
    /// instant or for a later instant still inside the window. Canceled work never runs. Time then rests at the
    /// target. Returns the number of items executed.
    /// </summary>
    public ValueTask<int> AdvanceByAsync(TimeSpan delta, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(delta, TimeSpan.Zero);
        DateTimeOffset target;
        lock (_gate)
        {
            target = _now + delta;
        }

        return DriveAsync(target, single: false, cancellationToken);
    }

    /// <summary>Advances virtual time to <paramref name="target"/> with the same rules as <see cref="AdvanceByAsync"/>.</summary>
    public ValueTask<int> AdvanceToAsync(DateTimeOffset target, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (target < _now)
            {
                throw new ArgumentOutOfRangeException(nameof(target), target, $"Virtual time cannot move backwards from {_now:O}.");
            }
        }

        return DriveAsync(target.ToUniversalTime(), single: false, cancellationToken);
    }

    internal ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var timer = new VirtualTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    internal ScheduledWork ScheduleTimerAt(DateTimeOffset dueTime, Func<CancellationToken, ValueTask> work)
    {
        lock (_gate)
        {
            return ScheduleLocked("timer", dueTime < _now ? _now : dueTime, work);
        }
    }

    internal bool Cancel(ScheduledWork work)
    {
        lock (_gate)
        {
            if (work.Status != ScheduledWorkStatus.Pending || !_pending.Remove(work))
            {
                return false;
            }

            work.Status = ScheduledWorkStatus.Canceled;
            return true;
        }
    }

    /// <summary>Returns the task of the work item currently executing, if any, for bounded waiting during disposal.</summary>
    internal (Task Task, string Description)? GetActiveWork()
    {
        lock (_gate)
        {
            return _activeWork is null || _activeItem is null
                ? null
                : (_activeWork, $"Scheduled work {_activeItem}");
        }
    }

    /// <summary>
    /// Closes the scheduler at environment disposal: no further work starts, and work that has not started is discarded.
    /// Work that is already running is reported by <see cref="GetActiveWork"/> so that disposal can wait for it.
    /// </summary>
    internal ImmutableArray<PendingWork> Close()
    {
        lock (_gate)
        {
            _closed = true;
            var discarded = SnapshotPending();
            foreach (var work in _pending)
            {
                work.Status = ScheduledWorkStatus.Discarded;
            }

            _pending.Clear();
            return discarded;
        }
    }

    private ScheduledWork ScheduleLocked(string name, DateTimeOffset dueTime, Func<CancellationToken, ValueTask> work)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(work);
        ThrowIfClosed();
        var item = new ScheduledWork(this, name, dueTime, ++_nextSequence, work);
        _pending.Add(item);
        return item;
    }

    private async ValueTask<int> DriveAsync(DateTimeOffset? target, bool single, CancellationToken cancellationToken)
    {
        if (_insideWork.Value)
        {
            throw new InvalidOperationException("Scheduled work cannot drive the scheduler re-entrantly; schedule follow-up work instead.");
        }

        lock (_gate)
        {
            ThrowIfClosed();
            if (_driving)
            {
                throw new InvalidOperationException("The scheduler is already being driven by another caller; only one driver is supported.");
            }

            _driving = true;
        }

        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _environment.LifetimeToken);
            var steps = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ScheduledWork item;
                TaskCompletionSource completion;
                lock (_gate)
                {
                    ThrowIfClosed();
                    var limit = target ?? _now;
                    if (_pending.Count == 0 || _pending.Min!.DueTime > limit)
                    {
                        if (target is { } advanceTo && advanceTo > _now)
                        {
                            _now = advanceTo;
                        }

                        return steps;
                    }

                    if (steps >= MaxStepsPerDrive)
                    {
                        throw new SimulationLimitExceededException(MaxStepsPerDrive, _now, SnapshotPending());
                    }

                    item = _pending.Min;
                    _pending.Remove(item);
                    if (item.DueTime > _now)
                    {
                        _now = item.DueTime;
                    }

                    item.Status = ScheduledWorkStatus.Running;
                    // Published under the same lock that selected the item, so disposal never observes a started item as idle.
                    completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    _activeItem = item;
                    _activeWork = completion.Task;
                }

                steps++;
                await ExecuteAsync(item, completion, linked.Token).ConfigureAwait(false);
                if (single)
                {
                    return steps;
                }
            }
        }
        finally
        {
            lock (_gate)
            {
                _driving = false;
                _activeItem = null;
            }
        }
    }

    private async Task ExecuteAsync(ScheduledWork item, TaskCompletionSource completion, CancellationToken cancellationToken)
    {
        _insideWork.Value = true;
        var details = $"sequence={item.Sequence.ToString(CultureInfo.InvariantCulture)}";
        try
        {
            await item.Callback(cancellationToken).ConfigureAwait(false);
            item.Status = ScheduledWorkStatus.Completed;
            _environment.Journal.Record(JournalProvider, JournalResource, item.Name, FaultPhases.After, OperationOutcome.Succeeded, details: details);
        }
        catch (Exception exception)
        {
            item.Status = ScheduledWorkStatus.Failed;
            var outcome = exception is OperationCanceledException && cancellationToken.IsCancellationRequested
                ? OperationOutcome.Canceled
                : OperationOutcome.Failed;
            _environment.Journal.Record(JournalProvider, JournalResource, item.Name, FaultPhases.After, outcome, details: details, error: exception.Message);
            throw;
        }
        finally
        {
            _insideWork.Value = false;
            lock (_gate)
            {
                _activeWork = null;
                _activeItem = null;
            }

            completion.TrySetResult();
        }
    }

    private ImmutableArray<PendingWork> SnapshotPending() =>
        [.. _pending.Select(work => new PendingWork(work.Name, work.DueTime, work.Sequence))];

    private void ThrowIfClosed()
    {
        if (_closed)
        {
            throw new ObjectDisposedException(nameof(SimulationScheduler), $"The scheduler of scenario '{_environment.ScenarioId}' was closed when its environment was disposed.");
        }
    }

    private sealed class WorkOrder : IComparer<ScheduledWork>
    {
        public static readonly WorkOrder Instance = new();

        public int Compare(ScheduledWork? x, ScheduledWork? y)
        {
            if (ReferenceEquals(x, y))
            {
                return 0;
            }

            ArgumentNullException.ThrowIfNull(x);
            ArgumentNullException.ThrowIfNull(y);
            var byDue = x.DueTime.UtcTicks.CompareTo(y.DueTime.UtcTicks);
            return byDue != 0 ? byDue : x.Sequence.CompareTo(y.Sequence);
        }
    }
}
