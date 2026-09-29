using SimForge.Assertions;
using SimForge.Testing;
using SimForge.Tests.Shared;
using Xunit;

namespace SimForge.Xunit.Fixtures;

/// <summary>
/// One xUnit test per scenario outcome. Only <see cref="Passing"/> may pass; SimForge.Xunit.Tests checks xUnit's report.
/// </summary>
public sealed class OutcomeFixtures
{
    private static readonly ScenarioRunOptions ShortDeadline = new()
    {
        Timeout = TimeSpan.FromMilliseconds(100),
        CleanupTimeout = TimeSpan.FromMilliseconds(100),
    };

    [Fact]
    public Task Passing() => XunitScenario.RunAsync(Scenario.Create("fixture.pass", async (context, cancellationToken) =>
        await context.Scheduler.AdvanceByAsync(TimeSpan.FromSeconds(1), cancellationToken)));

    [Fact]
    public Task AssertionFailure() => XunitScenario.RunAsync(Scenario.Create("fixture.assertion", (_, _) =>
    {
        SimAssert.Equal(1, 2, "deliberate assertion failure");
        return ValueTask.CompletedTask;
    }));

    [Fact]
    public Task UnsupportedCapability() => XunitScenario.RunAsync(Scenario.Create("fixture.unsupported", (_, _) =>
        throw new UnsupportedCapabilityException("fixture.feature", "deliberately unsupported")));

    [Fact]
    public Task Skipped() => XunitScenario.RunAsync(Scenario.Create("fixture.skip", (context, _) =>
    {
        context.Skip("deliberately skipped");
        return ValueTask.CompletedTask;
    }));

    [Fact]
    public Task Canceled()
    {
        TestContext.Current.CancelCurrentTest();
        return XunitScenario.RunAsync(Scenario.Create("fixture.cancel", (_, _) => ValueTask.CompletedTask));
    }

    [Fact]
    public Task TimedOut() => XunitScenario.RunAsync(
        Scenario.Create("fixture.timeout", async (_, cancellationToken) => await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)),
        ShortDeadline);

    [Fact]
    public Task Abandoned() => XunitScenario.RunAsync(
        Scenario.Create("fixture.abandoned", async (_, _) => await new TaskCompletionSource().Task),
        ShortDeadline);

    [Fact]
    public Task CleanupFailure() => XunitScenario.RunAsync(Scenario.Create(
        "fixture.cleanup",
        setup => setup.Environment.AddResource(new RecordingResource("db") { DisposeError = new IOException("deliberate cleanup failure") }),
        (_, _) => ValueTask.CompletedTask));

    [Fact]
    public Task InitializationFailure() => XunitScenario.RunAsync(Scenario.Create(
        "fixture.initialization",
        setup => setup.Environment.AddResource(new RecordingResource("db") { InitializeError = new InvalidOperationException("deliberate initialization failure") }),
        (_, _) => ValueTask.CompletedTask));
}
