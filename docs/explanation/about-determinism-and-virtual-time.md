# About determinism and virtual time

A test that sometimes fails is worse than no test: it teaches people to ignore failures. SimForge is built so that a
scenario, run again with the same seed, does the same thing. This page explains how that is achieved and where the
guarantee stops.

## One environment owns everything

Every scenario gets its own `SimulationEnvironment`. The environment owns the resources, the clock, the queue of
scheduled work, the identifier generator, the fault rules, and the journal. Nothing is shared through static state.
Two scenarios running in parallel with identically named databases therefore cannot see each other. Nothing survives
from one test into the next, because each environment is disposed at the end of its run.

## Time moves only when the test says so

Time-dependent behavior (timeouts, retries, expirations) is usually where test suites become slow or flaky. Waiting for
real time makes tests slow; racing real time makes them flaky. SimForge replaces the clock. Application code receives a
`TimeProvider` (the environment's `VirtualClock`), and time advances only when the test calls `AdvanceByAsync` or
`AdvanceToAsync`. An hour-long backoff takes microseconds and happens at exactly the instant it should.

There is also no background thread that runs timers. Scheduled work, including timer callbacks, runs only while the
test drives the scheduler, and only one caller may drive at a time. Ordering is therefore fully determined: by due
time, then by the order in which work was scheduled.

`RunUntilIdleAsync` runs everything due *now* and deliberately never moves time forward. If draining the queue could
also advance the clock, a test could skip over a timeout without anyone noticing. Keeping "run what is due" separate
from "let time pass" makes every passage of time visible in the test code.

A drive also has a step bound. Work that keeps rescheduling itself (a retry loop that never succeeds, say) ends in
`SimulationLimitExceededException` listing the pending work, instead of hanging the test run.

## Seeds

Identifiers come from `DeterministicIds`, which derives GUIDs and counters from the environment's seed. When a run does
not choose a seed, the seed is a stable hash of the scenario ID. So a rerun is already reproducible, and every failure
report prints the seed that was used.

## Real deadlines are a separate thing

Virtual time governs the simulation. It cannot protect a test run from code that genuinely hangs. For that, a scenario
run can have a *real* elapsed-time deadline, and virtual time never counts against it. A scenario that advances a
simulated year still finishes in milliseconds. When a deadline passes, SimForge cancels the scenario and waits a bounded
time for it to stop. Managed code cannot be forcibly stopped, so work that ignores cancellation is reported as abandoned
and its environment is disposed and never reused.

## Where determinism stops

The guarantee covers work that goes through SimForge's scheduler, and identifiers from `DeterministicIds`. It does not
cover work started with `Task.Run`, thread-pool continuations, real clocks, random number generators, or I/O started by
application code. When a scenario needs one of those to be reproducible, route it through the virtual clock or the
scheduler. Microsoft's `FakeTimeProvider` offers a controllable clock too. SimForge's clock is additionally tied to a
scheduler, a journal, and fault injection, so time, ordering, and failures are recorded together.

## Related

- [How to test time-dependent code](../how-to/test-time-dependent-code.md)
- [SimulationScheduler and VirtualClock](../reference/core.md#simulationscheduler) (reference)
