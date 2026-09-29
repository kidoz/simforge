using SimForge.Tests.Shared;
using Xunit;

namespace SimForge.Core.Tests;

public sealed class EnvironmentLifecycleTests
{
    [Fact]
    public async Task Initialization_runs_in_registration_order_and_disposal_in_reverse()
    {
        var log = new EventLog();
        var environment = new SimulationEnvironment();
        environment.AddResource(new RecordingResource("a", log));
        environment.AddResource(new RecordingResource("b", log));
        environment.AddResource(new RecordingResource("c", log));
        Assert.Equal(SimulationEnvironmentState.Created, environment.State);

        await environment.InitializeAsync(TestContext.Current.CancellationToken);
        Assert.Equal(SimulationEnvironmentState.Ready, environment.State);

        await environment.DisposeAsync();
        Assert.Equal(SimulationEnvironmentState.Disposed, environment.State);
        Assert.Equal(["init:a", "init:b", "init:c", "dispose:c", "dispose:b", "dispose:a"], log.Snapshot());
    }

    [Fact]
    public async Task Partial_initialization_failure_disposes_started_resources_in_reverse_and_keeps_the_original_error()
    {
        var log = new EventLog();
        var original = new InvalidOperationException("b cannot start");
        var environment = new SimulationEnvironment();
        var a = environment.AddResource(new RecordingResource("a", log));
        var b = environment.AddResource(new RecordingResource("b", log) { InitializeError = original });
        var c = environment.AddResource(new RecordingResource("c", log));

        var exception = await Assert.ThrowsAsync<SimulationInitializationException>(
            () => environment.InitializeAsync(TestContext.Current.CancellationToken));

        Assert.Same(original, exception.InnerException);
        Assert.Equal("b", exception.ResourceName);
        Assert.Empty(exception.CleanupErrors);
        Assert.Equal(SimulationEnvironmentState.InitializationFailed, environment.State);
        Assert.Equal(["init:a", "init:b", "dispose:b", "dispose:a"], log.Snapshot());
        Assert.Throws<InvalidOperationException>(environment.ThrowIfNotReady);

        await environment.DisposeAsync();
        Assert.Equal((1, 1, 1), (a.DisposeCount, b.DisposeCount, c.DisposeCount));
        Assert.Equal("dispose:c", log.Snapshot()[^1]);
    }

    [Fact]
    public async Task Cleanup_errors_during_failed_initialization_are_retained_with_the_original_error()
    {
        var environment = new SimulationEnvironment();
        environment.AddResource(new RecordingResource("a") { DisposeError = new IOException("a cleanup failed") });
        environment.AddResource(new RecordingResource("b") { InitializeError = new InvalidOperationException("b failed") });

        var exception = await Assert.ThrowsAsync<SimulationInitializationException>(
            () => environment.InitializeAsync(TestContext.Current.CancellationToken));

        Assert.Equal("b failed", exception.InnerException!.Message);
        var cleanup = Assert.IsType<ResourceCleanupException>(Assert.Single(exception.CleanupErrors));
        Assert.Equal("a", cleanup.ResourceName);
        Assert.IsType<IOException>(cleanup.InnerException);
        await environment.DisposeAsync();
    }

    [Fact]
    public async Task Canceled_initialization_starts_no_resource_and_reports_cancellation()
    {
        var log = new EventLog();
        var environment = new SimulationEnvironment();
        environment.AddResource(new RecordingResource("a", log));
        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();

        var exception = await Assert.ThrowsAsync<SimulationInitializationException>(() => environment.InitializeAsync(canceled.Token));

        Assert.IsType<OperationCanceledException>(exception.InnerException);
        Assert.Null(exception.ResourceName);
        Assert.Empty(log.Snapshot());
        await environment.DisposeAsync();
    }

    [Fact]
    public async Task All_disposal_errors_are_collected_and_every_resource_is_still_disposed()
    {
        var environment = new SimulationEnvironment();
        var a = environment.AddResource(new RecordingResource("a"));
        environment.AddResource(new RecordingResource("b") { DisposeError = new InvalidOperationException("b") });
        environment.AddResource(new RecordingResource("c") { DisposeError = new InvalidOperationException("c") });
        await environment.InitializeAsync(TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAsync<SimulationCleanupException>(() => environment.DisposeAsync().AsTask());

        Assert.Equal(["c", "b"], exception.Errors.Cast<ResourceCleanupException>().Select(error => error.ResourceName));
        Assert.Equal(1, a.DisposeCount);
        Assert.Equal(exception.Errors, environment.CleanupErrors);
        Assert.Equal(SimulationEnvironmentState.Disposed, environment.State);
    }

    [Fact]
    public async Task Repeated_and_concurrent_disposal_dispose_once_and_later_calls_do_not_throw()
    {
        var environment = new SimulationEnvironment();
        var resource = environment.AddResource(new RecordingResource("a") { DisposeError = new InvalidOperationException("broken") });
        await environment.InitializeAsync(TestContext.Current.CancellationToken);

        var first = environment.DisposeAsync().AsTask();
        var concurrent = environment.DisposeAsync().AsTask();

        await Assert.ThrowsAsync<SimulationCleanupException>(() => first);
        await concurrent;
        await environment.DisposeAsync();
        Assert.Equal(1, resource.DisposeCount);
    }

    [Fact]
    public async Task Configuration_is_rejected_after_initialization_and_everything_after_disposal()
    {
        var environment = new SimulationEnvironment();
        environment.AddResource(new RecordingResource("a"));
        Assert.Throws<ArgumentException>(() => environment.AddResource(new RecordingResource("a")));
        Assert.Throws<ArgumentException>(() => environment.AddResource(new RecordingResource("not valid!")));
        Assert.Throws<InvalidOperationException>(environment.ThrowIfNotReady);

        await environment.InitializeAsync(TestContext.Current.CancellationToken);
        Assert.Throws<InvalidOperationException>(() => environment.AddResource(new RecordingResource("b")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => environment.InitializeAsync(TestContext.Current.CancellationToken));

        await environment.DisposeAsync();
        Assert.True(environment.LifetimeToken.IsCancellationRequested);
        Assert.Throws<ObjectDisposedException>(environment.ThrowIfNotReady);
        Assert.Throws<ObjectDisposedException>(() => environment.AddResource(new RecordingResource("c")));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => environment.InitializeAsync(TestContext.Current.CancellationToken));
        Assert.Throws<ObjectDisposedException>(() => environment.Faults.Add(new FaultRule { Provider = RecordingResource.ProviderName, Operation = RecordingResource.Operation }));
    }

    [Fact]
    public async Task Resources_are_looked_up_by_name_and_type()
    {
        await using var environment = new SimulationEnvironment();
        var resource = environment.AddResource(new RecordingResource("db"));

        Assert.Same(resource, environment.GetResource<RecordingResource>("db"));
        Assert.Throws<KeyNotFoundException>(() => environment.GetResource<RecordingResource>("missing"));
    }

    [Fact]
    public async Task Environment_cannot_be_disposed_from_its_own_scheduled_work()
    {
        await using var environment = new SimulationEnvironment();
        environment.Scheduler.Schedule("dispose-self", TimeSpan.Zero, async _ => await environment.DisposeAsync());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => environment.Scheduler.RunUntilIdleAsync(TestContext.Current.CancellationToken).AsTask());
        Assert.NotEqual(SimulationEnvironmentState.Disposed, environment.State);
    }

    [Fact]
    public async Task Disposal_cancels_running_scheduled_work_that_observes_the_lifetime_token()
    {
        var environment = new SimulationEnvironment(new SimulationEnvironmentOptions { CleanupTimeout = TimeSpan.FromSeconds(10) });
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        environment.Scheduler.Schedule("cooperative", TimeSpan.Zero, async cancellationToken =>
        {
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        });
        var driver = Task.Run(() => environment.Scheduler.RunUntilIdleAsync(TestContext.Current.CancellationToken).AsTask(), TestContext.Current.CancellationToken);
        await started.Task;

        await environment.DisposeAsync();

        Assert.Empty(environment.CleanupErrors);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => driver);
        Assert.Contains(environment.Journal.GetEntries(), entry => entry.Operation == "cooperative" && entry.Outcome == OperationOutcome.Canceled);
    }

    [Fact]
    public async Task Work_that_ignores_cancellation_is_abandoned_after_the_cleanup_timeout()
    {
        var environment = new SimulationEnvironment(new SimulationEnvironmentOptions { CleanupTimeout = TimeSpan.FromMilliseconds(100) });
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        environment.Scheduler.Schedule("stubborn", TimeSpan.Zero, async _ =>
        {
            started.SetResult();
            await release.Task;
        });
        var driver = Task.Run(() => environment.Scheduler.RunUntilIdleAsync(TestContext.Current.CancellationToken).AsTask(), TestContext.Current.CancellationToken);
        await started.Task;

        var exception = await Assert.ThrowsAsync<SimulationCleanupException>(() => environment.DisposeAsync().AsTask());

        var abandoned = Assert.IsType<SimulationWorkAbandonedException>(Assert.Single(exception.Errors));
        Assert.Contains("stubborn", abandoned.WorkDescription, StringComparison.Ordinal);
        release.SetResult();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => driver);
    }

    [Fact]
    public async Task Disposal_never_starts_further_work_and_waits_for_the_running_item_before_disposing_resources()
    {
        var log = new EventLog();
        var environment = new SimulationEnvironment(new SimulationEnvironmentOptions { CleanupTimeout = TimeSpan.FromSeconds(30) });
        environment.AddResource(new RecordingResource("db", log));
        await environment.InitializeAsync(TestContext.Current.CancellationToken);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        environment.Scheduler.Schedule("running", TimeSpan.Zero, async _ =>
        {
            started.SetResult();
            await release.Task;
            log.Add("running:done");
        });
        var next = environment.Scheduler.Schedule("next", TimeSpan.Zero, () => log.Add("next:ran"));
        var driver = Task.Run(() => environment.Scheduler.RunUntilIdleAsync(TestContext.Current.CancellationToken).AsTask(), TestContext.Current.CancellationToken);
        await started.Task;

        var disposal = environment.DisposeAsync().AsTask();
        await Task.WhenAny(disposal, Task.Delay(TimeSpan.FromMilliseconds(200), TestContext.Current.CancellationToken));
        Assert.False(disposal.IsCompleted, "disposal must wait for the running item");
        release.SetResult();
        await disposal;

        Assert.Equal(["init:db", "running:done", "dispose:db"], log.Snapshot());
        Assert.Equal(ScheduledWorkStatus.Discarded, next.Status);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => driver);
    }

    [Fact]
    public void Invalid_options_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => new SimulationEnvironment(new SimulationEnvironmentOptions { ScenarioId = " " }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SimulationEnvironment(new SimulationEnvironmentOptions { MaxSchedulerSteps = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SimulationEnvironment(new SimulationEnvironmentOptions { CleanupTimeout = TimeSpan.FromTicks(-1) }));
    }
}
