using SimForge.Assertions;
using SimForge.Testing;
using SimForge.Tests.Shared;
using Xunit;
using Xunit.Sdk;

namespace SimForge.Xunit.Tests;

public sealed class OutcomeMappingTests
{
    private static readonly ScenarioExecutor Executor = new();

    private static Task<ScenarioResult> RunAsync(Scenario scenario, ScenarioRunOptions? options = null, CancellationToken? cancellationToken = null) =>
        Executor.ExecuteAsync(scenario, options, cancellationToken ?? TestContext.Current.CancellationToken);

    [Fact]
    public async Task Passed_result_maps_to_a_passing_test()
    {
        var result = await RunAsync(Scenario.Create("mapping.pass", (_, _) => ValueTask.CompletedTask));

        XunitScenario.ThrowIfNotPassed(result);
    }

    [Fact]
    public async Task Failed_result_throws_with_the_report_and_the_original_error()
    {
        var result = await RunAsync(Scenario.Create("mapping.fail", (_, _) =>
        {
            SimAssert.True(false, "deliberate");
            return ValueTask.CompletedTask;
        }));

        var exception = Assert.Throws<ScenarioFailedException>(() => XunitScenario.ThrowIfNotPassed(result));

        Assert.Same(result, exception.Result);
        Assert.IsType<SimForgeAssertionException>(exception.InnerException);
        Assert.Contains("Scenario mapping.fail: Failed (Assertion)", exception.Message, StringComparison.Ordinal);
        Assert.Contains($"seed {result.Seed}", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Canceled_and_timed_out_results_never_pass()
    {
        var canceled = await RunAsync(Scenario.Create("mapping.cancel", (_, _) => ValueTask.CompletedTask), cancellationToken: new CancellationToken(canceled: true));
        var timedOut = await RunAsync(
            Scenario.Create("mapping.timeout", async (_, cancellationToken) => await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)),
            new ScenarioRunOptions { Timeout = TimeSpan.FromMilliseconds(50) });

        Assert.Equal(ScenarioOutcome.Canceled, canceled.Outcome);
        Assert.Equal(ScenarioOutcome.TimedOut, timedOut.Outcome);
        Assert.Contains("Canceled", Assert.Throws<ScenarioFailedException>(() => XunitScenario.ThrowIfNotPassed(canceled)).Message, StringComparison.Ordinal);
        Assert.Contains("TimedOut", Assert.Throws<ScenarioFailedException>(() => XunitScenario.ThrowIfNotPassed(timedOut)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cleanup_failure_after_a_passing_body_throws()
    {
        var result = await RunAsync(Scenario.Create(
            "mapping.cleanup",
            setup => setup.Environment.AddResource(new RecordingResource("db") { DisposeError = new IOException("cleanup") }),
            (_, _) => ValueTask.CompletedTask));

        var exception = Assert.Throws<ScenarioFailedException>(() => XunitScenario.ThrowIfNotPassed(result));

        Assert.Contains("Failed (Cleanup)", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Skipped_result_maps_to_an_xunit_dynamic_skip()
    {
        var result = await RunAsync(Scenario.Create("mapping.skip", (context, _) =>
        {
            context.Skip("not modeled");
            return ValueTask.CompletedTask;
        }));

        // Caught by hand: xUnit's Assert.Throws rethrows dynamic-skip exceptions, which would skip this test instead of checking it.
        Exception? thrown = null;
        try
        {
            XunitScenario.ThrowIfNotPassed(result);
        }
        catch (Exception exception)
        {
            thrown = exception;
        }

        var skip = Assert.IsType<SkipException>(thrown);
        Assert.Contains("not modeled", skip.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Repeated_non_passing_runs_in_one_test_each_throw_scenario_failed_exception()
    {
        var failing = Scenario.Create("mapping.repeated-failure", (_, _) => throw new InvalidOperationException("deliberate"));

        await Assert.ThrowsAsync<ScenarioFailedException>(() => XunitScenario.RunAsync(failing));
        await Assert.ThrowsAsync<ScenarioFailedException>(() => XunitScenario.RunAsync(failing));
    }

    [Fact]
    public async Task RunAsync_passes_through_for_a_passing_scenario_inside_a_real_xunit_test()
    {
        await XunitScenario.RunAsync(Scenario.Create("mapping.inline", async (context, cancellationToken) =>
            await context.Scheduler.AdvanceByAsync(TimeSpan.FromMinutes(1), cancellationToken)));
    }
}
