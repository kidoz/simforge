using Xunit;

namespace SimForge.Core.Tests;

public sealed class VirtualClockTests
{
    private static readonly DateTimeOffset Start = SimulationEnvironmentOptions.DefaultStartTime;

    [Fact]
    public async Task Clock_reports_virtual_time_independent_of_the_host_clock()
    {
        await using var environment = new SimulationEnvironment();
        var clock = environment.Clock;
        var started = clock.GetTimestamp();

        Assert.Equal(Start, clock.GetUtcNow());
        Assert.Equal(TimeZoneInfo.Utc, clock.LocalTimeZone);

        await environment.Scheduler.AdvanceByAsync(TimeSpan.FromHours(3), TestContext.Current.CancellationToken);

        Assert.Equal(Start.AddHours(3), clock.GetUtcNow());
        Assert.Equal(TimeSpan.FromHours(3), clock.GetElapsedTime(started));
    }

    [Fact]
    public async Task Custom_start_time_is_used_and_normalized_to_utc()
    {
        var start = new DateTimeOffset(2030, 6, 1, 12, 0, 0, TimeSpan.FromHours(2));
        await using var environment = new SimulationEnvironment(new SimulationEnvironmentOptions { StartTime = start });

        Assert.Equal(start, environment.Clock.GetUtcNow());
        Assert.Equal(TimeSpan.Zero, environment.Clock.GetUtcNow().Offset);
    }

    [Fact]
    public async Task Task_delay_with_the_virtual_clock_completes_only_when_time_is_advanced_past_it()
    {
        await using var environment = new SimulationEnvironment();
        var cancellationToken = TestContext.Current.CancellationToken;
        var delay = Task.Delay(TimeSpan.FromMinutes(5), environment.Clock, cancellationToken);

        await environment.Scheduler.AdvanceByAsync(TimeSpan.FromMinutes(5) - TimeSpan.FromTicks(1), cancellationToken);
        Assert.False(delay.IsCompleted);

        await environment.Scheduler.AdvanceByAsync(TimeSpan.FromTicks(1), cancellationToken);
        Assert.True(delay.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task Timed_cancellation_source_follows_virtual_time()
    {
        await using var environment = new SimulationEnvironment();
        using var source = new CancellationTokenSource(TimeSpan.FromSeconds(30), environment.Clock);

        await environment.Scheduler.AdvanceByAsync(TimeSpan.FromSeconds(29), TestContext.Current.CancellationToken);
        Assert.False(source.IsCancellationRequested);

        await environment.Scheduler.AdvanceByAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        Assert.True(source.IsCancellationRequested);
    }

    [Fact]
    public async Task Periodic_timer_fires_at_each_period_without_drift_until_stopped()
    {
        await using var environment = new SimulationEnvironment();
        var cancellationToken = TestContext.Current.CancellationToken;
        var ticks = new List<TimeSpan>();
        using var timer = environment.Clock.CreateTimer(_ => ticks.Add(environment.Clock.GetUtcNow() - Start), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));

        await environment.Scheduler.AdvanceByAsync(TimeSpan.FromSeconds(3.5), cancellationToken);
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3)], ticks);

        Assert.True(timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan));
        await environment.Scheduler.AdvanceByAsync(TimeSpan.FromSeconds(5), cancellationToken);
        Assert.Equal(3, ticks.Count);
        Assert.Equal(0, environment.Scheduler.PendingCount);
    }

    [Fact]
    public async Task Changing_a_timer_replaces_its_pending_occurrence()
    {
        await using var environment = new SimulationEnvironment();
        var cancellationToken = TestContext.Current.CancellationToken;
        var fired = new List<TimeSpan>();
        using var timer = environment.Clock.CreateTimer(_ => fired.Add(environment.Clock.GetUtcNow() - Start), null, TimeSpan.FromSeconds(10), Timeout.InfiniteTimeSpan);

        Assert.True(timer.Change(TimeSpan.FromSeconds(2), Timeout.InfiniteTimeSpan));
        await environment.Scheduler.AdvanceByAsync(TimeSpan.FromSeconds(20), cancellationToken);

        Assert.Equal([TimeSpan.FromSeconds(2)], fired);
    }

    [Fact]
    public async Task Zero_due_timer_fires_at_the_current_instant_on_idle_drain()
    {
        await using var environment = new SimulationEnvironment();
        var fired = 0;
        using var timer = environment.Clock.CreateTimer(_ => fired++, null, TimeSpan.Zero, Timeout.InfiniteTimeSpan);

        await environment.Scheduler.RunUntilIdleAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, fired);
        Assert.Equal(Start, environment.Clock.GetUtcNow());
    }

    [Fact]
    public async Task Disposed_timer_never_fires_and_cannot_be_changed()
    {
        await using var environment = new SimulationEnvironment();
        var fired = 0;
        var timer = environment.Clock.CreateTimer(_ => fired++, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));

        timer.Dispose();
        await environment.Scheduler.AdvanceByAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal(0, fired);
        Assert.False(timer.Change(TimeSpan.Zero, Timeout.InfiniteTimeSpan));
    }

    [Fact]
    public async Task Invalid_timer_intervals_are_rejected()
    {
        await using var environment = new SimulationEnvironment();

        Assert.Throws<ArgumentOutOfRangeException>(() => environment.Clock.CreateTimer(_ => { }, null, TimeSpan.FromTicks(-5), Timeout.InfiniteTimeSpan));
        Assert.Throws<ArgumentOutOfRangeException>(() => environment.Clock.CreateTimer(_ => { }, null, TimeSpan.Zero, TimeSpan.FromTicks(-5)));
    }
}
