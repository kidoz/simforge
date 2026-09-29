using SimForge.Messaging;

namespace SimForge.Kafka.Tests;

/// <summary>An initialized environment with one simulated cluster: <c>orders</c> (3 partitions) and <c>audit</c> (1 partition).</summary>
internal sealed class TestCluster : IAsyncDisposable
{
    private int _messageCount;

    private TestCluster(SimulationEnvironment environment, SimulatedKafkaCluster cluster)
    {
        Environment = environment;
        Cluster = cluster;
    }

    public SimulationEnvironment Environment { get; }

    public SimulatedKafkaCluster Cluster { get; }

    public SimulationScheduler Scheduler => Environment.Scheduler;

    public static TopicPartition Orders(int partition) => new("orders", partition);

    public static async Task<TestCluster> CreateAsync(SimulationEnvironmentOptions? options = null, CancellationToken cancellationToken = default)
    {
        var environment = new SimulationEnvironment(options ?? new SimulationEnvironmentOptions { ScenarioId = "kafka-tests" });
        var cluster = environment.AddKafkaCluster("kafka", topics => topics.Topic("orders", 3).Topic("audit", 1));
        await environment.InitializeAsync(cancellationToken);
        return new TestCluster(environment, cluster);
    }

    public MessageEnvelope Message(string? id = null) => MessageEnvelope.FromText(id ?? $"m-{++_messageCount}", "payload");

    public ProduceResult Produce(int partition, string? id = null) => Cluster.Produce("orders", partition, Message(id));

    /// <summary>Joins a member and runs the resulting rebalance.</summary>
    public async Task<KafkaConsumer> JoinAsync(string group = "g", KafkaOffsetReset reset = KafkaOffsetReset.Earliest, CancellationToken cancellationToken = default)
    {
        var member = Cluster.JoinGroup(group, ["orders"], reset);
        await Scheduler.RunUntilIdleAsync(cancellationToken);
        return member;
    }

    public ValueTask DisposeAsync() => Environment.DisposeAsync();
}
