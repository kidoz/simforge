using Xunit;
using static SimForge.Kafka.Tests.TestCluster;

namespace SimForge.Kafka.Tests;

public sealed class ConsumerGroupTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Membership_takes_effect_only_when_the_scheduler_runs_the_rebalance()
    {
        await using var test = await TestCluster.CreateAsync(cancellationToken: Token);
        test.Produce(0);

        var member = test.Cluster.JoinGroup("g", ["orders"], KafkaOffsetReset.Earliest);

        Assert.Empty(member.Assignment);
        Assert.Empty(member.Poll());
        Assert.True(test.Cluster.GetGroup("g").RebalancePending);

        await test.Scheduler.RunUntilIdleAsync(Token);

        Assert.Equal([Orders(0), Orders(1), Orders(2)], member.Assignment);
        Assert.Single(member.Poll());
        Assert.Equal((1, false), (test.Cluster.GetGroup("g").Generation, test.Cluster.GetGroup("g").RebalancePending));
    }

    [Fact]
    public async Task Range_assignment_gives_each_partition_to_exactly_one_member_in_join_order()
    {
        await using var test = await TestCluster.CreateAsync(cancellationToken: Token);
        var first = await test.JoinAsync(cancellationToken: Token);
        var second = await test.JoinAsync(cancellationToken: Token);
        var third = await test.JoinAsync(cancellationToken: Token);
        var fourth = await test.JoinAsync(cancellationToken: Token);

        Assert.Equal([Orders(0)], first.Assignment);
        Assert.Equal([Orders(1)], second.Assignment);
        Assert.Equal([Orders(2)], third.Assignment);
        Assert.Empty(fourth.Assignment);
        Assert.Equal(["g-member-1", "g-member-2", "g-member-3", "g-member-4"], test.Cluster.GetGroup("g").Members.Select(member => member.MemberId));

        third.Close();
        fourth.Close();
        await test.Scheduler.RunUntilIdleAsync(Token);
        Assert.Equal([Orders(0), Orders(1)], first.Assignment);
        Assert.Equal([Orders(2)], second.Assignment);
    }

    [Fact]
    public async Task Independent_groups_each_read_every_record()
    {
        await using var test = await TestCluster.CreateAsync(cancellationToken: Token);
        test.Produce(0, "m-1");
        test.Produce(2, "m-2");
        var billing = await test.JoinAsync("billing", cancellationToken: Token);
        var shipping = await test.JoinAsync("shipping", cancellationToken: Token);

        Assert.Equal(["m-1", "m-2"], billing.Poll().Select(record => record.Message.MessageId).Order());
        Assert.Equal(["m-1", "m-2"], shipping.Poll().Select(record => record.Message.MessageId).Order());
    }

    [Fact]
    public async Task Poll_returns_records_in_offset_order_and_advances_only_the_read_position()
    {
        await using var test = await TestCluster.CreateAsync(cancellationToken: Token);
        test.Produce(1, "m-1");
        test.Produce(1, "m-2");
        var member = await test.JoinAsync(cancellationToken: Token);

        var first = member.Poll();
        test.Produce(1, "m-3");
        var second = member.Poll();

        Assert.Equal(["m-1", "m-2"], first.Select(record => record.Message.MessageId));
        Assert.Equal([0L, 1L], first.Select(record => record.Offset));
        Assert.Equal(["m-3"], second.Select(record => record.Message.MessageId));
        Assert.Equal(3, member.Position(Orders(1)));
        Assert.Null(member.Committed(Orders(1)));
    }

    [Fact]
    public async Task Commit_stores_the_next_offset_to_consume_for_every_resolved_partition()
    {
        await using var test = await TestCluster.CreateAsync(cancellationToken: Token);
        test.Produce(0);
        test.Produce(0);
        var member = await test.JoinAsync(cancellationToken: Token);
        member.Poll();

        member.Commit();

        // Like commitSync(), Commit() stores the position of every assigned partition with a resolved position,
        // including partitions that returned no records.
        Assert.Equal(2, member.Committed(Orders(0)));
        Assert.Equal(
            [(Orders(0), 2L), (Orders(1), 0L), (Orders(2), 0L)],
            test.Cluster.GetGroup("g").CommittedOffsets.Select(pair => (pair.Key, pair.Value)));
    }

    [Fact]
    public async Task Restart_after_processing_but_before_commit_redelivers_from_the_committed_offset()
    {
        await using var test = await TestCluster.CreateAsync(cancellationToken: Token);
        test.Produce(0, "m-1");
        test.Produce(0, "m-2");
        test.Produce(0, "m-3");
        var crashed = await test.JoinAsync(cancellationToken: Token);
        Assert.Equal(3, crashed.Poll().Count);
        crashed.Commit(Orders(0), 1);

        crashed.Close();
        var restarted = await test.JoinAsync(cancellationToken: Token);

        Assert.Equal(["m-2", "m-3"], restarted.Poll().Select(record => record.Message.MessageId));
    }

    [Fact]
    public async Task Rebalance_resets_positions_to_committed_offsets_so_uncommitted_progress_is_read_again()
    {
        await using var test = await TestCluster.CreateAsync(cancellationToken: Token);
        test.Produce(0, "m-1");
        test.Produce(0, "m-2");
        var first = await test.JoinAsync(cancellationToken: Token);
        first.Poll();
        first.Commit(Orders(0), 1);

        await test.JoinAsync(cancellationToken: Token);

        Assert.Contains(Orders(0), first.Assignment);
        Assert.Equal(["m-2"], first.Poll().Select(record => record.Message.MessageId));
    }

    [Fact]
    public async Task Committing_a_partition_lost_in_a_rebalance_fails_without_changing_offsets()
    {
        await using var test = await TestCluster.CreateAsync(cancellationToken: Token);
        test.Produce(2);
        var first = await test.JoinAsync(cancellationToken: Token);
        first.Poll();
        await test.JoinAsync(cancellationToken: Token);

        var exception = Assert.Throws<KafkaException>(() => first.Commit(Orders(2), 1));

        Assert.Equal(KafkaErrorCodes.CommitFailed, exception.ErrorCode);
        Assert.DoesNotContain(Orders(2), first.Assignment);
        Assert.Empty(test.Cluster.GetGroup("g").CommittedOffsets);
    }

    [Fact]
    public async Task Seek_replays_and_seek_to_end_skips_existing_records()
    {
        await using var test = await TestCluster.CreateAsync(cancellationToken: Token);
        test.Produce(0, "m-1");
        test.Produce(0, "m-2");
        var member = await test.JoinAsync(cancellationToken: Token);
        member.Poll();

        member.Seek(Orders(0), 1);
        var replay = member.Poll();
        member.SeekToBeginning(Orders(0));
        var fromStart = member.Poll();
        member.SeekToEnd(Orders(0));
        var atEnd = member.Poll();
        test.Produce(0, "m-3");

        Assert.Equal(["m-2"], replay.Select(record => record.Message.MessageId));
        Assert.Equal(["m-1", "m-2"], fromStart.Select(record => record.Message.MessageId));
        Assert.Empty(atEnd);
        Assert.Equal(["m-3"], member.Poll().Select(record => record.Message.MessageId));
    }

    [Theory]
    [InlineData(KafkaOffsetReset.Earliest, new[] { "m-1", "m-2" })]
    [InlineData(KafkaOffsetReset.Latest, new string[0])]
    public async Task Reset_policy_decides_where_a_partition_without_a_committed_offset_starts(KafkaOffsetReset reset, string[] expected)
    {
        await using var test = await TestCluster.CreateAsync(cancellationToken: Token);
        test.Produce(0, "m-1");
        test.Produce(0, "m-2");
        var member = await test.JoinAsync(reset: reset, cancellationToken: Token);

        var records = member.Poll();
        test.Produce(0, "m-3");

        Assert.Equal(expected, records.Select(record => record.Message.MessageId));
        Assert.Equal(["m-3"], member.Poll().Select(record => record.Message.MessageId));
    }

    [Fact]
    public async Task Reset_policy_none_fails_without_consuming_anything()
    {
        await using var test = await TestCluster.CreateAsync(cancellationToken: Token);
        test.Produce(0);
        var member = await test.JoinAsync(reset: KafkaOffsetReset.None, cancellationToken: Token);

        var noOffset = Assert.Throws<KafkaException>(() => member.Poll());
        member.Seek(Orders(0), 0);
        member.Seek(Orders(1), 0);
        member.Seek(Orders(2), 0);
        member.Seek(Orders(0), 5);
        var outOfRange = Assert.Throws<KafkaException>(() => member.Poll());

        Assert.Equal(KafkaErrorCodes.NoOffset, noOffset.ErrorCode);
        Assert.Equal(KafkaErrorCodes.OffsetOutOfRange, outOfRange.ErrorCode);
        Assert.Equal(0, member.Position(Orders(1)));
    }

    [Fact]
    public async Task Position_beyond_the_log_end_is_reset_by_the_policy()
    {
        await using var test = await TestCluster.CreateAsync(cancellationToken: Token);
        test.Produce(0, "m-1");
        var member = await test.JoinAsync(cancellationToken: Token);

        member.Seek(Orders(0), 10);

        Assert.Equal(["m-1"], member.Poll().Select(record => record.Message.MessageId));
    }

    [Fact]
    public async Task Poll_respects_max_records_and_rotates_across_partitions()
    {
        await using var test = await TestCluster.CreateAsync(cancellationToken: Token);
        for (var round = 0; round < 3; round++)
        {
            test.Produce(0);
            test.Produce(1);
            test.Produce(2);
        }

        var member = await test.JoinAsync(cancellationToken: Token);

        var partitions = Enumerable.Range(0, 3).Select(_ => member.Poll(maxRecords: 1).Single().Partition).ToList();

        Assert.Equal([0, 1, 2], partitions);
        Assert.Equal(6, member.Poll(maxRecords: 100).Count);
    }

    [Fact]
    public async Task Invalid_member_operations_are_rejected()
    {
        await using var test = await TestCluster.CreateAsync(cancellationToken: Token);
        var first = await test.JoinAsync(cancellationToken: Token);
        var second = await test.JoinAsync(cancellationToken: Token);

        Assert.Throws<InvalidOperationException>(() => second.Position(Orders(0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => first.Seek(Orders(0), -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => first.Poll(maxRecords: 0));
        Assert.Throws<ArgumentException>(() => test.Cluster.JoinGroup("g", [], KafkaOffsetReset.Earliest));

        first.Close();
        Assert.Throws<InvalidOperationException>(() => first.Poll());
        Assert.Throws<InvalidOperationException>(() => first.Commit());
    }
}
