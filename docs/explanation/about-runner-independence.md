# About runner independence

SimForge scenarios are not xUnit tests. They are plain objects that xUnit, or a future SimForge runner, can execute.
This page explains why the two are kept apart and what that separation guarantees.

## Scenarios and tests

A scenario describes a workflow against simulated infrastructure: what to set up, and what to do and check. A test
framework decides when and how things run, and how results are reported. Mixing the two ties every scenario to one
framework's attributes, fixtures, and assertion types.

So the scenario contracts (`IScenario`, `ScenarioContext`, `SimAssert`) depend only on SimForge's core, and an xUnit test
is a one-line wrapper around a scenario. The intended promise is that scenario bodies written against these contracts
run unchanged under a native SimForge runner when one exists. xUnit attributes, fixtures, and xUnit's own assertions
remain outside that promise.

## One owner for the lifecycle

`ScenarioExecutor` is the only component that creates, initializes, and disposes a scenario's environment, and the only
one that decides the outcome. Adapters for test frameworks translate its results; they do not repeat its logic.
Behavior therefore stays identical across runners: the same scenario classified the same way, with the same diagnostics.

The executor returns a result instead of throwing, because some things need more than one error to describe. A
scenario can fail its assertion *and* fail to clean up afterwards. Both errors are kept, side by side, and so is the
exact outcome (passed, failed, skipped, canceled, or timed out).

## A thin adapter that cannot let failures through

The xUnit adapter has one essential job: never report a scenario that did not pass as a passing test. Canceled and
timed-out scenarios therefore fail the xUnit test, and so do scenarios that passed but failed during cleanup. The mapping
is tested through xUnit itself. A separate project of deliberately failing tests runs under xUnit's own runner, and a
test reads xUnit's XML report to check every outcome.

The adapter takes no cancellation-token parameter. It always uses the token xUnit gives the current test, so there is
no second token to forget to pass.

## Dependencies point one way

Only the xUnit adapter references a test framework. The core and the providers reference nothing but the .NET runtime,
and application code never references SimForge. These rules are checked by tests rather than left to convention. They
keep the simulation engine usable from any host, and keep production code free of test tooling.

## Related

- [SimForge.Testing](../reference/testing.md) and [SimForge.Xunit](../reference/xunit.md) (reference)
- [Packages](../reference/packages.md) (reference)
