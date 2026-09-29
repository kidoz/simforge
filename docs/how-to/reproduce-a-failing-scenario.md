# How to reproduce and diagnose a failing scenario

Use this when a scenario fails and you need to see what happened and run it again the same way.

## Read the report

A failing test's message starts with the outcome, failure kind, seed, and source location, followed by the journal:

```text
Scenario tutorial.place-order: Failed (Assertion) after 18.3 ms; seed 2031535255
Defined at .../OrderScenarios.cs:10
Primary error: SimForge.SimForgeAssertionException: SimAssert.Equal failed for `order.Get<string>("customer")`.
...
Journal (6 of 6 entries):
  #3 2026-01-01T00:00:00.0000000+00:00 postgresql/shop insert:after Succeeded target=orders details=tx=1; rows=1
```

Journal entries with phase `before` changed nothing; entries with phase `after` did. `Faulted` marks an injected fault.

## Run again with the same seed

Unless you set a seed, the seed is derived from the scenario ID, so plain reruns are already reproducible. To pin the
seed a report showed:

```csharp
[Fact]
public Task Place_order() => XunitScenario.RunAsync(OrderScenarios.PlaceOrder, new ScenarioRunOptions { Seed = 2031535255 });
```

## Capture keys and values

Payloads are off by default. Turn them on while investigating:

```csharp
new ScenarioRunOptions { CapturePayloads = true }
```

## Export the full diagnostics

```csharp
new ScenarioRunOptions
{
    DiagnosticsDirectory = Path.Combine(AppContext.BaseDirectory, "diagnostics"),
    ExportDiagnostics = DiagnosticsExport.Always,
}
```

The file is `<scenario-id>.seed-<seed>.diagnostics.json`. It contains the full journal, fired and unfired faults, and
pending work. Under xUnit, scenarios that do not pass also attach the same JSON to the test result; `dotnet test` saves
it under `TestResults/`.

## Correlate entries with your own IDs

Wrap a step of the scenario in a correlation scope. Every entry recorded inside it carries the ID:

```csharp
using (context.Journal.BeginCorrelation("order:1"))
{
    shop.Insert("orders", new Dictionary<string, object?> { ["id"] = 1L });
}
```

## Inspect a run in code

To examine a result programmatically, run the scenario with the executor instead of the xUnit adapter:

```csharp
var result = await new ScenarioExecutor().ExecuteAsync(scenario, new ScenarioRunOptions { CapturePayloads = true }, cancellationToken);
var insert = result.Diagnostics.Journal.Single(entry => entry.CorrelationId == "order:1");
```

## Bound a scenario that hangs

```csharp
new ScenarioRunOptions { Timeout = TimeSpan.FromSeconds(30) }
```

The deadline is real time and covers setup, initialization, and the body. A scenario that ignores cancellation is
reported as abandoned.

See also: [DiagnosticSnapshot](../reference/core.md#diagnosticsnapshot), [ScenarioRunOptions](../reference/testing.md#scenariorunoptions).
