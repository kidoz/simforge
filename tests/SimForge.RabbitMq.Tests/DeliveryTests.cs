using SimForge.Messaging;
using Xunit;

namespace SimForge.RabbitMq.Tests;

public sealed class DeliveryTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Deliveries_happen_only_while_the_scheduler_is_driven()
    {
        await using var test = await TestBroker.CreateAsync(cancellationToken: Token);
        var (_, deliveries) = test.Recorder("work");

        test.Send("work", "m-1");
        Assert.Empty(deliveries);

        await test.Scheduler.RunUntilIdleAsync(Token);

        var delivery = Assert.Single(deliveries);
        Assert.Equal(("m-1", 1UL, false, "work"), (delivery.Message.MessageId, delivery.DeliveryTag, delivery.Redelivered, delivery.Queue));
        Assert.Equal((0, 1), (test.Broker.GetQueue("work").ReadyCount, test.Broker.GetQueue("work").UnackedCount));
    }

    [Fact]
    public async Task Delivery_tags_are_scoped_per_channel()
    {
        await using var test = await TestBroker.CreateAsync(cancellationToken: Token);
        var (_, first) = test.Recorder("audit-1");
        var (_, second) = test.Recorder("audit-2");

        test.Broker.Publish("audit", "", test.Message());
        test.Broker.Publish("audit", "", test.Message());
        await test.Scheduler.RunUntilIdleAsync(Token);

        Assert.Equal([1UL, 2UL], first.Select(delivery => delivery.DeliveryTag));
        Assert.Equal([1UL, 2UL], second.Select(delivery => delivery.DeliveryTag));
    }

    [Fact]
    public async Task Prefetch_bounds_unacknowledged_deliveries_per_consumer()
    {
        await using var test = await TestBroker.CreateAsync(cancellationToken: Token);
        var (channel, deliveries) = test.Recorder("work", prefetch: 2);
        for (var index = 1; index <= 5; index++)
        {
            test.Send("work", $"m-{index}");
        }

        await test.Scheduler.RunUntilIdleAsync(Token);
        Assert.Equal(["m-1", "m-2"], deliveries.Select(delivery => delivery.Message.MessageId));

        channel.Ack(1);
        await test.Scheduler.RunUntilIdleAsync(Token);

        Assert.Equal(["m-1", "m-2", "m-3"], deliveries.Select(delivery => delivery.Message.MessageId));
        Assert.Equal((2, 2), (test.Broker.GetQueue("work").ReadyCount, test.Broker.GetQueue("work").UnackedCount));
    }

    [Fact]
    public async Task Consumers_of_a_queue_are_selected_round_robin()
    {
        await using var test = await TestBroker.CreateAsync(cancellationToken: Token);
        var (_, first) = test.Recorder("work");
        var (_, second) = test.Recorder("work");
        for (var index = 1; index <= 4; index++)
        {
            test.Send("work", $"m-{index}");
        }

        await test.Scheduler.RunUntilIdleAsync(Token);

        Assert.Equal(["m-1", "m-3"], first.Select(delivery => delivery.Message.MessageId));
        Assert.Equal(["m-2", "m-4"], second.Select(delivery => delivery.Message.MessageId));
    }

    [Fact]
    public async Task Ack_with_multiple_settles_every_outstanding_delivery_up_to_the_tag()
    {
        await using var test = await TestBroker.CreateAsync(cancellationToken: Token);
        var (channel, _) = test.Recorder("work");
        for (var index = 1; index <= 4; index++)
        {
            test.Send("work");
        }

        await test.Scheduler.RunUntilIdleAsync(Token);

        channel.Ack(2, multiple: true);
        Assert.Equal(2, channel.UnackedCount);
        channel.Ack(0, multiple: true);
        Assert.Equal(0, channel.UnackedCount);
        Assert.Equal(0, test.Broker.GetQueue("work").UnackedCount);
    }

    [Fact]
    public async Task Requeued_message_returns_to_its_original_position_marked_redelivered()
    {
        await using var test = await TestBroker.CreateAsync(cancellationToken: Token);
        var (_, deliveries) = test.Recorder("work", prefetch: 1);
        test.Send("work", "m-1");
        test.Send("work", "m-2");
        await test.Scheduler.RunUntilIdleAsync(Token);

        deliveries[0].Nack(requeue: true);
        await test.Scheduler.RunUntilIdleAsync(Token);

        Assert.Equal(["m-1", "m-1"], deliveries.Select(delivery => delivery.Message.MessageId));
        Assert.Equal([false, true], deliveries.Select(delivery => delivery.Redelivered));
        Assert.Equal(["m-2"], test.Broker.GetQueue("work").ReadyMessages.Select(message => message.MessageId));
    }

    [Fact]
    public async Task Rejected_message_without_a_dead_letter_exchange_is_discarded()
    {
        await using var test = await TestBroker.CreateAsync(cancellationToken: Token);
        var (_, deliveries) = test.Recorder("audit-1");
        test.Broker.Publish("audit", "", test.Message());
        await test.Scheduler.RunUntilIdleAsync(Token);

        deliveries[0].Reject(requeue: false);

        var queue = test.Broker.GetQueue("audit-1");
        Assert.Equal((0, 0), (queue.ReadyCount, queue.UnackedCount));
        Assert.Contains(test.Environment.Journal.GetEntries(), entry => entry.Operation == RabbitMqOperations.DeadLetter && entry.Details!.Contains("discarded", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Rejected_message_is_dead_lettered_with_death_headers_and_the_configured_routing_key()
    {
        await using var test = await TestBroker.CreateAsync(cancellationToken: Token);
        var (_, deliveries) = test.Recorder("order-placed");
        var (_, dead) = test.Recorder("order-placed.dead");
        test.Broker.Publish("orders", "order.placed", MessageEnvelope.FromText("m-1", "poison") with { CorrelationId = "order:7" });
        await test.Scheduler.RunUntilIdleAsync(Token);

        deliveries[0].Nack(requeue: false);
        await test.Scheduler.RunUntilIdleAsync(Token);

        var deadLettered = Assert.Single(dead);
        Assert.Equal(("m-1", "dlx", "dead", false), (deadLettered.Message.MessageId, deadLettered.Exchange, deadLettered.RoutingKey, deadLettered.Redelivered));
        Assert.Equal("order-placed", deadLettered.Message.Headers["x-first-death-queue"]);
        Assert.Equal("rejected", deadLettered.Message.Headers["x-first-death-reason"]);
        Assert.Equal("orders", deadLettered.Message.Headers["x-first-death-exchange"]);
        Assert.Equal("order:7", deadLettered.Message.CorrelationId);
        Assert.Equal("poison", deadLettered.Message.GetBodyAsText());
    }

    [Fact]
    public async Task Dead_lettering_through_the_default_exchange_and_to_a_missing_exchange()
    {
        await using var test = await TestBroker.CreateAsync(cancellationToken: Token);
        var (_, work) = test.Recorder("work");
        var (_, lost) = test.Recorder("lost");
        test.Send("work", "m-work");
        test.Send("lost", "m-lost");
        await test.Scheduler.RunUntilIdleAsync(Token);

        work[0].Reject(requeue: false);
        lost[0].Reject(requeue: false);

        Assert.Equal(["m-work"], test.Broker.GetQueue("work.dead").ReadyMessages.Select(message => message.MessageId));
        Assert.Contains(test.Environment.Journal.GetEntries(), entry => entry.Details?.Contains("dead-letter exchange not found", StringComparison.Ordinal) == true);
        Assert.All(["work", "lost"], name => Assert.Equal(0, test.Broker.GetQueue(name).UnackedCount));
    }

    [Fact]
    public async Task Unknown_or_repeated_delivery_tags_close_the_channel_and_requeue_its_deliveries()
    {
        await using var test = await TestBroker.CreateAsync(cancellationToken: Token);
        var (channel, deliveries) = test.Recorder("work");
        test.Send("work", "m-1");
        test.Send("work", "m-2");
        await test.Scheduler.RunUntilIdleAsync(Token);
        channel.Ack(1);

        var repeated = Assert.Throws<RabbitMqException>(() => channel.Ack(1));

        Assert.Equal(RabbitMqErrorCodes.PreconditionFailed, repeated.ErrorCode);
        Assert.False(channel.IsOpen);
        Assert.Contains("precondition_failed", channel.CloseReason, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => channel.Ack(2));
        Assert.Equal(["m-2"], test.Broker.GetQueue("work").ReadyMessages.Select(message => message.MessageId));
        Assert.Equal(2, deliveries.Count);
    }

    [Fact]
    public async Task Closing_a_channel_requeues_its_deliveries_for_another_consumer()
    {
        await using var test = await TestBroker.CreateAsync(cancellationToken: Token);
        var (closing, closed) = test.Recorder("work");
        test.Send("work", "m-1");
        await test.Scheduler.RunUntilIdleAsync(Token);

        closing.Close();
        var (_, survivor) = test.Recorder("work");
        await test.Scheduler.RunUntilIdleAsync(Token);

        Assert.Single(closed);
        var redelivery = Assert.Single(survivor);
        Assert.Equal(("m-1", true, 1UL), (redelivery.Message.MessageId, redelivery.Redelivered, redelivery.DeliveryTag));
        Assert.Equal(1, test.Broker.GetQueue("work").ConsumerCount);
    }

    [Fact]
    public async Task Cancelled_consumer_gets_no_new_deliveries_but_its_deliveries_can_still_be_acked()
    {
        await using var test = await TestBroker.CreateAsync(cancellationToken: Token);
        var channel = test.Broker.OpenChannel();
        var received = new List<RabbitMqDelivery>();
        var consumer = channel.Consume("work", 10, (delivery, _) =>
        {
            received.Add(delivery);
            return ValueTask.CompletedTask;
        });
        test.Send("work", "m-1");
        await test.Scheduler.RunUntilIdleAsync(Token);

        consumer.Cancel();
        test.Send("work", "m-2");
        await test.Scheduler.RunUntilIdleAsync(Token);
        received[0].Ack();

        Assert.Single(received);
        Assert.False(consumer.IsActive);
        Assert.Equal((1, 0, 0), (test.Broker.GetQueue("work").ReadyCount, test.Broker.GetQueue("work").UnackedCount, test.Broker.GetQueue("work").ConsumerCount));
    }

    [Fact]
    public async Task Consume_errors_close_the_channel_and_unlimited_prefetch_is_unsupported()
    {
        await using var test = await TestBroker.CreateAsync(cancellationToken: Token);
        var missingQueue = test.Broker.OpenChannel();
        var reusedTag = test.Broker.OpenChannel();
        var unlimited = test.Broker.OpenChannel();
        ValueTask Ignore(RabbitMqDelivery delivery, CancellationToken cancellationToken) => ValueTask.CompletedTask;

        var notFound = Assert.Throws<RabbitMqException>(() => missingQueue.Consume("missing", 1, Ignore));
        reusedTag.Consume("work", 1, Ignore, "consumer-a");
        var notAllowed = Assert.Throws<RabbitMqException>(() => reusedTag.Consume("audit-1", 1, Ignore, "consumer-a"));
        var unsupported = Assert.Throws<UnsupportedCapabilityException>(() => unlimited.Consume("work", 0, Ignore));

        Assert.Equal((RabbitMqErrorCodes.NotFound, false), (notFound.ErrorCode, missingQueue.IsOpen));
        Assert.Equal((RabbitMqErrorCodes.NotAllowed, false), (notAllowed.ErrorCode, reusedTag.IsOpen));
        Assert.Equal((RabbitMqCapabilityIds.UnlimitedPrefetch, true), (unsupported.CapabilityId, unlimited.IsOpen));
    }

    [Fact]
    public async Task Handler_exceptions_reach_the_scheduler_driver_and_leave_the_delivery_unacknowledged()
    {
        await using var test = await TestBroker.CreateAsync(cancellationToken: Token);
        test.Broker.OpenChannel().Consume("work", 1, (_, _) => throw new InvalidOperationException("consumer bug"));
        test.Send("work");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => test.Scheduler.RunUntilIdleAsync(Token).AsTask());

        Assert.Equal("consumer bug", exception.Message);
        Assert.Equal(1, test.Broker.GetQueue("work").UnackedCount);
    }

    [Fact]
    public async Task Endless_requeue_loop_is_stopped_by_the_scheduler_step_bound()
    {
        await using var test = await TestBroker.CreateAsync(options: new SimulationEnvironmentOptions { MaxSchedulerSteps = 100 }, cancellationToken: Token);
        test.Broker.OpenChannel().Consume("work", 1, (delivery, _) =>
        {
            delivery.Nack(requeue: true);
            return ValueTask.CompletedTask;
        });
        test.Send("work");

        var exception = await Assert.ThrowsAsync<SimulationLimitExceededException>(() => test.Scheduler.RunUntilIdleAsync(Token).AsTask());

        Assert.Contains(exception.PendingWork, work => work.Name == "rabbitmq:broker:work:deliver");
    }

    [Fact]
    public async Task Consumers_cannot_change_the_stored_or_another_consumers_message()
    {
        await using var test = await TestBroker.CreateAsync(cancellationToken: Token);
        var body = "original"u8.ToArray();
        var (_, first) = test.Recorder("audit-1");
        var (_, second) = test.Recorder("audit-2");

        test.Broker.Publish("audit", "", new MessageEnvelope("m-1", body));
        body[0] = (byte)'X';
        await test.Scheduler.RunUntilIdleAsync(Token);
        first[0].Message.GetBody()[0] = (byte)'Y';

        Assert.Equal("original", first[0].Message.GetBodyAsText());
        Assert.Equal("original", second[0].Message.GetBodyAsText());
    }

    [Fact]
    public async Task Deliveries_are_journaled_in_order_with_their_tags()
    {
        await using var test = await TestBroker.CreateAsync(cancellationToken: Token);
        var (channel, _) = test.Recorder("work");
        test.Send("work", "m-1");
        await test.Scheduler.RunUntilIdleAsync(Token);
        channel.Ack(1);

        var operations = test.Environment.Journal.GetEntries()
            .Where(entry => entry.Provider == SimulatedRabbitMqBroker.ProviderName)
            .Select(entry => entry.Operation);

        Assert.Equal(
            [RabbitMqOperations.OpenChannel, RabbitMqOperations.Consume, RabbitMqOperations.Publish, RabbitMqOperations.Deliver, RabbitMqOperations.Ack],
            operations.Where(operation => operation != "initialize"));
    }
}
