using SimForge.Messaging;

namespace SimForge.RabbitMq.Tests;

/// <summary>An initialized environment with one simulated broker, disposed with the test.</summary>
internal sealed class TestBroker : IAsyncDisposable
{
    private int _messageCount;

    private TestBroker(SimulationEnvironment environment, SimulatedRabbitMqBroker broker)
    {
        Environment = environment;
        Broker = broker;
    }

    public SimulationEnvironment Environment { get; }

    public SimulatedRabbitMqBroker Broker { get; }

    public SimulationScheduler Scheduler => Environment.Scheduler;

    /// <summary>
    /// Exchanges: <c>orders</c> (direct), <c>audit</c> (fanout), <c>dlx</c> (direct).
    /// Queues: <c>order-placed</c> (bound to orders/order.placed, dead-letters to dlx/dead), <c>order-placed.dead</c>
    /// (bound to dlx/dead), <c>audit-1</c> and <c>audit-2</c> (bound to audit), <c>work</c> (dead-letters to the default
    /// exchange as <c>work.dead</c>), <c>work.dead</c>, and <c>lost</c> (dead-letters to an undeclared exchange).
    /// </summary>
    public static void StandardTopology(RabbitMqTopologyBuilder topology) => topology
        .Exchange("orders", ExchangeType.Direct)
        .Exchange("audit", ExchangeType.Fanout)
        .Exchange("dlx", ExchangeType.Direct)
        .Queue("order-placed", queue => queue.DeadLetterTo("dlx", "dead"))
        .Queue("order-placed.dead")
        .Queue("audit-1")
        .Queue("audit-2")
        .Queue("work", queue => queue.DeadLetterTo("", "work.dead"))
        .Queue("work.dead")
        .Queue("lost", queue => queue.DeadLetterTo("missing-exchange"))
        .Bind("order-placed", "orders", "order.placed")
        .Bind("order-placed.dead", "dlx", "dead")
        .Bind("audit-1", "audit")
        .Bind("audit-2", "audit");

    public static async Task<TestBroker> CreateAsync(Action<RabbitMqTopologyBuilder>? topology = null, SimulationEnvironmentOptions? options = null, CancellationToken cancellationToken = default)
    {
        var environment = new SimulationEnvironment(options ?? new SimulationEnvironmentOptions { ScenarioId = "rabbitmq-tests" });
        var broker = environment.AddRabbitMqBroker("broker", topology ?? StandardTopology);
        await environment.InitializeAsync(cancellationToken);
        return new TestBroker(environment, broker);
    }

    public MessageEnvelope Message(string? id = null) => MessageEnvelope.FromText(id ?? $"m-{++_messageCount}", "payload");

    /// <summary>Publishes a text message to the default exchange, which routes it to <paramref name="queue"/>.</summary>
    public PublishConfirmation Send(string queue, string? id = null) => Broker.Publish("", queue, Message(id));

    /// <summary>Opens a channel with a consumer that records deliveries and never settles them itself.</summary>
    public (RabbitMqChannel Channel, List<RabbitMqDelivery> Deliveries) Recorder(string queue, ushort prefetch = 10)
    {
        var channel = Broker.OpenChannel();
        var deliveries = new List<RabbitMqDelivery>();
        channel.Consume(queue, prefetch, (delivery, _) =>
        {
            deliveries.Add(delivery);
            return ValueTask.CompletedTask;
        });
        return (channel, deliveries);
    }

    public ValueTask DisposeAsync() => Environment.DisposeAsync();
}
