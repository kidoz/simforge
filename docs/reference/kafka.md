# SimForge.Kafka

Namespace `SimForge.Kafka`. An in-memory model of Kafka topics, partition logs, and consumer groups. It is not a Kafka
broker and is not wire compatible: Confluent.Kafka and other clients cannot connect to it. Records carry
[`MessageEnvelope`](messaging.md) values.

- [Registration and topics](#registration-and-topics)
- [SimulatedKafkaCluster](#simulatedkafkacluster)
- [KafkaConsumer](#kafkaconsumer)
- [Group membership and assignment](#group-membership-and-assignment)
- [Positions, commits, and resets](#positions-commits-and-resets)
- [Fault points](#fault-points)
- [Capability table](#capability-table)
- [Error codes](#error-codes)

## Registration and topics

```csharp
SimulatedKafkaCluster AddKafkaCluster(this SimulationEnvironment environment, string name, Action<KafkaTopologyBuilder> configureTopics)
```

`AddKafkaCluster` is a C# 14 extension member. The environment must be in state `Created`. Topics are fixed from then
on.

| Builder member | Description |
|---|---|
| `Topic(name, partitions)` | Declares a topic with at least one partition. At least one topic is required. |

Topic names have at most 249 ASCII letters, digits, `.`, `_`, or `-`, and must not be `.` or `..`. Names starting with
`__` throw `UnsupportedCapabilityException` (`kafka.topics.management`).

## SimulatedKafkaCluster

Implements `ISimulationResource`. `ProviderName` is `kafka`.

| Member | Description |
|---|---|
| `Name`, `Provider`, `Topology`, `FaultPoints` | Resource identity, topics, and declared fault points. |
| `Produce(topic, partition, message, key = null)` | Appends a record to the partition. Returns `ProduceResult(Topic, Partition, Offset, Timestamp)`. |
| `Produce(topic, key, message)` | Appends a keyed record to the partition chosen by `PartitionForKey`. |
| `PartitionForKey(key, partitionCount)` | Static. `(FNV-1a 32-bit hash of the UTF-8 key bytes & 0x7FFFFFFF) % partitionCount`. |
| `JoinGroup(groupId, topics, autoOffsetReset)` | Adds a member to the group, creating the group if needed. Returns a `KafkaConsumer`. |
| `GetPartition(topic, partition)` | `PartitionSnapshot(Topic, Partition, LogEndOffset, Records)`. |
| `GetGroup(groupId)` | `GroupSnapshot(GroupId, Generation, RebalancePending, Members, CommittedOffsets)`. |
| `InjectFault(operation, phase = FaultPhases.Before, occurrence = 1, reason = null)` | Registers a fault rule for this cluster. |

Log rules:
- Offsets start at 0 and increase by 1 per record within a partition. There is no ordering across partitions.
- `LogEndOffset` is the number of records in the partition. Records are never deleted.
- Each `KafkaRecord(Topic, Partition, Offset, Key, Message, Timestamp)` is stamped with the virtual time of the append.

`PartitionForKey` is not the Java client's murmur2 partitioner or librdkafka's partitioner. Equal keys map to the same
partition of a topic.

## KafkaConsumer

A consumer-group member. Implements `IDisposable` and `IAsyncDisposable`; disposal closes it.

| Member | Description |
|---|---|
| `GroupId`, `MemberId`, `Topics`, `AutoOffsetReset` | Membership values. `MemberId` is `<group>-member-<n>` in join order. |
| `Assignment` | Partitions assigned by the last rebalance, in topic/partition order. |
| `IsClosed` | True after `Close` or environment disposal. |
| `Poll(maxRecords = 500)` | Returns up to `maxRecords` records from assigned partitions and advances their positions. |
| `Position(partition)` | Offset of the next record to read. |
| `Seek(partition, offset)` | Sets the position. A negative offset throws `ArgumentOutOfRangeException`. |
| `SeekToBeginning(partition)`, `SeekToEnd(partition)` | Set the position to 0 or to `LogEndOffset`, immediately. |
| `Commit()` | Commits the position of every assigned partition whose position is resolved. |
| `Commit(partition, offset)` | Commits `offset` as the next offset to consume. |
| `Committed(partition)` | The group's committed offset, or null. |
| `Close()` | Leaves the group without committing. |

`Position`, `Seek`, and the seek variants require an assigned partition, and throw `InvalidOperationException`
otherwise. After `Close`, every member except `Close` and `Dispose` throws `InvalidOperationException`.

## Group membership and assignment

- `JoinGroup` and `Close` schedule the group's rebalance as work named `kafka:<cluster>:<group>:rebalance`. Until the
  scheduler runs it, existing members keep their assignment, a new member has none, and `GroupSnapshot.RebalancePending`
  is true.
- A rebalance increments `Generation` and assigns partitions by range, per topic. The topic's partitions, in order, are
  split into contiguous ranges over the members subscribed to it, in join order. When the split is uneven, earlier
  members get one extra partition. Members beyond the partition count get none.
- Every partition is assigned to exactly one member of the group. Separate groups are independent and read the same
  records.
- A rebalance discards the positions of all assigned partitions (eager rebalancing). The next read resolves them again
  from committed offsets, so progress that was not committed is read again.

## Positions, commits, and resets

- A position is resolved on first use by `Poll` or `Position`. It is the group's committed offset if one exists and is
  not beyond the log end; otherwise the reset policy applies. `Poll` resolves every assigned partition before reading
  any record.
- `KafkaOffsetReset.Earliest` starts at 0. `Latest` starts at `LogEndOffset`. `None` throws `KafkaException`:
  `no_offset` without a committed offset, `offset_out_of_range` when the position is beyond the log end.
- `Poll` reads partitions in topic/partition order, starting one partition later on each call, and returns records in
  offset order per partition.
- Committed offsets belong to the group. They change only through `Commit`, survive member restarts, and persist while
  the environment lives. `Commit()` also stores positions of partitions that returned no records, as `commitSync()`
  does. Committing a partition not assigned to the member throws `KafkaException` (`commit_failed`) before any offset
  changes.
- Offsets are never committed automatically.

## Fault points

| Operation (`KafkaOperations`) | Phase | Effect when a rule fires |
|---|---|---|
| `produce` | `before` | Nothing is appended. `SimulatedFaultException.StateChanged` is false. |
| `produce` | `after` | The record is appended, but `Produce` throws instead of returning its offset. A retry appends a duplicate. |
| `commit` | `before` | No offset is stored. |
| `commit` | `after` | The offsets are stored, but `Commit` throws. `StateChanged` is true when any offset was stored. |

## Capability table

The machine-readable form is [`kafka.capabilities.json`](kafka.capabilities.json), generated from
`KafkaCapabilities.Manifest`. No capability is verified against a reference Kafka version. The reference documentation
used is Apache Kafka 4.1.

| Capability | Status | Level | What it covers |
|---|---|---|---|
| `kafka.topics` | SimulatedOnly | ServiceSemantics | Named topics with fixed partition counts, declared at registration. |
| `kafka.log` | SimulatedOnly | ServiceSemantics | Append-only logs with monotonic offsets from 0; ordering within a partition only. |
| `kafka.producer.explicit-partition` | SimulatedOnly | ServiceSemantics | Producing to a chosen partition with an optional key. |
| `kafka.producer.key-partitioner` | SimulatedOnly | ApplicationContract | Documented FNV-1a key partitioner; not murmur2. |
| `kafka.consumers.groups` | SimulatedOnly | ServiceSemantics | Deterministic range assignment, one member per partition, independent groups. |
| `kafka.consumers.simplified-rebalancing` | SimulatedOnly | ApplicationContract | Joins and leaves take effect at scheduler boundaries; eager position reset. |
| `kafka.consumers.positions` | SimulatedOnly | ServiceSemantics | Read positions separate from commits; poll, seek, and seek to beginning or end. |
| `kafka.consumers.offset-commits` | SimulatedOnly | ServiceSemantics | Manual commits of the next offset to consume; `commit_failed` after losing a partition. |
| `kafka.consumers.offset-reset` | SimulatedOnly | ServiceSemantics | `Earliest`, `Latest`, and `None` reset policies. |
| `kafka.faults` | SimulatedOnly | ApplicationContract | Fault points for produce and commit, before and after. |
| `kafka.client-compatibility` | Unsupported | WireProtocol | Wire protocol, Confluent.Kafka, and bootstrap servers. |
| `kafka.topics.management` | Unsupported | ServiceSemantics | Auto-creation, runtime topic changes, and internal `__` topics. |
| `kafka.producer.keyless-partitioning` | Unsupported | ServiceSemantics | Keyless records without an explicit partition. |
| `kafka.producer.idempotence-and-transactions` | Unsupported | ServiceSemantics | Idempotent producers, transactions, read_committed, and exactly-once. |
| `kafka.consumers.auto-commit` | Unsupported | ServiceSemantics | `enable.auto.commit`. |
| `kafka.consumers.advanced-membership` | Unsupported | ServiceSemantics | Static membership, cooperative rebalancing, the consumer group protocol, heartbeats, and session timeouts. |
| `kafka.consumers.manual-assignment` | Unsupported | ServiceSemantics | `assign()` without a group. |
| `kafka.consumers.offset-lookup` | Unsupported | ServiceSemantics | Offsets by timestamp and the `by_duration` reset policy. |
| `kafka.replication` | Unsupported | ServiceSemantics | Replication, in-sync replicas, leader election, and acks settings. |
| `kafka.log.retention-and-compaction` | Unsupported | ServiceSemantics | Retention, compaction, and log start offsets other than 0. |

Deviations from Kafka:
- The key partitioner differs from the clients' defaults.
- Seeking to the beginning or end is not lazy.
- There is no coordinator protocol.
- The reset policy has no default; Kafka's default is `latest`.

## Error codes

SimForge error codes reuse Kafka error names. No protocol parity is claimed.

| Code | Raised when |
|---|---|
| `unknown_topic_or_partition` | Producing to, or subscribing to, a topic or partition that does not exist. |
| `no_offset` | A partition has no committed offset and the reset policy is `None`. |
| `offset_out_of_range` | A position is beyond the log end and the reset policy is `None`. |
| `commit_failed` | Committing a partition not assigned to the member. |
| `injected_fault` | A fault rule fired (`SimulatedFaultException`). |
