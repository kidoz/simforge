using Xunit;

namespace SimForge.RabbitMq.Tests;

public sealed class FaultTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Publish_fault_before_acceptance_enqueues_nothing()
    {
        await using var test = await TestBroker.CreateAsync(cancellationToken: Token);
        test.Broker.InjectFault(RabbitMqOperations.Publish, reason: "broker unavailable");

        var exception = Assert.Throws<SimulatedFaultException>(() => test.Send("work"));

        Assert.False(exception.StateChanged);
        Assert.Equal(0, test.Broker.GetQueue("work").ReadyCount);
        Assert.Equal(PublishOutcome.Routed, test.Send("work").Outcome);
    }

    [Fact]
    public async Task Publish_fault_after_acceptance_enqueues_the_message_but_loses_the_confirmation()
    {
        await using var test = await TestBroker.CreateAsync(cancellationToken: Token);
        test.Broker.InjectFault(RabbitMqOperations.Publish, FaultPhases.After, reason: "confirm lost");

        var exception = Assert.Throws<SimulatedFaultException>(() => test.Send("work", "m-1"));

        Assert.True(exception.StateChanged);
        Assert.Equal(["m-1"], test.Broker.GetQueue("work").ReadyMessages.Select(message => message.MessageId));
    }

    [Fact]
    public async Task Lost_ack_closes_the_channel_and_the_message_is_redelivered()
    {
        await using var test = await TestBroker.CreateAsync(cancellationToken: Token);
        var fault = test.Broker.InjectFault(RabbitMqOperations.Ack, reason: "connection dropped before the ack arrived");
        var (channel, first) = test.Recorder("work");
        test.Send("work", "m-1");
        await test.Scheduler.RunUntilIdleAsync(Token);

        var exception = Assert.Throws<SimulatedFaultException>(() => first[0].Ack());

        Assert.False(exception.StateChanged);
        Assert.Equal(fault.Id, exception.Report.RuleId);
        Assert.False(channel.IsOpen);
        var (_, second) = test.Recorder("work");
        await test.Scheduler.RunUntilIdleAsync(Token);
        var redelivery = Assert.Single(second);
        Assert.Equal(("m-1", true), (redelivery.Message.MessageId, redelivery.Redelivered));
        redelivery.Ack();
        Assert.Equal((0, 0), (test.Broker.GetQueue("work").ReadyCount, test.Broker.GetQueue("work").UnackedCount));
    }

    [Theory]
    [InlineData(RabbitMqOperations.Nack)]
    [InlineData(RabbitMqOperations.Reject)]
    public async Task Lost_negative_acknowledgement_leaves_the_message_for_redelivery(string operation)
    {
        await using var test = await TestBroker.CreateAsync(cancellationToken: Token);
        test.Broker.InjectFault(operation);
        var (channel, deliveries) = test.Recorder("order-placed");
        test.Broker.Publish("orders", "order.placed", test.Message("m-1"));
        await test.Scheduler.RunUntilIdleAsync(Token);

        Assert.Throws<SimulatedFaultException>(() =>
        {
            if (operation == RabbitMqOperations.Nack)
            {
                deliveries[0].Nack(requeue: false);
            }
            else
            {
                deliveries[0].Reject(requeue: false);
            }
        });

        Assert.False(channel.IsOpen);
        Assert.Equal(0, test.Broker.GetQueue("order-placed.dead").ReadyCount);
        Assert.Equal(["m-1"], test.Broker.GetQueue("order-placed").ReadyMessages.Select(message => message.MessageId));
    }

    [Fact]
    public async Task Fault_rules_for_undeclared_points_are_rejected()
    {
        await using var test = await TestBroker.CreateAsync(cancellationToken: Token);

        Assert.Throws<UnsupportedCapabilityException>(() => test.Broker.InjectFault(RabbitMqOperations.Deliver));
        Assert.Throws<UnsupportedCapabilityException>(() => test.Broker.InjectFault(RabbitMqOperations.Ack, FaultPhases.After));
    }
}
