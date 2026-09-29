using SimForge.Messaging;
using Xunit;

namespace SimForge.RabbitMq.Tests;

public sealed class TopologyAndRoutingTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Default_exchange_routes_to_the_queue_named_by_the_routing_key()
    {
        await using var test = await TestBroker.CreateAsync(cancellationToken: Token);

        var routed = test.Send("work");
        var dropped = test.Broker.Publish("", "no-such-queue", test.Message());
        var returned = test.Broker.Publish("", "no-such-queue", test.Message(), mandatory: true);

        Assert.Equal((PublishOutcome.Routed, "work"), (routed.Outcome, Assert.Single(routed.Queues)));
        Assert.Equal(PublishOutcome.Dropped, dropped.Outcome);
        Assert.Equal(PublishOutcome.Returned, returned.Outcome);
        Assert.Empty(returned.Queues);
        Assert.Equal(1, test.Broker.GetQueue("work").ReadyCount);
    }

    [Fact]
    public async Task Direct_exchange_routes_only_exact_routing_key_matches()
    {
        await using var test = await TestBroker.CreateAsync(cancellationToken: Token);

        var matching = test.Broker.Publish("orders", "order.placed", test.Message());
        var other = test.Broker.Publish("orders", "order.cancelled", test.Message(), mandatory: true);

        Assert.Equal(["order-placed"], matching.Queues);
        Assert.Equal(PublishOutcome.Returned, other.Outcome);
        Assert.Equal(1, test.Broker.GetQueue("order-placed").ReadyCount);
    }

    [Fact]
    public async Task Fanout_exchange_enqueues_one_copy_per_bound_queue_and_ignores_the_routing_key()
    {
        await using var test = await TestBroker.CreateAsync(cancellationToken: Token);

        var confirmation = test.Broker.Publish("audit", "anything", test.Message("m-1"));

        Assert.Equal(["audit-1", "audit-2"], confirmation.Queues);
        Assert.Equal("m-1", Assert.Single(test.Broker.GetQueue("audit-1").ReadyMessages).MessageId);
        Assert.Equal("m-1", Assert.Single(test.Broker.GetQueue("audit-2").ReadyMessages).MessageId);
    }

    [Fact]
    public async Task Publishing_to_an_undeclared_or_reserved_exchange_fails_before_any_state_changes()
    {
        await using var test = await TestBroker.CreateAsync(cancellationToken: Token);

        var missing = Assert.Throws<RabbitMqException>(() => test.Broker.Publish("missing", "work", test.Message()));
        var reserved = Assert.Throws<UnsupportedCapabilityException>(() => test.Broker.Publish("amq.direct", "work", test.Message()));

        Assert.Equal(RabbitMqErrorCodes.NotFound, missing.ErrorCode);
        Assert.Equal(RabbitMqCapabilityIds.ReservedNames, reserved.CapabilityId);
        Assert.All(test.Broker.Topology.Queues, queue => Assert.Equal(0, test.Broker.GetQueue(queue.Name).ReadyCount));
    }

    [Fact]
    public async Task Confirmation_is_independent_of_consumers_and_acknowledgement()
    {
        await using var test = await TestBroker.CreateAsync(cancellationToken: Token);

        var confirmation = test.Send("work");

        Assert.Equal(PublishOutcome.Routed, confirmation.Outcome);
        var queue = test.Broker.GetQueue("work");
        Assert.Equal((1, 0, 0), (queue.ReadyCount, queue.UnackedCount, queue.ConsumerCount));
    }

    [Theory]
    [InlineData(ExchangeType.Topic, RabbitMqCapabilityIds.TopicRouting)]
    [InlineData(ExchangeType.Headers, RabbitMqCapabilityIds.HeadersRouting)]
    public async Task Topic_and_headers_exchanges_are_unsupported(ExchangeType type, string capability)
    {
        await using var environment = new SimulationEnvironment();

        var exception = Assert.Throws<UnsupportedCapabilityException>(
            () => environment.AddRabbitMqBroker("broker", topology => topology.Exchange("events", type).Queue("q")));

        Assert.Equal(capability, exception.CapabilityId);
        Assert.Empty(environment.Resources);
    }

    [Fact]
    public async Task Invalid_topology_is_rejected()
    {
        await using var environment = new SimulationEnvironment();

        Assert.Throws<UnsupportedCapabilityException>(() => environment.AddRabbitMqBroker("a", topology => topology.Queue("amq.gen-1")));
        Assert.Throws<UnsupportedCapabilityException>(() => environment.AddRabbitMqBroker("b", topology => topology.Exchange("amq.topic", ExchangeType.Direct).Queue("q")));
        Assert.Throws<ArgumentException>(() => environment.AddRabbitMqBroker("c", topology => topology.Queue("q").Bind("q", "undeclared")));
        Assert.Throws<ArgumentException>(() => environment.AddRabbitMqBroker("d", topology => topology.Exchange("x", ExchangeType.Direct).Bind("q", "x")));
        Assert.Throws<ArgumentException>(() => environment.AddRabbitMqBroker("e", topology => topology.Queue("q").Bind("q", "")));
        Assert.Throws<ArgumentException>(() => environment.AddRabbitMqBroker("f", topology => topology.Queue("q").Queue("q")));
        Assert.Throws<ArgumentException>(() => environment.AddRabbitMqBroker("g", topology => topology.Exchange("x", ExchangeType.Direct)));
        Assert.Throws<ArgumentException>(() => environment.AddRabbitMqBroker("h", topology => topology.Queue(new string('q', 256))));
        Assert.Empty(environment.Resources);
    }

    [Fact]
    public async Task Repeated_bindings_are_ignored()
    {
        await using var test = await TestBroker.CreateAsync(topology => topology
            .Exchange("x", ExchangeType.Direct)
            .Queue("q")
            .Bind("q", "x", "k")
            .Bind("q", "x", "k"), cancellationToken: Token);

        var confirmation = test.Broker.Publish("x", "k", test.Message());

        Assert.Single(test.Broker.Topology.Bindings);
        Assert.Equal(["q"], confirmation.Queues);
        Assert.Equal(1, test.Broker.GetQueue("q").ReadyCount);
    }

    [Fact]
    public async Task Publish_is_journaled_with_the_message_correlation_and_payload_only_when_captured()
    {
        await using var quiet = await TestBroker.CreateAsync(cancellationToken: Token);
        await using var capturing = await TestBroker.CreateAsync(options: new SimulationEnvironmentOptions { CapturePayloads = true }, cancellationToken: Token);
        var message = MessageEnvelope.FromText("m-1", "secret body") with { CorrelationId = "order:42" };

        quiet.Broker.Publish("", "work", message);
        capturing.Broker.Publish("", "work", message);

        var quietEntry = quiet.Environment.Journal.GetEntries().Single(entry => entry.Operation == RabbitMqOperations.Publish);
        var capturedEntry = capturing.Environment.Journal.GetEntries().Single(entry => entry.Operation == RabbitMqOperations.Publish);
        Assert.Equal(("rabbitmq", "broker", "order:42"), (quietEntry.Provider, quietEntry.Resource, quietEntry.CorrelationId));
        Assert.Contains("outcome=Routed", quietEntry.Details, StringComparison.Ordinal);
        Assert.Null(quietEntry.Payload);
        Assert.Contains("secret body", capturedEntry.Payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Parallel_environments_with_identical_broker_names_are_isolated()
    {
        async Task<int> RunAsync(int messages)
        {
            await using var test = await TestBroker.CreateAsync(cancellationToken: Token);
            for (var index = 0; index < messages; index++)
            {
                test.Send("work");
                await Task.Yield();
            }

            return test.Broker.GetQueue("work").ReadyCount;
        }

        var counts = await Task.WhenAll(Task.Run(() => RunAsync(30), Token), Task.Run(() => RunAsync(70), Token));

        Assert.Equal([30, 70], counts);
    }

    [Fact]
    public async Task Broker_is_unusable_before_initialization_and_after_disposal()
    {
        var environment = new SimulationEnvironment();
        var broker = environment.AddRabbitMqBroker("broker", TestBroker.StandardTopology);

        Assert.Throws<InvalidOperationException>(() => broker.Publish("", "work", MessageEnvelope.FromText("m", "x")));
        await environment.InitializeAsync(Token);
        var channel = broker.OpenChannel();
        await environment.DisposeAsync();

        Assert.Throws<ObjectDisposedException>(() => broker.Publish("", "work", MessageEnvelope.FromText("m", "x")));
        Assert.Throws<ObjectDisposedException>(() => broker.OpenChannel());
        Assert.False(channel.IsOpen);
        channel.Dispose();
    }
}
