# How to test a RabbitMQ consumer

Use this to run an application's message handler against the simulated RabbitMQ broker, including redelivery and
poison messages.

## 1. Declare the topology

Add the broker in the scenario setup. Declare exchanges before binding to them:

```csharp
private static SimulatedRabbitMqBroker AddBroker(ScenarioSetup setup) =>
    setup.Environment.AddRabbitMqBroker("broker", topology => topology
        .Exchange("orders", ExchangeType.Direct)
        .Exchange("orders.dlx", ExchangeType.Direct)
        .Queue("order-placed", queue => queue.DeadLetterTo("orders.dlx", "dead"))
        .Queue("order-placed.dead")
        .Bind("order-placed", "orders", "order.placed")
        .Bind("order-placed.dead", "orders.dlx", "dead"));
```

Register the application's handler as a service, so that one instance lives for the whole run:

```csharp
setup.AddService(_ => new OrderPlacedHandler());
```

## 2. Connect the handler to a consumer

Open a channel and translate each delivery into a call to the handler, acknowledging after the handler succeeds. Put
this in a helper, `StartConsumer(context)` below, so that a test can reconnect after a channel closes:

```csharp
var broker = context.GetResource<SimulatedRabbitMqBroker>("broker");
var handler = context.GetRequiredService<OrderPlacedHandler>();
var channel = broker.OpenChannel();
channel.Consume("order-placed", prefetchCount: 10, async (delivery, cancellationToken) =>
{
    var placed = delivery.Message.ReadJson<OrderPlaced>();
    await handler.HandleAsync(delivery.Message.MessageId, placed, cancellationToken);
    delivery.Ack();
});
```

## 3. Publish and deliver

```csharp
var message = MessageEnvelope.FromJson("msg-1", new OrderPlaced(context.Ids.NewGuid(), 4_250)) with { Type = "orders.order-placed.v1" };
var confirmation = broker.Publish("orders", "order.placed", message);
SimAssert.Equal(PublishOutcome.Routed, confirmation.Outcome);

await context.Scheduler.RunUntilIdleAsync(cancellationToken);
```

Deliveries happen only while the scheduler runs. Nothing reaches the handler until `RunUntilIdleAsync` (or an advance of
virtual time).

## 4. Assert on the outcome

Check the application's own state first, then the queue:

```csharp
var queue = broker.GetQueue("order-placed");
SimAssert.Equal((0, 0), (queue.ReadyCount, queue.UnackedCount));
```

A message left in `UnackedCount` means the handler neither acknowledged nor rejected it.

## Test redelivery after a lost acknowledgement

Make the next ack fail as if the connection dropped. The channel closes, and the message is requeued as redelivered:

```csharp
broker.InjectFault(RabbitMqOperations.Ack, reason: "connection dropped");
```

The failed `Ack()` throws `SimulatedFaultException` inside the handler, so the exception surfaces from
`RunUntilIdleAsync`. Expect it there, then open a new consumer, as a reconnecting application would, and drive again:

```csharp
await SimAssert.ThrowsAsync<SimulatedFaultException>(() => context.Scheduler.RunUntilIdleAsync(cancellationToken).AsTask());
StartConsumer(context); // opens a new channel and registers the handler again
await context.Scheduler.RunUntilIdleAsync(cancellationToken);
```

The handler receives the message a second time, with `delivery.Redelivered` set. Assert that the business effect
happened once.

## Test a poison message

Reject without requeueing to send the message to the dead-letter queue:

```csharp
delivery.Reject(requeue: false);
```

The dead-lettered copy carries `x-first-death-queue`, `x-first-death-reason` (`rejected`), and
`x-first-death-exchange` headers:

```csharp
var dead = SimAssert.Single(broker.GetQueue("order-placed.dead").ReadyMessages);
SimAssert.Equal("rejected", dead.Headers["x-first-death-reason"]);
```

## Test publish failures

```csharp
broker.InjectFault(RabbitMqOperations.Publish);                    // not accepted: nothing is enqueued
broker.InjectFault(RabbitMqOperations.Publish, FaultPhases.After); // enqueued, but the confirmation is lost
```

Both throw `SimulatedFaultException`. `StateChanged` tells the two apart: when it is true, the message is already in the
queue, and a retry publishes a duplicate.

See also: [SimForge.RabbitMq](../reference/rabbitmq.md), [About the RabbitMQ model](../explanation/about-the-rabbitmq-model.md).
