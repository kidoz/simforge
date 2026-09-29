using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace SimForge.Testing;

/// <summary>
/// A runner-neutral scenario. <see cref="Configure"/> runs while the environment is still configurable, and
/// <see cref="ExecuteAsync"/> runs against the initialized environment. Implementations must not keep per-run state
/// in fields, because one instance may be executed repeatedly or in parallel.
/// </summary>
public interface IScenario
{
    ScenarioDescriptor Descriptor { get; }

    /// <summary>Adds resources, fault rules, and services before the environment is initialized.</summary>
    void Configure(ScenarioSetup setup);

    ValueTask ExecuteAsync(ScenarioContext context, CancellationToken cancellationToken);
}

/// <summary>Delegate-based <see cref="IScenario"/>; no inheritance is required.</summary>
public sealed class Scenario : IScenario
{
    private readonly Action<ScenarioSetup>? _configure;
    private readonly Func<ScenarioContext, CancellationToken, ValueTask> _execute;

    public Scenario(ScenarioDescriptor descriptor, Action<ScenarioSetup>? configure, Func<ScenarioContext, CancellationToken, ValueTask> execute)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(execute);
        Descriptor = descriptor;
        _configure = configure;
        _execute = execute;
    }

    public ScenarioDescriptor Descriptor { get; }

    /// <summary>Creates a scenario with no setup step, recording the caller's source location.</summary>
    public static Scenario Create(
        string id,
        Func<ScenarioContext, CancellationToken, ValueTask> execute,
        [CallerFilePath] string filePath = "",
        [CallerLineNumber] int lineNumber = 0) =>
        new(new ScenarioDescriptor(id, source: new ScenarioSourceLocation(filePath, lineNumber)), configure: null, execute);

    /// <summary>Creates a scenario with a setup step, recording the caller's source location.</summary>
    public static Scenario Create(
        string id,
        Action<ScenarioSetup> configure,
        Func<ScenarioContext, CancellationToken, ValueTask> execute,
        [CallerFilePath] string filePath = "",
        [CallerLineNumber] int lineNumber = 0) =>
        new(new ScenarioDescriptor(id, source: new ScenarioSourceLocation(filePath, lineNumber)), configure, execute);

    public void Configure(ScenarioSetup setup) => _configure?.Invoke(setup);

    public ValueTask ExecuteAsync(ScenarioContext context, CancellationToken cancellationToken) => _execute(context, cancellationToken);

    public override string ToString() => Descriptor.ToString();
}

/// <summary>Thrown by <see cref="ScenarioContext.Skip"/> to end a scenario as skipped.</summary>
public sealed class ScenarioSkippedException : Exception
{
    public ScenarioSkippedException(string reason)
        : base(reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
    }
}

/// <summary>Configuration stage of a scenario run. The environment is not yet initialized.</summary>
public sealed class ScenarioSetup
{
    private readonly List<(Type ServiceType, Func<ScenarioContext, object> Factory)> _services = [];
    private bool _sealed;

    internal ScenarioSetup(ScenarioDescriptor descriptor, SimulationEnvironment environment)
    {
        Descriptor = descriptor;
        Environment = environment;
    }

    public ScenarioDescriptor Descriptor { get; }

    /// <summary>The environment in its <see cref="SimulationEnvironmentState.Created"/> stage.</summary>
    public SimulationEnvironment Environment { get; }

    /// <summary>
    /// Registers a per-run service, created lazily from the initialized context at most once per run. Services that
    /// implement <see cref="IAsyncDisposable"/> or <see cref="IDisposable"/> are disposed in reverse creation order,
    /// before the environment. This is a lookup table, not a dependency-injection container.
    /// </summary>
    public ScenarioSetup AddService<TService>(Func<ScenarioContext, TService> factory)
        where TService : class
    {
        ArgumentNullException.ThrowIfNull(factory);
        if (_sealed)
        {
            throw new InvalidOperationException("Services can only be registered during scenario configuration.");
        }

        if (_services.Exists(registration => registration.ServiceType == typeof(TService)))
        {
            throw new ArgumentException($"A service of type {typeof(TService).Name} is already registered.", nameof(factory));
        }

        _services.Add((typeof(TService), context => factory(context)));
        return this;
    }

    internal ScenarioServices Seal()
    {
        _sealed = true;
        return new ScenarioServices(_services);
    }
}

/// <summary>Execution stage of a scenario run: an initialized, isolated environment plus per-run services.</summary>
public sealed class ScenarioContext
{
    private readonly ScenarioServices _services;

    internal ScenarioContext(ScenarioDescriptor descriptor, SimulationEnvironment environment, ScenarioServices services)
    {
        Descriptor = descriptor;
        Environment = environment;
        _services = services;
    }

    public ScenarioDescriptor Descriptor { get; }

    public SimulationEnvironment Environment { get; }

    public int Seed => Environment.Seed;

    /// <summary>Virtual clock; pass it to application code as its <see cref="TimeProvider"/>.</summary>
    public VirtualClock Clock => Environment.Clock;

    public SimulationScheduler Scheduler => Environment.Scheduler;

    public OperationJournal Journal => Environment.Journal;

    public FaultInjector Faults => Environment.Faults;

    public DeterministicIds Ids => Environment.Ids;

    /// <summary>Per-run services registered with <see cref="ScenarioSetup.AddService{TService}"/>, plus the context, environment, and clock.</summary>
    public IServiceProvider Services => _services;

    public TService GetRequiredService<TService>()
        where TService : class =>
        _services.GetService(typeof(TService)) as TService
        ?? throw new InvalidOperationException($"No service of type {typeof(TService).Name} is registered for scenario '{Descriptor.Id}'.");

    public TResource GetResource<TResource>(string name)
        where TResource : class, ISimulationResource =>
        Environment.GetResource<TResource>(name);

    public DiagnosticSnapshot CaptureDiagnostics() => Environment.CaptureDiagnostics();

    /// <summary>Ends the scenario with outcome <see cref="ScenarioOutcome.Skipped"/>.</summary>
    [DoesNotReturn]
    public void Skip(string reason) => throw new ScenarioSkippedException(reason);
}

internal sealed class ScenarioServices : IServiceProvider
{
    private readonly Lock _gate = new();
    private readonly Dictionary<Type, Func<ScenarioContext, object>> _factories;
    private readonly Dictionary<Type, object> _instances = [];
    private readonly List<object> _creationOrder = [];
    private readonly HashSet<Type> _resolving = [];
    private ScenarioContext? _context;
    private bool _disposed;

    public ScenarioServices(IEnumerable<(Type ServiceType, Func<ScenarioContext, object> Factory)> registrations)
    {
        _factories = registrations.ToDictionary(registration => registration.ServiceType, registration => registration.Factory);
    }

    public void Attach(ScenarioContext context) => _context = context;

    public object? GetService(Type serviceType)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        var context = _context ?? throw new InvalidOperationException("Services are available only after the environment is initialized.");
        if (serviceType == typeof(ScenarioContext))
        {
            return context;
        }

        if (serviceType == typeof(SimulationEnvironment))
        {
            return context.Environment;
        }

        if (serviceType == typeof(TimeProvider) || serviceType == typeof(VirtualClock))
        {
            return context.Clock;
        }

        if (serviceType == typeof(IServiceProvider))
        {
            return this;
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_instances.TryGetValue(serviceType, out var existing))
            {
                return existing;
            }

            if (!_factories.TryGetValue(serviceType, out var factory))
            {
                return null;
            }

            if (!_resolving.Add(serviceType))
            {
                throw new InvalidOperationException($"Circular service dependency detected while creating {serviceType.Name}.");
            }

            try
            {
                var instance = factory(context) ?? throw new InvalidOperationException($"The factory for {serviceType.Name} returned null.");
                _instances[serviceType] = instance;
                _creationOrder.Add(instance);
                return instance;
            }
            finally
            {
                _resolving.Remove(serviceType);
            }
        }
    }

    public async Task<List<Exception>> DisposeAsync()
    {
        object[] created;
        lock (_gate)
        {
            _disposed = true;
            created = [.. _creationOrder];
            _creationOrder.Clear();
            _instances.Clear();
        }

        var errors = new List<Exception>();
        foreach (var instance in created.Reverse())
        {
            try
            {
                switch (instance)
                {
                    case IAsyncDisposable asyncDisposable:
                        await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                        break;
                    case IDisposable disposable:
                        disposable.Dispose();
                        break;
                }
            }
            catch (Exception exception)
            {
                errors.Add(exception);
            }
        }

        return errors;
    }
}
