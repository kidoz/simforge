# About the Kafka model

`SimForge.Kafka` models Kafka as what it fundamentally is: a set of append-only logs that consumers read at their own
pace. This page explains how the model treats positions, commits, and group membership, and why.

## Logs, not queues

A RabbitMQ queue hands each message to one consumer and forgets it once acknowledged. A Kafka partition keeps every
record at a fixed offset, and any number of consumer groups read it independently. Nothing is removed by reading. What
changes is a *position*: the offset of the next record a consumer will read.

This difference is why SimForge has no shared "broker" interface. A RabbitMQ consumer acknowledges individual messages;
a Kafka consumer advances and commits offsets. Code that treats one as the other is usually wrong, in ways that only
show up under failure.

## Reading is not committing

Each group member has a read position per partition, which moves forward as it polls. Separately, the group has
committed offsets, which move only when the application commits. The gap between the two is where at-least-once
delivery comes from. If a consumer handles records and then crashes before committing, whoever takes over the
partition starts from the committed offset and receives the same records again.

The model keeps these two numbers apart and makes the gap easy to reach:
- a member can be closed without committing
- a commit can be made to fail
- any change in group membership resets positions to the committed offsets

This lets a test check the property that matters: records processed twice produce their business effect once.

Offsets are never committed automatically. Kafka clients auto-commit by default, which moves the committed offset on a
timer, independently of whether records were actually handled. That hides exactly the bugs these tests exist to find,
so the model requires explicit commits.

## Membership changes at scheduler boundaries

In a real cluster, rebalancing is a protocol between members and a group coordinator, driven by heartbeats and
timeouts. The model replaces it with a simplified reassignment. Joining or leaving schedules a rebalance, and the
rebalance runs when the test drives the scheduler. Tests therefore decide exactly when ownership changes, and can
place a rebalance between handling a record and committing it.

The model rebalances *eagerly*: every rebalance takes every partition away and gives out a fresh assignment, and
positions restart from committed offsets. Cooperative rebalancing in modern clients avoids some of that churn. Assuming
the pessimistic case surfaces more duplicate deliveries in tests, which is the safer direction to err in.

Assignment is by range, per topic, in join order, so the same sequence of joins always yields the same assignment.
Kafka's default assignors behave similarly but not identically. Tests should assert that each partition has exactly one
owner, not that a particular member owns a particular partition.

## Keys and partitions

Records with the same key land on the same partition, which is what preserves per-key ordering. The model uses its own,
documented hash for this. It deliberately does not reproduce the Java client's murmur2 partitioner: different clients
use different defaults, and claiming parity with one of them would be a promise nobody has verified.

## What is deliberately left out

Idempotent producers, transactions, and exactly-once processing are not modeled. In the model, a retried produce after
a lost acknowledgement appends a duplicate, and consumers must cope with that. Replication, leader election,
retention, compaction, and the wire protocol are also absent. A passing test says nothing about how a cluster behaves
when a broker fails.

## Related

- [How to test a Kafka consumer](../how-to/test-a-kafka-consumer.md)
- [SimForge.Kafka](../reference/kafka.md) (reference)
- [About the RabbitMQ model](about-the-rabbitmq-model.md)
