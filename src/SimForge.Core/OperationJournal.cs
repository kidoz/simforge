using System.Collections.Immutable;

namespace SimForge;

/// <summary>Outcome recorded for a journaled operation.</summary>
public enum OperationOutcome
{
    /// <summary>The operation completed.</summary>
    Succeeded,

    /// <summary>The simulated service or the work item reported an error.</summary>
    Failed,

    /// <summary>The operation was rejected before any state changed, for example because it is unsupported.</summary>
    Rejected,

    /// <summary>A fault rule fired at this point.</summary>
    Faulted,

    /// <summary>The operation observed cancellation.</summary>
    Canceled,
}

/// <summary>
/// One immutable journal entry. <see cref="Phase"/> says whether state had changed when the entry was recorded:
/// <see cref="FaultPhases.Before"/> means it had not, and <see cref="FaultPhases.After"/> means it had.
/// </summary>
public sealed record OperationJournalEntry(
    long Sequence,
    string ScenarioId,
    int Seed,
    string Provider,
    string Resource,
    string Operation,
    string Phase,
    DateTimeOffset VirtualTime,
    string? CorrelationId,
    OperationOutcome Outcome,
    string? Target = null,
    string? Details = null,
    string? Error = null,
    string? Payload = null);

/// <summary>
/// Ordered, environment-owned record of provider and scheduler operations. By default it records operation metadata
/// only. Payloads (keys and values) are captured only when <see cref="SimulationEnvironmentOptions.CapturePayloads"/>
/// is enabled.
/// </summary>
public sealed class OperationJournal
{
    private readonly Lock _gate = new();
    private readonly List<OperationJournalEntry> _entries = [];
    private readonly AsyncLocal<string?> _correlation = new();
    private readonly SimulationEnvironment _environment;

    internal OperationJournal(SimulationEnvironment environment)
    {
        _environment = environment;
    }

    public bool CapturesPayloads => _environment.Options.CapturePayloads;

    /// <summary>Correlation ID attached to entries recorded by the current asynchronous flow.</summary>
    public string? CurrentCorrelationId => _correlation.Value;

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>
    /// Sets the correlation ID for entries recorded by the current asynchronous flow until the returned scope is disposed.
    /// Scopes nest; disposing a scope restores the previous ID.
    /// </summary>
    public IDisposable BeginCorrelation(string correlationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
        var previous = _correlation.Value;
        _correlation.Value = correlationId;
        return new CorrelationScope(this, previous);
    }

    /// <summary>
    /// Records an entry at the current virtual time. <paramref name="payload"/> is evaluated only when payload capture is
    /// enabled, so it must not have side effects. <paramref name="correlationId"/>, when not null, is recorded instead of
    /// the ambient <see cref="CurrentCorrelationId"/>.
    /// </summary>
    public OperationJournalEntry Record(
        string provider,
        string resource,
        string operation,
        string phase,
        OperationOutcome outcome,
        string? target = null,
        string? details = null,
        string? error = null,
        Func<string?>? payload = null,
        string? correlationId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(resource);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentException.ThrowIfNullOrWhiteSpace(phase);
        var virtualTime = _environment.Scheduler.Now;
        var capturedPayload = CapturesPayloads && payload is not null ? payload() : null;
        correlationId ??= _correlation.Value;
        lock (_gate)
        {
            var entry = new OperationJournalEntry(
                _entries.Count + 1,
                _environment.ScenarioId,
                _environment.Seed,
                provider,
                resource,
                operation,
                phase,
                virtualTime,
                correlationId,
                outcome,
                target,
                details,
                error,
                capturedPayload);
            _entries.Add(entry);
            return entry;
        }
    }

    /// <summary>Returns an immutable snapshot of all entries in recording order.</summary>
    public ImmutableArray<OperationJournalEntry> GetEntries()
    {
        lock (_gate)
        {
            return [.. _entries];
        }
    }

    private sealed class CorrelationScope(OperationJournal journal, string? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            journal._correlation.Value = previous;
        }
    }
}
