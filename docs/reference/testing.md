# SimForge.Testing

Namespace `SimForge.Testing`. Runner-neutral scenario contracts and execution.

- [ScenarioDescriptor](#scenariodescriptor)
- [IScenario and Scenario](#iscenario-and-scenario)
- [ScenarioSetup](#scenariosetup)
- [ScenarioContext](#scenariocontext)
- [ScenarioRunOptions](#scenariorunoptions)
- [ScenarioExecutor](#scenarioexecutor)
- [ScenarioResult](#scenarioresult)
- [Outcomes and failure kinds](#outcomes-and-failure-kinds)

## ScenarioDescriptor

```csharp
public ScenarioDescriptor(string id, string? displayName = null, IEnumerable<string>? tags = null, ScenarioSourceLocation? source = null)
```

| Member | Description |
|---|---|
| `Id` | Stable identifier: at most 256 ASCII letters, digits, `.`, `_`, `:`, `/`, or `-`, starting with a letter or digit. |
| `DisplayName` | Defaults to `Id`. |
| `Tags` | Non-empty, without whitespace. Stored de-duplicated in ordinal order. |
| `Source` | `ScenarioSourceLocation(FilePath, LineNumber)`, or null. `ScenarioSourceLocation.Capture()` records the caller's location. |
| `DefaultSeed` | 32-bit FNV-1a hash of the UTF-8 bytes of `Id`. The same value in every process and on every machine. |

Descriptors are equal when `Id`, `DisplayName`, `Tags` (by content), and `Source` are equal.

## IScenario and Scenario

```csharp
public interface IScenario
{
    ScenarioDescriptor Descriptor { get; }
    void Configure(ScenarioSetup setup);
    ValueTask ExecuteAsync(ScenarioContext context, CancellationToken cancellationToken);
}
```

`Configure` runs before the environment is initialized. `ExecuteAsync` runs against the initialized environment. A
scenario instance may be executed repeatedly and in parallel.

`Scenario` implements `IScenario` with delegates:

| Member | Description |
|---|---|
| `Scenario(descriptor, configure, execute)` | `configure` may be null. |
| `Scenario.Create(id, execute)` | Descriptor with the caller's source location; no setup step. |
| `Scenario.Create(id, configure, execute)` | Descriptor with the caller's source location. |

## ScenarioSetup

Passed to `Configure`.

| Member | Description |
|---|---|
| `Descriptor` | The scenario's descriptor. |
| `Environment` | The `SimulationEnvironment`, in state `Created`. |
| `AddService<TService>(Func<ScenarioContext, TService> factory)` | Registers a per-run service. Each type may be registered once. Allowed only during `Configure`. |

Service rules:

- A service is created on first request and at most once per run.
- Services are disposed in reverse creation order before the environment. `IAsyncDisposable` is preferred over `IDisposable`.
- A circular dependency throws `InvalidOperationException`.
- This is a lookup table, not a dependency-injection container.

## ScenarioContext

Passed to `ExecuteAsync`.

| Member | Description |
|---|---|
| `Descriptor`, `Environment`, `Seed` | Run identity. |
| `Clock` | `VirtualClock` (a `TimeProvider`). |
| `Scheduler`, `Journal`, `Faults`, `Ids` | The environment's components. |
| `Services` | `IServiceProvider` over registered services. It also resolves `ScenarioContext`, `SimulationEnvironment`, `TimeProvider`, `VirtualClock`, and `IServiceProvider`. |
| `GetRequiredService<TService>()` | Returns a registered service, or throws `InvalidOperationException`. |
| `GetResource<TResource>(name)` | Same as `Environment.GetResource`. |
| `CaptureDiagnostics()` | Same as `Environment.CaptureDiagnostics`. |
| `Skip(reason)` | Throws `ScenarioSkippedException`; the outcome becomes `Skipped`. |

## ScenarioRunOptions

| Property | Type | Default | Description |
|---|---|---|---|
| `Seed` | `int?` | `Descriptor.DefaultSeed` | Seed of the run. |
| `StartTime` | `DateTimeOffset` | 2026-01-01T00:00:00Z | Initial virtual time. |
| `Timeout` | `TimeSpan?` | none | Real-time deadline covering configuration, initialization, and the body. Must be positive. Virtual time does not count. |
| `CleanupTimeout` | `TimeSpan` | 5 s | Real time a canceled body, or running scheduled work, gets before being reported as abandoned. |
| `MaxSchedulerSteps` | `int` | `10_000` | Passed to the environment. |
| `CapturePayloads` | `bool` | `false` | Passed to the environment. |
| `DiagnosticsDirectory` | `string?` | null | Directory for exported diagnostics. Nothing is written when null. |
| `ExportDiagnostics` | `DiagnosticsExport` | `OnFailure` | `Never`, `OnFailure` (outcomes other than `Passed` and `Skipped`, or any cleanup error), or `Always`. |

`ScenarioRunOptions.Default` holds the defaults.

## ScenarioExecutor

```csharp
public Task<ScenarioResult> ExecuteAsync(IScenario scenario, ScenarioRunOptions? options = null, CancellationToken cancellationToken = default)
```

Sequence of one run:

1. Create a new `SimulationEnvironment` whose scenario ID is `Descriptor.Id`.
2. Call `Configure`.
3. Call `InitializeAsync`.
4. Run `ExecuteAsync` on the thread pool, under `cancellationToken` linked with `Timeout`.
5. On cancellation or timeout, wait up to `CleanupTimeout` for the body. If it does not stop, set `WorkAbandoned`.
6. Dispose services, then the environment. This always happens.
7. Capture diagnostics and export them if requested.
8. Return a `ScenarioResult`.

`ExecuteAsync` reports failures through the result and does not throw them. It throws only for a null scenario or
invalid options.

## ScenarioResult

| Member | Description |
|---|---|
| `Descriptor`, `Seed` | Run identity. |
| `Outcome`, `FailureKind` | See below. |
| `Passed` | `Outcome == Passed`. |
| `Elapsed` | Real elapsed time, including cleanup. |
| `PrimaryError` | The error that decided the outcome, or null. |
| `CleanupErrors` | Errors from disposing services and the environment. |
| `SkipReason` | Reason passed to `Skip`, or null. |
| `WorkAbandoned` | True when the body did not stop within `CleanupTimeout`. |
| `Diagnostics` | `DiagnosticSnapshot` captured after disposal. |
| `DiagnosticsPath` | Path of the exported diagnostics, or null. |
| `FormatReport(maxJournalEntries = 25)` | Text report: outcome, failure kind, elapsed time, seed, source location, errors, and the diagnostics summary. |

## Outcomes and failure kinds

| `ScenarioOutcome` | Condition |
|---|---|
| `Passed` | The body completed and cleanup reported no error. |
| `Failed` | The body, configuration, initialization, or cleanup failed. |
| `Skipped` | `Skip` was called and cleanup reported no error. |
| `Canceled` | The caller's token was canceled before the scenario completed. |
| `TimedOut` | `Timeout` elapsed before the scenario completed, even if the body finished afterwards. |

`FailureKind` is `None` unless the outcome is `Failed`:

| Cause | `ScenarioFailureKind` |
|---|---|
| `SimForgeAssertionException` | `Assertion` |
| `UnsupportedCapabilityException` | `UnsupportedCapability` |
| `SimulatedServiceException`, including `SimulatedFaultException` | `SimulatedServiceError` |
| `SimulationLimitExceededException` | `SimulationLimit` |
| `SimForgeInternalException` | `Internal` |
| Any other exception thrown by `Configure` | `Configuration` |
| Any other exception during initialization | `Initialization` |
| Any other exception thrown by the body, including an `OperationCanceledException` not caused by the run's token | `Application` |
| Cleanup error after a passing or skipped body | `Cleanup` |

For configuration and initialization failures, the first five rows take precedence over `Configuration` and
`Initialization`. Cleanup errors are always kept in `CleanupErrors` alongside `PrimaryError`.
