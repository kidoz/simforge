# SimForge.Core

Namespace `SimForge`.

- [SimulationEnvironment](#simulationenvironment)
- [ISimulationResource](#isimulationresource)
- [SimulationScheduler](#simulationscheduler)
- [VirtualClock](#virtualclock)
- [DeterministicIds](#deterministicids)
- [FaultInjector](#faultinjector)
- [OperationJournal](#operationjournal)
- [DiagnosticSnapshot](#diagnosticsnapshot)
- [Exceptions](#exceptions)

Capability manifest types (`CapabilityManifest`, `Capability`, `CapabilityStatus`, `CompatibilityLevel`) are described in
[capabilities](capabilities.md).

## SimulationEnvironment

Owns all simulated state of one scenario: resources, virtual time, scheduled work, deterministic IDs, faults, and the
journal. Environments share no mutable state.

```csharp
public SimulationEnvironment(SimulationEnvironmentOptions? options = null)
```

### SimulationEnvironmentOptions

| Property | Type | Default | Description |
|---|---|---|---|
| `ScenarioId` | `string` | `"ad-hoc"` | Recorded in every journal entry. Must not be blank. |
| `Seed` | `int` | `0` | Seed for `DeterministicIds`. |
| `StartTime` | `DateTimeOffset` | `DefaultStartTime` (2026-01-01T00:00:00Z) | Initial virtual time, converted to UTC. |
| `MaxSchedulerSteps` | `int` | `10_000` | Maximum work items per scheduler drive. Must be at least 1. |
| `CapturePayloads` | `bool` | `false` | Records operation payloads (keys and values) in the journal. |
| `CleanupTimeout` | `TimeSpan` | 5 s | Real time that disposal waits for running scheduled work. Must not be negative. |

### Members

| Member | Description |
|---|---|
| `Options`, `ScenarioId`, `Seed` | Values fixed at construction. |
| `State` | Current `SimulationEnvironmentState`. |
| `Clock` | The `VirtualClock`. |
| `Scheduler` | The `SimulationScheduler`. |
| `Journal` | The `OperationJournal`. |
| `Faults` | The `FaultInjector`. |
| `Ids` | The `DeterministicIds`. |
| `LifetimeToken` | Canceled when disposal begins. Scheduled work receives a token linked to it. |
| `Resources` | Registered resources, in registration order. |
| `CleanupErrors` | Errors collected by disposal; empty until disposal completes. |
| `AddResource<TResource>(resource)` | Registers a resource. Allowed only in `Created`. Resource names are unique per environment, contain 1–128 ASCII letters, digits, `-`, `_`, or `.`, and are compared ordinally. |
| `GetResource<TResource>(name)` | Returns a registered resource. Throws `KeyNotFoundException` for an unknown name and `InvalidCastException` for a type mismatch. |
| `InitializeAsync(cancellationToken)` | Initializes resources in registration order. Allowed once, from `Created`. |
| `ThrowIfNotReady()` | Throws unless the state is `Ready`. Providers call it before every operation. |
| `ThrowIfDisposed()` | Throws `ObjectDisposedException` once disposal has begun. |
| `CaptureDiagnostics()` | Returns a `DiagnosticSnapshot`. |
| `DisposeAsync()` | Disposes the environment. |

### SimulationEnvironmentState

| Value | Meaning |
|---|---|
| `Created` | Configurable: resources and fault rules can be added. |
| `Initializing` | `InitializeAsync` is running. |
| `Ready` | Resources are usable. |
| `InitializationFailed` | A resource failed to initialize; resources whose initialization started were disposed. |
| `Disposing` | Disposal is running. |
| `Disposed` | Disposal has completed. |

### Lifecycle behavior

| Situation | Behavior |
|---|---|
| A resource fails to initialize | Resources whose initialization started, including the failing one, are disposed in reverse order. `SimulationInitializationException` carries the original error as `InnerException`, the resource name, and every cleanup error. The state becomes `InitializationFailed`. |
| Initialization is canceled | Same as a failure. The inner error is `OperationCanceledException` and `ResourceName` is null. |
| Disposal | 1. Closes the scheduler; pending work is discarded and journaled, and no further work starts. 2. Cancels `LifetimeToken`. 3. Waits up to `CleanupTimeout` for scheduled work that is already running. 4. Disposes every resource in reverse registration order and collects every error. |
| Disposal errors | The first `DisposeAsync` call throws `SimulationCleanupException` with all errors. The errors are also kept in `CleanupErrors`. |
| Repeated or concurrent disposal | Waits for the first disposal; never throws and never disposes a resource twice. |
| Running work ignores cancellation | Reported as `SimulationWorkAbandonedException` after `CleanupTimeout`. The work is not stopped. |
| Disposal from inside scheduled work | Throws `InvalidOperationException`. |
| Use before initialization | `ThrowIfNotReady` throws `InvalidOperationException`. |
| Use after disposal | The environment, scheduler, and providers throw `ObjectDisposedException`. |

## ISimulationResource

```csharp
public interface ISimulationResource : IAsyncDisposable
{
    string Name { get; }
    string Provider { get; }
    IReadOnlyCollection<FaultPoint> FaultPoints { get; }
    ValueTask InitializeAsync(CancellationToken cancellationToken);
}
```

| Member | Contract |
|---|---|
| `Name` | Unique within the environment. |
| `Provider` | Provider identifier, for example `postgresql`. |
| `FaultPoints` | Fault points the resource evaluates. Fault rules may target only declared points. |
| `InitializeAsync` | Called once, in registration order. |
| `DisposeAsync` | Must tolerate partial or absent initialization. The environment disposes every resource it owns, including one whose initialization failed. |

## SimulationScheduler

Holds virtual time and runs scheduled work. Work runs only while a caller drives the scheduler.

| Member | Description |
|---|---|
| `Now` | Current virtual instant (UTC). |
| `PendingCount` | Number of work items not yet started. |
| `MaxStepsPerDrive` | Value of `SimulationEnvironmentOptions.MaxSchedulerSteps`. |
| `IsExecutingWork` | True when the calling flow is executing scheduled work. |
| `GetPendingWork()` | Immutable list of `PendingWork(Name, DueTime, Sequence)` in execution order. |
| `Schedule(name, delay, work)` | Queues work due at `Now + delay`. `work` is `Func<CancellationToken, ValueTask>` or `Action`. A negative delay throws `ArgumentOutOfRangeException`. |
| `ScheduleAt(name, dueTime, work)` | Queues work at an absolute instant. An instant earlier than `Now` throws `ArgumentOutOfRangeException`. |
| `RunNextAsync(cancellationToken)` | Runs exactly one item due at or before `Now`. Returns false when none is runnable. Never advances time. |
| `RunUntilIdleAsync(cancellationToken)` | Runs items due at or before `Now`, including items they schedule for `Now`, until none remain. Returns the number executed. Never advances time. |
| `AdvanceByAsync(delta, cancellationToken)` | Advances time by `delta` (not negative) and runs the work due within the window. Returns the number executed. |
| `AdvanceToAsync(target, cancellationToken)` | Same as `AdvanceByAsync`, with an absolute target that must not be earlier than `Now`. |

Execution rules:

- Work is ordered by due time, then by insertion sequence.
- During an advance, the clock is set to each item's due time before the item runs. Work scheduled during the advance runs if its due time falls inside the window. Time then rests at the target.
- One caller drives at a time. A second concurrent driver throws `InvalidOperationException`, and so does a driver called from inside scheduled work.
- A drive that has executed `MaxStepsPerDrive` items while runnable work remains throws `SimulationLimitExceededException`, listing the pending work.
- An exception thrown by work propagates to the driver unchanged. The item becomes `Failed`, and later items stay pending.
- Every executed item is journaled with provider `simforge`, resource `scheduler`, and the item's name as the operation.

### ScheduledWork

| Member | Description |
|---|---|
| `Name`, `DueTime`, `Sequence` | Values fixed at scheduling. |
| `Status` | `Pending`, `Running`, `Completed`, `Failed`, `Canceled`, or `Discarded` (dropped at disposal). |
| `Cancel()` | Cancels pending work. Returns false once the work has started, completed, or been canceled. |

## VirtualClock

`VirtualClock : TimeProvider`. Reads time from the environment's scheduler.

| Member | Value |
|---|---|
| `GetUtcNow()` | `Scheduler.Now`. |
| `GetTimestamp()` | Virtual ticks elapsed since the environment was created. |
| `TimestampFrequency` | `TimeSpan.TicksPerSecond`. |
| `LocalTimeZone` | `TimeZoneInfo.Utc`. |
| `CreateTimer(callback, state, dueTime, period)` | A timer whose callbacks run as scheduled work named `timer`. `Timeout.InfiniteTimeSpan` as `dueTime` leaves the timer unarmed. A period that is zero or `Timeout.InfiniteTimeSpan` makes a one-shot timer. Periodic occurrences are scheduled from the previous due time. `Change` after `Dispose` returns false. |

`Task.Delay(TimeSpan, TimeProvider)`, `CancellationTokenSource(TimeSpan, TimeProvider)`, and `PeriodicTimer` work through
`CreateTimer`.

## DeterministicIds

| Member | Description |
|---|---|
| `NewGuid()` | Next GUID of the environment's sequence: an RFC 9562 version 8 UUID derived from the seed. The same seed yields the same sequence. |
| `NextValue(sequenceName)` | Next value of a named counter, starting at 1. Counters are independent. |

## FaultInjector

| Member | Description |
|---|---|
| `Add(FaultRule rule)` | Registers a one-shot rule and returns its `ActiveFault`. |
| `Rules` | All registered rules. |
| `GetFiredFaults()` | Reports of fired rules, in firing order. |
| `GetUnfiredFaults()` | `UnfiredFault(RuleId, Rule, Matches)` for every rule that has not fired. |
| `Evaluate(resource, operation, phase, stateChanged)` | Called by providers. Returns the `FaultReport` of the rule that fired, or null. |
| `ThrowIfTriggered(resource, operation, phase, stateChanged)` | Evaluates and throws `SimulatedFaultException` when a rule fires. |

### FaultRule

| Property | Required | Default | Description |
|---|---|---|---|
| `Provider` | yes | | Provider to match. |
| `Resource` | no | null (any resource) | Resource name to match. |
| `Operation` | yes | | Operation to match. |
| `Phase` | no | `FaultPhases.Before` | Phase to match. |
| `Occurrence` | no | `1` | The rule fires on this matching evaluation (1-based), counted from registration. |
| `Reason` | no | null | Included in the report and the exception message. |

### Phases

| Constant | Value | Meaning when the rule fires |
|---|---|---|
| `FaultPhases.Before` | `before` | The operation did not happen. State is unchanged. |
| `FaultPhases.After` | `after` | The operation happened, but the caller receives an error instead of success. |

### Matching rules

- `Add` throws `UnsupportedCapabilityException` unless a registered resource with the rule's provider (and name, when `Resource` is set) declares a matching `FaultPoint(Provider, Operation, Phase)`.
- `Add` throws `ArgumentException` for a rule identical to an unfired registered rule.
- `Add` is allowed until disposal begins; afterwards it throws `ObjectDisposedException`.
- At each evaluation, every unfired rule that matches increments its count. The earliest-registered rule whose count has reached its `Occurrence` fires. At most one rule fires per evaluation; another rule that became due fires at its next match.
- Every firing is journaled with outcome `Faulted`.

### FaultReport

| Field | Description |
|---|---|
| `RuleId` | `fault-1`, `fault-2`, … in registration order. |
| `Provider`, `Resource`, `Operation`, `Phase` | The point where the rule fired. |
| `Occurrence` | The matching evaluation at which it fired. |
| `StateChanged` | Whether the operation had changed state. |
| `VirtualTime`, `CorrelationId`, `Reason` | Context at firing time. |

`ActiveFault` exposes `Id`, `Rule`, `Matches`, `Report`, and `HasFired`.

## OperationJournal

| Member | Description |
|---|---|
| `Record(provider, resource, operation, phase, outcome, target, details, error, payload)` | Appends an entry at the current virtual time. `payload` is a `Func<string?>`, evaluated only when payload capture is enabled. |
| `GetEntries()` | Immutable snapshot of all entries, in recording order. |
| `Count` | Number of entries. |
| `BeginCorrelation(correlationId)` | Sets the correlation ID for entries recorded by the current asynchronous flow until the returned scope is disposed. Scopes nest. |
| `CurrentCorrelationId` | The current flow's correlation ID. |
| `CapturesPayloads` | Value of `SimulationEnvironmentOptions.CapturePayloads`. |

### OperationJournalEntry

| Field | Description |
|---|---|
| `Sequence` | 1, 2, … in recording order. |
| `ScenarioId`, `Seed` | The environment's values. |
| `Provider`, `Resource`, `Operation` | What was recorded. |
| `Phase` | `before`: state had not changed. `after`: state had changed. |
| `VirtualTime` | Virtual time at recording. |
| `CorrelationId` | From `BeginCorrelation`, or null. |
| `Outcome` | `Succeeded`, `Failed`, `Rejected` (refused before any state change, for example unsupported), `Faulted` (a fault rule fired), or `Canceled`. |
| `Target`, `Details`, `Error` | Provider-specific context. |
| `Payload` | Captured payload, or null. |

## DiagnosticSnapshot

An immutable record with `ScenarioId`, `Seed`, `VirtualTime`, `EnvironmentState`, `Journal`, `PendingWork`,
`FiredFaults`, and `UnfiredFaults`.

| Member | Description |
|---|---|
| `FormatSummary(maxJournalEntries = 25)` | Text summary: identity, fired and unfired faults, pending work, and the most recent journal entries. |
| `ToJson()` / `WriteJson(stream)` | Indented JSON with LF line endings. |
| `ExportAsync(directory, fileName = null, cancellationToken)` | Writes the JSON into `directory` (created if missing) and returns the path. The default file name is `<scenario-id>.seed-<seed>.diagnostics.json`, with characters that are invalid in file names, and `.`, replaced by `_`. |

## Exceptions

All SimForge exceptions derive from `SimForgeException`.

| Exception | Raised when | Notable members |
|---|---|---|
| `UnsupportedCapabilityException` | A request is outside the declared capabilities. Raised before any state changes. | `CapabilityId` |
| `SimulatedServiceException` | A simulated service reports an error. | `Provider`, `Resource`, `ErrorCode` |
| `SimulatedFaultException` | A fault rule fires (`ErrorCode` `injected_fault`). Derives from `SimulatedServiceException`. | `Report`, `StateChanged` |
| `SimForgeAssertionException` | A SimForge assertion fails. | |
| `SimulationLimitExceededException` | A scheduler drive exceeds its step bound. | `StepLimit`, `VirtualTime`, `PendingWork` |
| `SimForgeInternalException` | A SimForge invariant is violated. | |
| `SimulationInitializationException` | Environment initialization fails. | `ResourceName`, `CleanupErrors`, `InnerException` |
| `SimulationCleanupException` | Environment disposal fails. | `Errors` |
| `ResourceCleanupException` | One resource fails to dispose. | `ResourceName`, `InnerException` |
| `SimulationWorkAbandonedException` | Running work does not stop within the cleanup timeout. | `WorkDescription`, `CleanupTimeout` |
