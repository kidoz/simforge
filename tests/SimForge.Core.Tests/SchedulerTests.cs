using Xunit;

namespace SimForge.Core.Tests;

public sealed class SchedulerTests
{
    private static readonly DateTimeOffset Start = SimulationEnvironmentOptions.DefaultStartTime;

    private static SimulationEnvironment NewEnvironment(int maxSteps = 10_000) =>
        new(new SimulationEnvironmentOptions { ScenarioId = "scheduler-tests", MaxSchedulerSteps = maxSteps });

    [Fact]
    public async Task Work_due_at_the_same_instant_runs_in_insertion_order_after_earlier_work()
    {
        await using var environment = NewEnvironment();
        var order = new List<string>();
        environment.Scheduler.Schedule("a", TimeSpan.FromSeconds(5), () => order.Add("a"));
        environment.Scheduler.Schedule("b", TimeSpan.FromSeconds(5), () => order.Add("b"));
        environment.Scheduler.Schedule("c", TimeSpan.FromSeconds(1), () => order.Add("c"));

        await environment.Scheduler.AdvanceByAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(["c", "a", "b"], order);
    }

    [Fact]
    public async Task Work_scheduled_for_the_current_instant_runs_after_already_queued_work_of_that_instant()
    {
        await using var environment = NewEnvironment();
        var order = new List<string>();
        environment.Scheduler.Schedule("first", TimeSpan.Zero, () =>
        {
            order.Add("first");
            environment.Scheduler.Schedule("spawned", TimeSpan.Zero, () => order.Add("spawned"));
        });
        environment.Scheduler.Schedule("second", TimeSpan.Zero, () => order.Add("second"));

        var executed = await environment.Scheduler.RunUntilIdleAsync(TestContext.Current.CancellationToken);

        Assert.Equal(3, executed);
        Assert.Equal(["first", "second", "spawned"], order);
        Assert.Equal(Start, environment.Scheduler.Now);
    }

    [Fact]
    public async Task Timer_does_not_fire_before_its_deadline_and_fires_exactly_at_it()
    {
        await using var environment = NewEnvironment();
        DateTimeOffset? firedAt = null;
        environment.Scheduler.Schedule("deadline", TimeSpan.FromSeconds(10), () => firedAt = environment.Clock.GetUtcNow());

        await environment.Scheduler.AdvanceByAsync(TimeSpan.FromSeconds(10) - TimeSpan.FromTicks(1), TestContext.Current.CancellationToken);
        Assert.Null(firedAt);
        Assert.Equal(1, environment.Scheduler.PendingCount);

        await environment.Scheduler.AdvanceByAsync(TimeSpan.FromTicks(1), TestContext.Current.CancellationToken);
        Assert.Equal(Start.AddSeconds(10), firedAt);

        await environment.Scheduler.AdvanceByAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        Assert.Equal(Start.AddSeconds(11), environment.Scheduler.Now);
        Assert.Equal(0, environment.Scheduler.PendingCount);
    }

    [Fact]
    public async Task Advance_runs_each_item_at_its_own_due_time_and_rests_at_the_target()
    {
        await using var environment = NewEnvironment();
        var observed = new List<TimeSpan>();
        environment.Scheduler.Schedule("one", TimeSpan.FromSeconds(1), () => observed.Add(environment.Clock.GetUtcNow() - Start));
        environment.Scheduler.Schedule("three", TimeSpan.FromSeconds(3), () => observed.Add(environment.Clock.GetUtcNow() - Start));

        await environment.Scheduler.AdvanceByAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3)], observed);
        Assert.Equal(Start.AddSeconds(5), environment.Scheduler.Now);
    }

    [Fact]
    public async Task Work_scheduled_during_an_advance_runs_if_due_inside_the_window_and_stays_pending_otherwise()
    {
        await using var environment = NewEnvironment();
        var observed = new List<(string Name, TimeSpan At)>();
        environment.Scheduler.Schedule("seed", TimeSpan.FromSeconds(1), () =>
        {
            observed.Add(("seed", environment.Clock.GetUtcNow() - Start));
            environment.Scheduler.Schedule("inside", TimeSpan.FromSeconds(2), () => observed.Add(("inside", environment.Clock.GetUtcNow() - Start)));
            environment.Scheduler.Schedule("outside", TimeSpan.FromSeconds(10), () => observed.Add(("outside", environment.Clock.GetUtcNow() - Start)));
        });

        await environment.Scheduler.AdvanceByAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal([("seed", TimeSpan.FromSeconds(1)), ("inside", TimeSpan.FromSeconds(3))], observed);
        var pending = Assert.Single(environment.Scheduler.GetPendingWork());
        Assert.Equal("outside", pending.Name);
        Assert.Equal(Start.AddSeconds(11), pending.DueTime);
    }

    [Fact]
    public async Task RunUntilIdle_never_advances_time_to_future_work()
    {
        await using var environment = NewEnvironment();
        var ran = false;
        environment.Scheduler.Schedule("future", TimeSpan.FromMilliseconds(1), () => ran = true);

        var executed = await environment.Scheduler.RunUntilIdleAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, executed);
        Assert.False(ran);
        Assert.Equal(Start, environment.Scheduler.Now);
        Assert.Equal(1, environment.Scheduler.PendingCount);
    }

    [Fact]
    public async Task RunNext_runs_exactly_one_runnable_item()
    {
        await using var environment = NewEnvironment();
        var order = new List<string>();
        environment.Scheduler.Schedule("a", TimeSpan.Zero, () => order.Add("a"));
        environment.Scheduler.Schedule("b", TimeSpan.Zero, () => order.Add("b"));
        environment.Scheduler.Schedule("later", TimeSpan.FromSeconds(1), () => order.Add("later"));
        var cancellationToken = TestContext.Current.CancellationToken;

        Assert.True(await environment.Scheduler.RunNextAsync(cancellationToken));
        Assert.Equal(["a"], order);
        Assert.True(await environment.Scheduler.RunNextAsync(cancellationToken));
        Assert.False(await environment.Scheduler.RunNextAsync(cancellationToken));
        Assert.Equal(["a", "b"], order);
        Assert.Equal(Start, environment.Scheduler.Now);
    }

    [Fact]
    public async Task Canceled_work_never_runs_including_work_canceled_by_earlier_work()
    {
        await using var environment = NewEnvironment();
        var ran = new List<string>();
        var canceledUpFront = environment.Scheduler.Schedule("canceled-up-front", TimeSpan.FromSeconds(1), () => ran.Add("canceled-up-front"));
        var canceledLater = environment.Scheduler.Schedule("canceled-later", TimeSpan.FromSeconds(3), () => ran.Add("canceled-later"));
        environment.Scheduler.Schedule("canceler", TimeSpan.FromSeconds(2), () => Assert.True(canceledLater.Cancel()));

        Assert.True(canceledUpFront.Cancel());
        Assert.False(canceledUpFront.Cancel());
        await environment.Scheduler.AdvanceByAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Empty(ran);
        Assert.Equal(ScheduledWorkStatus.Canceled, canceledUpFront.Status);
        Assert.Equal(ScheduledWorkStatus.Canceled, canceledLater.Status);
    }

    [Fact]
    public async Task Completed_work_cannot_be_canceled()
    {
        await using var environment = NewEnvironment();
        var work = environment.Scheduler.Schedule("done", TimeSpan.Zero, () => { });

        await environment.Scheduler.RunUntilIdleAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ScheduledWorkStatus.Completed, work.Status);
        Assert.False(work.Cancel());
    }

    [Fact]
    public async Task Self_rescheduling_work_exceeds_the_step_bound_and_reports_pending_work()
    {
        await using var environment = NewEnvironment(maxSteps: 50);
        void Loop() => environment.Scheduler.Schedule("loop", TimeSpan.Zero, Loop);
        Loop();

        var exception = await Assert.ThrowsAsync<SimulationLimitExceededException>(
            () => environment.Scheduler.RunUntilIdleAsync(TestContext.Current.CancellationToken).AsTask());

        Assert.Equal(50, exception.StepLimit);
        Assert.Equal("loop", Assert.Single(exception.PendingWork).Name);
        Assert.Contains("loop", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Advance_is_bounded_when_future_work_keeps_rescheduling_itself()
    {
        await using var environment = NewEnvironment(maxSteps: 100);
        void Tick() => environment.Scheduler.Schedule("tick", TimeSpan.FromMilliseconds(1), Tick);
        Tick();

        await Assert.ThrowsAsync<SimulationLimitExceededException>(
            () => environment.Scheduler.AdvanceByAsync(TimeSpan.FromHours(1), TestContext.Current.CancellationToken).AsTask());

        Assert.Equal(Start.AddMilliseconds(100), environment.Scheduler.Now);
    }

    [Fact]
    public async Task Failing_work_propagates_to_the_driver_and_is_journaled()
    {
        await using var environment = NewEnvironment();
        var failing = environment.Scheduler.Schedule("explode", TimeSpan.Zero, () => throw new InvalidOperationException("boom"));
        var later = environment.Scheduler.Schedule("later", TimeSpan.Zero, () => { });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => environment.Scheduler.RunUntilIdleAsync(TestContext.Current.CancellationToken).AsTask());

        Assert.Equal("boom", exception.Message);
        Assert.Equal(ScheduledWorkStatus.Failed, failing.Status);
        Assert.Equal(ScheduledWorkStatus.Pending, later.Status);
        var entry = Assert.Single(environment.Journal.GetEntries());
        Assert.Equal(("explode", OperationOutcome.Failed, "boom"), (entry.Operation, entry.Outcome, entry.Error));
    }

    [Fact]
    public async Task Scheduled_work_cannot_drive_the_scheduler_reentrantly()
    {
        await using var environment = NewEnvironment();
        environment.Scheduler.Schedule("reentrant", TimeSpan.Zero, async cancellationToken =>
            await environment.Scheduler.RunUntilIdleAsync(cancellationToken));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => environment.Scheduler.RunUntilIdleAsync(TestContext.Current.CancellationToken).AsTask());
    }

    [Fact]
    public async Task Only_one_caller_can_drive_the_scheduler_at_a_time()
    {
        await using var environment = NewEnvironment();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        environment.Scheduler.Schedule("slow", TimeSpan.Zero, async _ =>
        {
            started.SetResult();
            await release.Task;
        });
        var firstDriver = environment.Scheduler.RunUntilIdleAsync(TestContext.Current.CancellationToken).AsTask();
        await started.Task;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => environment.Scheduler.RunUntilIdleAsync(TestContext.Current.CancellationToken).AsTask());

        release.SetResult();
        Assert.Equal(1, await firstDriver);
    }

    [Fact]
    public async Task Invalid_times_are_rejected_and_time_never_moves_backwards()
    {
        await using var environment = NewEnvironment();
        var cancellationToken = TestContext.Current.CancellationToken;

        Assert.Throws<ArgumentOutOfRangeException>(() => environment.Scheduler.Schedule("negative", TimeSpan.FromTicks(-1), () => { }));
        Assert.Throws<ArgumentOutOfRangeException>(() => environment.Scheduler.ScheduleAt("past", Start.AddTicks(-1), _ => ValueTask.CompletedTask));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => environment.Scheduler.AdvanceByAsync(TimeSpan.FromTicks(-1), cancellationToken).AsTask());

        await environment.Scheduler.AdvanceToAsync(Start.AddMinutes(1), cancellationToken);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => environment.Scheduler.AdvanceToAsync(Start, cancellationToken).AsTask());
        Assert.Equal(Start.AddMinutes(1), environment.Scheduler.Now);
    }

    [Fact]
    public async Task Disposal_discards_pending_work_and_closes_the_scheduler()
    {
        var environment = NewEnvironment();
        var first = environment.Scheduler.Schedule("first", TimeSpan.FromSeconds(1), () => { });
        var second = environment.Scheduler.Schedule("second", TimeSpan.FromSeconds(2), () => { });

        await environment.DisposeAsync();

        Assert.Equal(ScheduledWorkStatus.Discarded, first.Status);
        Assert.Equal(ScheduledWorkStatus.Discarded, second.Status);
        Assert.Contains(environment.Journal.GetEntries(), entry => entry.Operation == "discard" && entry.Details!.StartsWith("2 ", StringComparison.Ordinal));
        Assert.Throws<ObjectDisposedException>(() => environment.Scheduler.Schedule("late", TimeSpan.Zero, () => { }));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => environment.Scheduler.RunUntilIdleAsync(TestContext.Current.CancellationToken).AsTask());
    }
}
