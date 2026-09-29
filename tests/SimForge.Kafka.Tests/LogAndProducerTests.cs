using SimForge.Messaging;
using Xunit;

namespace SimForge.Kafka.Tests;

public sealed class LogAndProducerTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Offsets_are_monotonic_per_partition_and_start_at_zero()
    {
        await using var test = await TestCluster.CreateAsync(cancellationToken: Token);

        var offsets = new[] { test.Produce(0), test.Produce(1), test.Produce(0), test.Produce(0), test.Produce(1) }
            .Select(result => (result.Partition, result.Offset));

        Assert.Equal([(0, 0L), (1, 0L), (0, 1L), (0, 2L), (1, 1L)], offsets);
        Assert.Equal(3, test.Cluster.GetPartition("orders", 0).LogEndOffset);
        Assert.Equal(0, test.Cluster.GetPartition("orders", 2).LogEndOffset);
    }

    [Fact]
    public async Task Record_timestamps_are_the_virtual_append_time()
    {
        await using var test = await TestCluster.CreateAsync(cancellationToken: Token);
        test.Produce(0);
        await test.Scheduler.AdvanceByAsync(TimeSpan.FromMinutes(5), Token);

        var later = test.Produce(0);

        Assert.Equal(SimulationEnvironmentOptions.DefaultStartTime.AddMinutes(5), later.Timestamp);
        Assert.Equal(later.Timestamp, test.Cluster.GetPartition("orders", 0).Records[1].Timestamp);
    }

    [Theory]
    [InlineData("order-1", 3, 1)]
    [InlineData("order-2", 3, 1)]
    [InlineData("order-3", 3, 0)]
    [InlineData("customer-42", 3, 2)]
    [InlineData("order-1", 8, 5)]
    [InlineData("", 8, 5)]
    public void Key_partitioner_follows_the_documented_algorithm(string key, int partitions, int expected)
    {
        // Reference values computed independently: (FNV-1a 32-bit of the UTF-8 key & 0x7FFFFFFF) % partitions.
        Assert.Equal(expected, SimulatedKafkaCluster.PartitionForKey(key, partitions));
    }

    [Fact]
    public async Task Records_with_the_same_key_land_on_the_same_partition()
    {
        await using var test = await TestCluster.CreateAsync(cancellationToken: Token);

        var first = test.Cluster.Produce("orders", "customer-42", test.Message());
        var second = test.Cluster.Produce("orders", "customer-42", test.Message());

        Assert.Equal((2, 2), (first.Partition, second.Partition));
        Assert.Equal([0L, 1L], test.Cluster.GetPartition("orders", 2).Records.Select(record => record.Offset));
        Assert.All(test.Cluster.GetPartition("orders", 2).Records, record => Assert.Equal("customer-42", record.Key));
    }

    [Fact]
    public async Task Unknown_topics_and_partitions_fail_before_any_state_changes()
    {
        await using var test = await TestCluster.CreateAsync(cancellationToken: Token);

        var topic = Assert.Throws<KafkaException>(() => test.Cluster.Produce("missing", 0, test.Message()));
        var partition = Assert.Throws<KafkaException>(() => test.Cluster.Produce("orders", 3, test.Message()));
        var keyed = Assert.Throws<KafkaException>(() => test.Cluster.Produce("missing", "key", test.Message()));
        var subscription = Assert.Throws<KafkaException>(() => test.Cluster.JoinGroup("g", ["missing"], KafkaOffsetReset.Earliest));

        Assert.All([topic, partition, keyed, subscription], exception => Assert.Equal(KafkaErrorCodes.UnknownTopicOrPartition, exception.ErrorCode));
        Assert.All([0, 1, 2], index => Assert.Equal(0, test.Cluster.GetPartition("orders", index).LogEndOffset));
        Assert.Throws<KeyNotFoundException>(() => test.Cluster.GetGroup("g"));
    }

    [Fact]
    public async Task Invalid_topology_is_rejected()
    {
        await using var environment = new SimulationEnvironment();

        Assert.Throws<ArgumentException>(() => environment.AddKafkaCluster("a", topics => topics.Topic("bad name", 1)));
        Assert.Throws<ArgumentException>(() => environment.AddKafkaCluster("b", topics => topics.Topic("..", 1)));
        Assert.Throws<ArgumentException>(() => environment.AddKafkaCluster("c", topics => topics.Topic(new string('t', 250), 1)));
        Assert.Throws<ArgumentException>(() => environment.AddKafkaCluster("d", topics => topics.Topic("t", 1).Topic("t", 2)));
        Assert.Throws<ArgumentOutOfRangeException>(() => environment.AddKafkaCluster("e", topics => topics.Topic("t", 0)));
        Assert.Throws<ArgumentException>(() => environment.AddKafkaCluster("f", _ => { }));
        var reserved = Assert.Throws<UnsupportedCapabilityException>(() => environment.AddKafkaCluster("g", topics => topics.Topic("__consumer_offsets", 1)));

        Assert.Equal(KafkaCapabilityIds.TopicManagement, reserved.CapabilityId);
        Assert.Empty(environment.Resources);
    }

    [Fact]
    public async Task Stored_records_are_isolated_from_the_producer()
    {
        await using var test = await TestCluster.CreateAsync(cancellationToken: Token);
        var body = "original"u8.ToArray();

        test.Cluster.Produce("audit", 0, new MessageEnvelope("m-1", body));
        body[0] = (byte)'X';

        Assert.Equal("original", test.Cluster.GetPartition("audit", 0).Records[0].Message.GetBodyAsText());
    }

    [Fact]
    public async Task Produce_is_journaled_with_correlation_and_payload_only_when_captured()
    {
        await using var quiet = await TestCluster.CreateAsync(cancellationToken: Token);
        await using var capturing = await TestCluster.CreateAsync(new SimulationEnvironmentOptions { CapturePayloads = true }, Token);
        var message = MessageEnvelope.FromText("m-1", "secret body") with { CorrelationId = "order:9" };

        quiet.Cluster.Produce("orders", 1, message, key: "k");
        capturing.Cluster.Produce("orders", 1, message, key: "k");

        var quietEntry = quiet.Environment.Journal.GetEntries().Single(entry => entry.Operation == KafkaOperations.Produce);
        var capturedEntry = capturing.Environment.Journal.GetEntries().Single(entry => entry.Operation == KafkaOperations.Produce);
        Assert.Equal(("kafka", "orders-1", "order:9"), (quietEntry.Provider, quietEntry.Target, quietEntry.CorrelationId));
        Assert.Contains("offset=0", quietEntry.Details, StringComparison.Ordinal);
        Assert.Null(quietEntry.Payload);
        Assert.Contains("secret body", capturedEntry.Payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cluster_is_unusable_before_initialization_and_after_disposal()
    {
        var environment = new SimulationEnvironment();
        var cluster = environment.AddKafkaCluster("kafka", topics => topics.Topic("orders", 1));
        Assert.Throws<InvalidOperationException>(() => cluster.Produce("orders", 0, MessageEnvelope.FromText("m", "x")));

        await environment.InitializeAsync(Token);
        var member = cluster.JoinGroup("g", ["orders"], KafkaOffsetReset.Earliest);
        await environment.DisposeAsync();

        Assert.Throws<ObjectDisposedException>(() => cluster.Produce("orders", 0, MessageEnvelope.FromText("m", "x")));
        Assert.True(member.IsClosed);
        member.Dispose();
    }

    [Fact]
    public async Task Parallel_environments_with_identical_cluster_names_are_isolated()
    {
        async Task<long> RunAsync(int records)
        {
            await using var test = await TestCluster.CreateAsync(cancellationToken: Token);
            for (var index = 0; index < records; index++)
            {
                test.Produce(0);
                await Task.Yield();
            }

            return test.Cluster.GetPartition("orders", 0).LogEndOffset;
        }

        var ends = await Task.WhenAll(Task.Run(() => RunAsync(40), Token), Task.Run(() => RunAsync(90), Token));

        Assert.Equal([40L, 90L], ends);
    }
}
