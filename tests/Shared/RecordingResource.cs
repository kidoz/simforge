using SimForge;

namespace SimForge.Tests.Shared;

/// <summary>Thread-safe ordered event log shared by test resources.</summary>
internal sealed class EventLog
{
    private readonly Lock _gate = new();
    private readonly List<string> _events = [];

    public void Add(string entry)
    {
        lock (_gate)
        {
            _events.Add(entry);
        }
    }

    public string[] Snapshot()
    {
        lock (_gate)
        {
            return [.. _events];
        }
    }
}

/// <summary>Test resource that records lifecycle calls and can fail on demand.</summary>
internal sealed class RecordingResource(string name, EventLog? log = null, string provider = RecordingResource.ProviderName) : ISimulationResource
{
    public const string ProviderName = "test";
    public const string Operation = "op";

    private int _disposeCount;

    public string Name { get; } = name;

    public string Provider { get; } = provider;

    public EventLog Log { get; } = log ?? new EventLog();

    public Exception? InitializeError { get; init; }

    public Exception? DisposeError { get; init; }

    public Func<CancellationToken, ValueTask>? OnInitialize { get; init; }

    public IReadOnlyCollection<FaultPoint> FaultPoints { get; init; } =
    [
        new(ProviderName, Operation, FaultPhases.Before),
        new(ProviderName, Operation, FaultPhases.After),
    ];

    public int DisposeCount => Volatile.Read(ref _disposeCount);

    public async ValueTask InitializeAsync(CancellationToken cancellationToken)
    {
        Log.Add($"init:{Name}");
        if (OnInitialize is not null)
        {
            await OnInitialize(cancellationToken);
        }

        if (InitializeError is not null)
        {
            throw InitializeError;
        }
    }

    public ValueTask DisposeAsync()
    {
        Interlocked.Increment(ref _disposeCount);
        Log.Add($"dispose:{Name}");
        return DisposeError is null ? ValueTask.CompletedTask : ValueTask.FromException(DisposeError);
    }
}
