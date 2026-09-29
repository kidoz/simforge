# How to test time-dependent code

Use this when code under test waits, times out, retries, or expires things, and you want the test to control time
instead of waiting for it.

## Give the code a TimeProvider

Make the code take a `TimeProvider` and use it for `GetUtcNow()`, `Task.Delay(delay, timeProvider, ...)`, timers, and
`CancellationTokenSource(delay, timeProvider)`:

```csharp
public sealed class ReminderService(TimeProvider timeProvider)
{
    public async Task<DateTimeOffset> WaitForReminderAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        await Task.Delay(delay, timeProvider, cancellationToken);
        return timeProvider.GetUtcNow();
    }
}
```

In the scenario setup, construct it with the virtual clock:

```csharp
setup => setup.AddService(context => new ReminderService(context.Clock))
```

## Advance time past a delay

Start the operation, advance the virtual clock, then await the operation:

```csharp
var reminder = context.GetRequiredService<ReminderService>().WaitForReminderAsync(TimeSpan.FromHours(1), cancellationToken);
await context.Scheduler.AdvanceByAsync(TimeSpan.FromHours(1), cancellationToken);
var remindedAt = await reminder; // 2026-01-01T01:00:00Z with the default start time
```

Await the operation before asserting on anything it does after the delay. The continuation after `Task.Delay` may run
on the thread pool.

## Schedule simulated events

To make something happen at a virtual instant, schedule it and advance:

```csharp
var expired = false;
context.Scheduler.Schedule("expire-cart", TimeSpan.FromMinutes(15), () => expired = true);
await context.Scheduler.AdvanceByAsync(TimeSpan.FromMinutes(15), cancellationToken);
```

## Run work that is due now without moving time

```csharp
context.Scheduler.Schedule("publish", TimeSpan.Zero, () => published++);
await context.Scheduler.RunUntilIdleAsync(cancellationToken);
```

`RunUntilIdleAsync` never advances the clock. Use `AdvanceByAsync` for anything due later.

## Drive a periodic timer

```csharp
using var timer = context.Clock.CreateTimer(_ => ticks++, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
await context.Scheduler.AdvanceByAsync(TimeSpan.FromMinutes(10), cancellationToken); // ticks == 10
```

## Start at a specific instant

Pass a start time with the run options:

```csharp
XunitScenario.RunAsync(scenario, new ScenarioRunOptions { StartTime = new DateTimeOffset(2030, 3, 31, 23, 59, 0, TimeSpan.Zero) });
```

## If the scheduler reports a step limit

`SimulationLimitExceededException` lists the pending work that kept the scheduler busy. Usually the fix is in the code
or the scenario, for example a retry loop without an exit. If the work is legitimately large, raise the bound with
`ScenarioRunOptions.MaxSchedulerSteps`.

See also: [SimulationScheduler and VirtualClock](../reference/core.md#simulationscheduler),
[About determinism and virtual time](../explanation/about-determinism-and-virtual-time.md).
