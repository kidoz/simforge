using SimForge.Testing;
using SimForge.Xunit;
using Xunit;

namespace Orders.Simulated.Tests;

/// <summary>Thin xUnit wrappers around <see cref="OrderPlacementScenarios"/>.</summary>
public sealed class OrderPlacementTests
{
    [Fact]
    public Task Order_and_outbox_commit_together() => XunitScenario.RunAsync(OrderPlacementScenarios.OrderAndOutboxCommitTogether);

    [Fact]
    public Task Storage_failure_before_commit_leaves_nothing() => XunitScenario.RunAsync(OrderPlacementScenarios.StorageFailureBeforeCommitLeavesNothing);

    [Fact]
    public Task Failure_writing_outbox_rolls_back_the_order() => XunitScenario.RunAsync(OrderPlacementScenarios.FailureWritingOutboxRollsBackTheOrder);

    [Fact]
    public Task Lost_commit_response_is_resolved_by_idempotent_retry() => XunitScenario.RunAsync(OrderPlacementScenarios.LostCommitResponseIsResolvedByIdempotentRetry);

    [Fact]
    public Task Conflicting_duplicate_is_rejected() => XunitScenario.RunAsync(OrderPlacementScenarios.ConflictingDuplicateIsRejected);

    [Fact]
    public async Task Canceled_and_repeated_runs_do_not_leak_state_into_later_runs()
    {
        var executor = new ScenarioExecutor();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(200));

        var canceled = await executor.ExecuteAsync(OrderPlacementScenarios.PlaceOrderThenWaitUntilCanceled, cancellationToken: cancellation.Token);
        var first = await executor.ExecuteAsync(OrderPlacementScenarios.OrderAndOutboxCommitTogether, cancellationToken: TestContext.Current.CancellationToken);
        var second = await executor.ExecuteAsync(OrderPlacementScenarios.OrderAndOutboxCommitTogether, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(ScenarioOutcome.Canceled, canceled.Outcome);
        Assert.Empty(canceled.CleanupErrors);
        Assert.Equal(SimForge.SimulationEnvironmentState.Disposed, canceled.Diagnostics.EnvironmentState);
        Assert.True(first.Passed, first.FormatReport());
        Assert.True(second.Passed, second.FormatReport());
    }
}
