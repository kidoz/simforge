using SimForge.Assertions;
using SimForge.Kafka;
using SimForge.Messaging;
using SimForge.RabbitMq;
using SimForge.Testing;
using SimForge.Xunit;
using Xunit;

namespace Orders.Simulated.Tests.ReadmeMessaging;

// Mirrors the RabbitMQ and Kafka code blocks in the repository README; the lines between the markers are copied verbatim.
public sealed record OrderPlaced(Guid OrderId, long TotalCents);

public sealed class OrderPlacedHandler
{
    public List<OrderPlaced> Handled { get; } = [];

    public Task HandleAsync(OrderPlaced message, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Handled.Add(message);
        return Task.CompletedTask;
    }
}

public static class MessagingScenarios
{
    public static Scenario ConsumeOrderPlaced { get; } = Scenario.Create(
        "readme.rabbitmq.consume",
        setup => setup.Environment.AddRabbitMqBroker("broker", topology => topology
            .Exchange("orders", ExchangeType.Direct)
            .Queue("order-placed")
            .Bind("order-placed", "orders", "order.placed")),
        async (context, cancellationToken) =>
        {
            var handler = new OrderPlacedHandler();
            var orderId = context.Ids.NewGuid();

            // README start
            var broker = context.GetResource<SimulatedRabbitMqBroker>("broker");
            broker.OpenChannel().Consume("order-placed", prefetchCount: 10, async (delivery, cancellationToken) =>
            {
                await handler.HandleAsync(delivery.Message.ReadJson<OrderPlaced>(), cancellationToken);
                delivery.Ack();
            });

            broker.Publish("orders", "order.placed", MessageEnvelope.FromJson("msg-1", new OrderPlaced(orderId, 4_250)));
            await context.Scheduler.RunUntilIdleAsync(cancellationToken); // deliver
            // README end

            SimAssert.Equal(orderId, SimAssert.Single(handler.Handled).OrderId);
            SimAssert.Equal(0, broker.GetQueue("order-placed").UnackedCount);
        });
}

public static class KafkaReadmeScenarios
{
    public static Scenario ConsumeOrderPlaced { get; } = Scenario.Create(
        "readme.kafka.consume",
        setup => setup.Environment.AddKafkaCluster("kafka", topics => topics.Topic("orders", partitions: 3)),
        async (context, cancellationToken) =>
        {
            var handler = new OrderPlacedHandler();
            var orderId = context.Ids.NewGuid();

            // README start
            var cluster = context.GetResource<SimulatedKafkaCluster>("kafka");
            var consumer = cluster.JoinGroup("billing", ["orders"], KafkaOffsetReset.Earliest);
            await context.Scheduler.RunUntilIdleAsync(cancellationToken); // runs the rebalance

            cluster.Produce("orders", orderId.ToString(), MessageEnvelope.FromJson("msg-1", new OrderPlaced(orderId, 4_250)));

            foreach (var record in consumer.Poll())
            {
                await handler.HandleAsync(record.Message.ReadJson<OrderPlaced>(), cancellationToken);
            }

            consumer.Commit(); // stores the next offset to read; without it, a restart reads the records again
            // README end

            SimAssert.Equal(orderId, SimAssert.Single(handler.Handled).OrderId);
            var partition = SimulatedKafkaCluster.PartitionForKey(orderId.ToString(), 3);
            SimAssert.Equal(1L, consumer.Committed(new TopicPartition("orders", partition)));
        });
}

public sealed class ReadmeMessagingTests
{
    [Fact]
    public Task Consume_order_placed() => XunitScenario.RunAsync(MessagingScenarios.ConsumeOrderPlaced);

    [Fact]
    public Task Consume_order_placed_from_kafka() => XunitScenario.RunAsync(KafkaReadmeScenarios.ConsumeOrderPlaced);
}
