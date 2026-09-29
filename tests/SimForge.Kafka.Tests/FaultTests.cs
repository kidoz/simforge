using Xunit;
using static SimForge.Kafka.Tests.TestCluster;

namespace SimForge.Kafka.Tests;

public sealed class FaultTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Produce_fault_before_append_writes_nothing()
    {
        await using var test = await TestCluster.CreateAsync(cancellationToken: Token);
        test.Cluster.InjectFault(KafkaOperations.Produce, reason: "leader unavailable");

        var exception = Assert.Throws<SimulatedFaultException>(() => test.Produce(0));

        Assert.False(exception.StateChanged);
        Assert.Equal(0, test.Cluster.GetPartition("orders", 0).LogEndOffset);
    }

    [Fact]
    public async Task Lost_produce_acknowledgement_makes_a_retry_append_a_duplicate()
    {
        await using var test = await TestCluster.CreateAsync(cancellationToken: Token);
        test.Cluster.InjectFault(KafkaOperations.Produce, FaultPhases.After, reason: "acknowledgement lost");

        var exception = Assert.Throws<SimulatedFaultException>(() => test.Produce(0, "m-1"));
        test.Produce(0, "m-1");

        Assert.True(exception.StateChanged);
        Assert.Equal(["m-1", "m-1"], test.Cluster.GetPartition("orders", 0).Records.Select(record => record.Message.MessageId));
    }

    [Fact]
    public async Task Lost_offset_commit_leads_to_redelivery_after_restart()
    {
        await using var test = await TestCluster.CreateAsync(cancellationToken: Token);
        test.Produce(0, "m-1");
        var member = await test.JoinAsync(cancellationToken: Token);
        Assert.Single(member.Poll());
        test.Cluster.InjectFault(KafkaOperations.Commit, reason: "coordinator unavailable");

        var exception = Assert.Throws<SimulatedFaultException>(() => member.Commit());
        member.Close();
        var restarted = await test.JoinAsync(cancellationToken: Token);

        Assert.False(exception.StateChanged);
        Assert.Null(restarted.Committed(Orders(0)));
        Assert.Equal(["m-1"], restarted.Poll().Select(record => record.Message.MessageId));
    }

    [Fact]
    public async Task Commit_fault_after_storing_reports_an_ambiguous_outcome()
    {
        await using var test = await TestCluster.CreateAsync(cancellationToken: Token);
        test.Produce(0);
        var member = await test.JoinAsync(cancellationToken: Token);
        member.Poll();
        test.Cluster.InjectFault(KafkaOperations.Commit, FaultPhases.After);

        var exception = Assert.Throws<SimulatedFaultException>(() => member.Commit());

        Assert.True(exception.StateChanged);
        Assert.Equal(1, member.Committed(Orders(0)));
    }

    [Fact]
    public async Task Fault_rules_for_undeclared_points_are_rejected()
    {
        await using var test = await TestCluster.CreateAsync(cancellationToken: Token);

        Assert.Throws<UnsupportedCapabilityException>(() => test.Cluster.InjectFault(KafkaOperations.Poll));
        Assert.Throws<UnsupportedCapabilityException>(() => test.Cluster.InjectFault(KafkaOperations.Rebalance));
    }
}
