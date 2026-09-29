# How to test a Kafka consumer

Use this to run an application's message handler against the simulated Kafka cluster, including redelivery after a
restart, lost offset commits, and rebalances.

## 1. Declare the topics

```csharp
setup.Environment.AddKafkaCluster("kafka", topics => topics.Topic("orders", partitions: 3));
setup.AddService(_ => new OrderPlacedHandler());
```

## 2. Join a consumer group

Membership changes take effect when the group's rebalance runs on the scheduler. Drive it before polling:

```csharp
var cluster = context.GetResource<SimulatedKafkaCluster>("kafka");
var consumer = cluster.JoinGroup("billing", ["orders"], KafkaOffsetReset.Earliest);
await context.Scheduler.RunUntilIdleAsync(cancellationToken); // runs the rebalance
```

Choose the reset policy deliberately. `Earliest` reads records that already exist. `Latest`, Kafka's default, reads
only records appended after the partition's position is first resolved.

## 3. Poll, handle, commit

Write the consume loop the way the application does it: poll, handle every record, then commit.

```csharp
private static async Task ConsumeOnceAsync(ScenarioContext context, KafkaConsumer consumer, CancellationToken cancellationToken)
{
    var handler = context.GetRequiredService<OrderPlacedHandler>();
    foreach (var record in consumer.Poll())
    {
        await handler.HandleAsync(record.Message.MessageId, record.Message.ReadJson<OrderPlaced>(), cancellationToken);
    }

    consumer.Commit();
}
```

Produce a keyed record and consume it:

```csharp
var produced = cluster.Produce("orders", orderId.ToString(), MessageEnvelope.FromJson("msg-1", new OrderPlaced(orderId, 4_250)));
await ConsumeOnceAsync(context, consumer, cancellationToken);
SimAssert.Equal(produced.Offset + 1, consumer.Committed(new TopicPartition("orders", produced.Partition)));
```

A committed offset is the *next* offset to read, so after consuming offset 0 the committed offset is 1.

## Test redelivery after a restart

Handle the records but do not commit, then close the member as if the process crashed. Join again and drive the
rebalance. The new member resumes from the committed offset and receives the same records again:

```csharp
foreach (var record in first.Poll())
{
    await handler.HandleAsync(record.Message.MessageId, record.Message.ReadJson<OrderPlaced>(), cancellationToken);
}

first.Close(); // crashed before committing
var restarted = cluster.JoinGroup("billing", ["orders"], KafkaOffsetReset.Earliest);
await context.Scheduler.RunUntilIdleAsync(cancellationToken);
var redelivered = restarted.Poll();
```

Handle the redelivered records and assert that the business effect happened once.

## Test a lost offset commit

```csharp
cluster.InjectFault(KafkaOperations.Commit, reason: "coordinator unavailable");
await SimAssert.ThrowsAsync<SimulatedFaultException>(() => ConsumeOnceAsync(context, consumer, cancellationToken));
SimAssert.Null(consumer.Committed(new TopicPartition("orders", 0)));
```

The records were handled, but the offset was not stored. After a restart, or any rebalance, they are read again.

## Test rebalances

Join or close members, drive the scheduler, and inspect assignments:

```csharp
var first = cluster.JoinGroup("billing", ["orders"], KafkaOffsetReset.Earliest);
var second = cluster.JoinGroup("billing", ["orders"], KafkaOffsetReset.Earliest);
await context.Scheduler.RunUntilIdleAsync(cancellationToken);
// With 3 partitions: first has 2 partitions, second has 1.
```

Every rebalance resets positions to the committed offsets. A member that polled without committing reads those records
again after any other member joins or leaves.

## Replay records

```csharp
first.SeekToBeginning(new TopicPartition("orders", 0));
```

`Seek(partition, offset)` moves to any offset. An offset beyond the log end is resolved with the reset policy on the next
read.

## Test a lost produce acknowledgement

```csharp
cluster.InjectFault(KafkaOperations.Produce, FaultPhases.After);
var lost = SimAssert.Throws<SimulatedFaultException>(() => cluster.Produce("orders", 0, message));
cluster.Produce("orders", 0, message); // the retry appends a duplicate
```

The model has no idempotent producer, so a retry after a lost acknowledgement appends the record twice. Consumers must
deduplicate.

See also: [SimForge.Kafka](../reference/kafka.md), [About the Kafka model](../explanation/about-the-kafka-model.md).
