using System.Collections.Immutable;

namespace SimForge;

/// <summary>A simulated resource (database, broker, ...) owned by one <see cref="SimulationEnvironment"/>.</summary>
/// <remarks>
/// <see cref="IAsyncDisposable.DisposeAsync"/> must tolerate partial or absent initialization, because the environment
/// disposes every resource it owns (in reverse registration order), including one whose initialization failed.
/// </remarks>
public interface ISimulationResource : IAsyncDisposable
{
    /// <summary>Unique name within the environment.</summary>
    string Name { get; }

    /// <summary>Provider identifier, for example <c>postgresql</c>.</summary>
    string Provider { get; }

    /// <summary>Fault points the resource evaluates. Fault rules may target only declared points.</summary>
    IReadOnlyCollection<FaultPoint> FaultPoints { get; }

    ValueTask InitializeAsync(CancellationToken cancellationToken);
}

/// <summary>Lifecycle stages of a <see cref="SimulationEnvironment"/>.</summary>
public enum SimulationEnvironmentState
{
    /// <summary>Configurable: resources and fault rules can be added.</summary>
    Created,
    Initializing,

    /// <summary>Initialized; resources are usable.</summary>
    Ready,

    /// <summary>A resource failed to initialize; started resources were already disposed.</summary>
    InitializationFailed,
    Disposing,
    Disposed,
}

/// <summary>Options fixed when an environment is created.</summary>
public sealed record SimulationEnvironmentOptions
{
    /// <summary>Default virtual start instant. It is fixed so that time-dependent results do not depend on the host clock.</summary>
    public static readonly DateTimeOffset DefaultStartTime = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public string ScenarioId { get; init; } = "ad-hoc";

    public int Seed { get; init; }

    public DateTimeOffset StartTime { get; init; } = DefaultStartTime;

    /// <summary>Bound on work items executed by one scheduler drive; exceeding it reports the pending work.</summary>
    public int MaxSchedulerSteps { get; init; } = 10_000;

    /// <summary>Captures operation payloads (keys and values) in the journal. It is off by default to avoid collecting sensitive data.</summary>
    public bool CapturePayloads { get; init; }

    /// <summary>Real time that disposal waits for scheduled work that is still running before abandoning it.</summary>
    public TimeSpan CleanupTimeout { get; init; } = TimeSpan.FromSeconds(5);

    internal void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ScenarioId, nameof(ScenarioId));
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxSchedulerSteps, 1, nameof(MaxSchedulerSteps));
        ArgumentOutOfRangeException.ThrowIfLessThan(CleanupTimeout, TimeSpan.Zero, nameof(CleanupTimeout));
    }
}

/// <summary>
/// Owns all simulated state of one scenario: resources, virtual time, scheduled work, deterministic IDs, faults, and the
/// journal. Separate environments share no mutable state.
/// </summary>
/// <remarks>
/// <para>Stages: <b>create</b> the environment, then <b>configure</b> it (<see cref="AddResource{TResource}"/> and fault
/// rules), <b>initialize</b> it (<see cref="InitializeAsync"/>, which initializes resources in registration order),
/// <b>execute</b> against it, and <b>dispose</b> it (<see cref="DisposeAsync"/>).</para>
/// <para>If initialization fails partway, the resources that had started initializing are disposed in reverse order
/// and <see cref="SimulationInitializationException"/> reports both the original error and any cleanup errors.</para>
/// <para>Disposal closes the scheduler (no further work starts and pending work is discarded), cancels
/// <see cref="LifetimeToken"/>, and waits up to <see cref="SimulationEnvironmentOptions.CleanupTimeout"/> for scheduled
/// work that is already running. It then disposes resources in reverse registration order. The first disposal call throws <see cref="SimulationCleanupException"/> when anything failed. Repeated or
/// concurrent calls wait for that disposal and never throw. After disposal, resource operations throw
/// <see cref="ObjectDisposedException"/>.</para>
/// </remarks>
public sealed class SimulationEnvironment : IAsyncDisposable
{
    private readonly Lock _gate = new();
    private readonly List<ResourceEntry> _resources = [];
    private readonly CancellationTokenSource _lifetime = new();
    private SimulationEnvironmentState _state = SimulationEnvironmentState.Created;
    private Task? _initialization;
    private Task? _disposal;
    private ImmutableArray<Exception> _cleanupErrors = [];

    public SimulationEnvironment(SimulationEnvironmentOptions? options = null)
    {
        Options = options ?? new SimulationEnvironmentOptions();
        Options.Validate();
        LifetimeToken = _lifetime.Token;
        Scheduler = new SimulationScheduler(this, Options.StartTime.ToUniversalTime(), Options.MaxSchedulerSteps);
        Clock = new VirtualClock(Scheduler);
        Journal = new OperationJournal(this);
        Faults = new FaultInjector(this);
        Ids = new DeterministicIds(Options.Seed);
    }

    public SimulationEnvironmentOptions Options { get; }

    public string ScenarioId => Options.ScenarioId;

    public int Seed => Options.Seed;

    public VirtualClock Clock { get; }

    public SimulationScheduler Scheduler { get; }

    public OperationJournal Journal { get; }

    public FaultInjector Faults { get; }

    public DeterministicIds Ids { get; }

    /// <summary>Canceled when disposal begins. Scheduled work receives a token linked to it.</summary>
    public CancellationToken LifetimeToken { get; }

    public SimulationEnvironmentState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    public IReadOnlyList<ISimulationResource> Resources
    {
        get
        {
            lock (_gate)
            {
                return [.. _resources.Select(entry => entry.Resource)];
            }
        }
    }

    /// <summary>Errors collected by disposal. The list is empty until disposal completes.</summary>
    public ImmutableArray<Exception> CleanupErrors
    {
        get
        {
            lock (_gate)
            {
                return _cleanupErrors;
            }
        }
    }

    /// <summary>Registers a resource. Allowed only before initialization; names must be unique within the environment.</summary>
    public TResource AddResource<TResource>(TResource resource)
        where TResource : class, ISimulationResource
    {
        ArgumentNullException.ThrowIfNull(resource);
        ValidateResourceName(resource.Name);
        lock (_gate)
        {
            ThrowIfDisposedLocked();
            if (_state != SimulationEnvironmentState.Created)
            {
                throw new InvalidOperationException(
                    $"Resource '{resource.Name}' cannot be added in state {_state}; resources can only be added before the environment is initialized.");
            }

            if (_resources.Exists(entry => entry.Resource.Name == resource.Name))
            {
                throw new ArgumentException($"A resource named '{resource.Name}' is already registered in this environment.", nameof(resource));
            }

            _resources.Add(new ResourceEntry(resource));
        }

        return resource;
    }

    public TResource GetResource<TResource>(string name)
        where TResource : class, ISimulationResource
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ISimulationResource? found;
        lock (_gate)
        {
            found = _resources.Find(entry => entry.Resource.Name == name)?.Resource;
        }

        return found switch
        {
            null => throw new KeyNotFoundException($"No resource named '{name}' is registered. Registered: {string.Join(", ", Resources.Select(resource => resource.Name))}."),
            TResource typed => typed,
            _ => throw new InvalidCastException($"Resource '{name}' is a {found.GetType().Name}, not a {typeof(TResource).Name}."),
        };
    }

    /// <summary>Initializes registered resources in registration order. Allowed once, from <see cref="SimulationEnvironmentState.Created"/>.</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ResourceEntry[] resources;
        lock (_gate)
        {
            ThrowIfDisposedLocked();
            if (_state != SimulationEnvironmentState.Created)
            {
                throw new InvalidOperationException($"The environment cannot be initialized in state {_state}; initialization is allowed once.");
            }

            _state = SimulationEnvironmentState.Initializing;
            _initialization = completion.Task;
            resources = [.. _resources];
        }

        try
        {
            await InitializeCoreAsync(resources, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            completion.TrySetResult();
        }
    }

    /// <summary>Throws unless the environment is <see cref="SimulationEnvironmentState.Ready"/>. Providers call it before every operation.</summary>
    public void ThrowIfNotReady()
    {
        var state = State;
        if (state is SimulationEnvironmentState.Disposing or SimulationEnvironmentState.Disposed)
        {
            throw new ObjectDisposedException(nameof(SimulationEnvironment), $"The environment of scenario '{ScenarioId}' has been disposed.");
        }

        if (state != SimulationEnvironmentState.Ready)
        {
            throw new InvalidOperationException($"The environment of scenario '{ScenarioId}' is not ready (state: {state}). Initialize it before using its resources.");
        }
    }

    /// <summary>Throws <see cref="ObjectDisposedException"/> once disposal has begun.</summary>
    public void ThrowIfDisposed()
    {
        lock (_gate)
        {
            ThrowIfDisposedLocked();
        }
    }

    /// <summary>Captures an immutable diagnostic snapshot: journal, pending work, and fault reports.</summary>
    public DiagnosticSnapshot CaptureDiagnostics() => new(
        ScenarioId,
        Seed,
        Scheduler.Now,
        State,
        Journal.GetEntries(),
        Scheduler.GetPendingWork(),
        Faults.GetFiredFaults(),
        Faults.GetUnfiredFaults());

    public async ValueTask DisposeAsync()
    {
        if (Scheduler.IsExecutingWork)
        {
            throw new InvalidOperationException("An environment cannot be disposed from inside its own scheduled work.");
        }

        TaskCompletionSource? owner = null;
        Task disposal;
        lock (_gate)
        {
            if (_disposal is null)
            {
                owner = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _disposal = owner.Task;
                _state = SimulationEnvironmentState.Disposing;
            }

            disposal = _disposal;
        }

        if (owner is null)
        {
            await disposal.ConfigureAwait(false);
            return;
        }

        List<Exception> errors;
        try
        {
            errors = await DisposeCoreAsync().ConfigureAwait(false);
            lock (_gate)
            {
                _state = SimulationEnvironmentState.Disposed;
                _cleanupErrors = [.. errors];
            }
        }
        finally
        {
            owner.TrySetResult();
        }

        if (errors.Count > 0)
        {
            throw new SimulationCleanupException(errors);
        }
    }

    private async Task InitializeCoreAsync(ResourceEntry[] resources, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, LifetimeToken);
        string? failedResource = null;
        Exception? failure = null;
        foreach (var entry in resources)
        {
            if (linked.IsCancellationRequested)
            {
                failure = new OperationCanceledException("Environment initialization was canceled.", linked.Token);
                break;
            }

            try
            {
                entry.InitializationStarted = true;
                await entry.Resource.InitializeAsync(linked.Token).ConfigureAwait(false);
                Journal.Record(entry.Resource.Provider, entry.Resource.Name, "initialize", FaultPhases.After, OperationOutcome.Succeeded);
            }
            catch (Exception exception)
            {
                var outcome = exception is OperationCanceledException ? OperationOutcome.Canceled : OperationOutcome.Failed;
                Journal.Record(entry.Resource.Provider, entry.Resource.Name, "initialize", FaultPhases.Before, outcome, error: exception.Message);
                failedResource = entry.Resource.Name;
                failure = exception;
                break;
            }
        }

        if (failure is null)
        {
            lock (_gate)
            {
                if (_state == SimulationEnvironmentState.Initializing)
                {
                    _state = SimulationEnvironmentState.Ready;
                    return;
                }
            }

            throw new ObjectDisposedException(nameof(SimulationEnvironment), "The environment was disposed while it was initializing.");
        }

        var cleanupErrors = await DisposeResourcesAsync(resources.Where(entry => entry.InitializationStarted)).ConfigureAwait(false);
        lock (_gate)
        {
            if (_state == SimulationEnvironmentState.Initializing)
            {
                _state = SimulationEnvironmentState.InitializationFailed;
            }
        }

        throw new SimulationInitializationException(failedResource, failure, cleanupErrors);
    }

    private async Task<List<Exception>> DisposeCoreAsync()
    {
        var errors = new List<Exception>();

        // Close the scheduler first so that no further work starts; the item that may already be running is awaited below.
        var discarded = Scheduler.Close();
        if (discarded.Length > 0)
        {
            Journal.Record("simforge", "scheduler", "discard", FaultPhases.After, OperationOutcome.Succeeded, details: $"{discarded.Length} pending work item(s) discarded at disposal");
        }

        try
        {
            await _lifetime.CancelAsync().ConfigureAwait(false);
        }
        catch (AggregateException exception)
        {
            errors.AddRange(exception.InnerExceptions);
        }

        Task? initialization;
        ResourceEntry[] resources;
        lock (_gate)
        {
            initialization = _initialization;
            resources = [.. _resources];
        }

        if (initialization is not null)
        {
            await initialization.ConfigureAwait(false);
        }

        if (Scheduler.GetActiveWork() is { } active)
        {
            var delay = Task.Delay(Options.CleanupTimeout, TimeProvider.System);
            if (await Task.WhenAny(active.Task, delay).ConfigureAwait(false) != active.Task)
            {
                errors.Add(new SimulationWorkAbandonedException(active.Description, Options.CleanupTimeout));
            }
        }

        errors.AddRange(await DisposeResourcesAsync(resources).ConfigureAwait(false));
        return errors;
    }

    private async Task<List<Exception>> DisposeResourcesAsync(IEnumerable<ResourceEntry> resources)
    {
        var errors = new List<Exception>();
        foreach (var entry in resources.Reverse())
        {
            lock (_gate)
            {
                if (entry.Disposed)
                {
                    continue;
                }

                entry.Disposed = true;
            }

            try
            {
                await entry.Resource.DisposeAsync().ConfigureAwait(false);
                Journal.Record(entry.Resource.Provider, entry.Resource.Name, "dispose", FaultPhases.After, OperationOutcome.Succeeded);
            }
            catch (Exception exception)
            {
                Journal.Record(entry.Resource.Provider, entry.Resource.Name, "dispose", FaultPhases.After, OperationOutcome.Failed, error: exception.Message);
                errors.Add(new ResourceCleanupException(entry.Resource.Name, exception));
            }
        }

        return errors;
    }

    private void ThrowIfDisposedLocked()
    {
        if (_state is SimulationEnvironmentState.Disposing or SimulationEnvironmentState.Disposed)
        {
            throw new ObjectDisposedException(nameof(SimulationEnvironment), $"The environment of scenario '{ScenarioId}' has been disposed.");
        }
    }

    private static void ValidateResourceName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Length > 128 || !name.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.'))
        {
            throw new ArgumentException($"Resource name '{name}' must be 1-128 ASCII letters, digits, '-', '_' or '.'.", nameof(name));
        }
    }

    private sealed class ResourceEntry(ISimulationResource resource)
    {
        public ISimulationResource Resource { get; } = resource;

        public bool InitializationStarted { get; set; }

        public bool Disposed { get; set; }
    }
}
