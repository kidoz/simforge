namespace SimForge.Kafka;

/// <summary>Capability identifiers of the Kafka-oriented provider.</summary>
public static class KafkaCapabilityIds
{
    public const string Topics = "kafka.topics";
    public const string Log = "kafka.log";
    public const string ExplicitPartitioning = "kafka.producer.explicit-partition";
    public const string KeyPartitioning = "kafka.producer.key-partitioner";
    public const string ConsumerGroups = "kafka.consumers.groups";
    public const string Rebalancing = "kafka.consumers.simplified-rebalancing";
    public const string Positions = "kafka.consumers.positions";
    public const string OffsetCommits = "kafka.consumers.offset-commits";
    public const string OffsetReset = "kafka.consumers.offset-reset";
    public const string Faults = "kafka.faults";

    public const string ClientCompatibility = "kafka.client-compatibility";
    public const string TopicManagement = "kafka.topics.management";
    public const string KeylessPartitioning = "kafka.producer.keyless-partitioning";
    public const string ExactlyOnce = "kafka.producer.idempotence-and-transactions";
    public const string AutoCommit = "kafka.consumers.auto-commit";
    public const string AdvancedMembership = "kafka.consumers.advanced-membership";
    public const string ManualAssignment = "kafka.consumers.manual-assignment";
    public const string OffsetLookup = "kafka.consumers.offset-lookup";
    public const string Replication = "kafka.replication";
    public const string RetentionAndCompaction = "kafka.log.retention-and-compaction";
}

/// <summary>Machine-readable capability manifest of <see cref="SimulatedKafkaCluster"/>.</summary>
public static class KafkaCapabilities
{
    public static CapabilityManifest Manifest { get; } = new(
        SimulatedKafkaCluster.ProviderName,
        "Kafka-oriented partitioned-log model",
        [
            new Capability
            {
                Id = KafkaCapabilityIds.Topics,
                Status = CapabilityStatus.SimulatedOnly,
                Level = CompatibilityLevel.ServiceSemantics,
                Summary = "Named topics with a fixed number of partitions, declared when the cluster is added.",
                Operations = ["AddKafkaCluster", "KafkaTopologyBuilder.Topic"],
                Boundaries =
                [
                    "Names use at most 249 ASCII letters, digits, '.', '_' or '-'; '.' and '..' are illegal; the '__' prefix is rejected.",
                    "Producing to or subscribing to an undeclared topic fails with unknown_topic_or_partition; topics are never created automatically.",
                ],
            },
            new Capability
            {
                Id = KafkaCapabilityIds.Log,
                Status = CapabilityStatus.SimulatedOnly,
                Level = CompatibilityLevel.ServiceSemantics,
                Summary = "Append-only partition logs with monotonic offsets starting at 0.",
                Operations = ["SimulatedKafkaCluster.GetPartition"],
                Assumptions =
                [
                    "Records are ordered within a partition only; there is no ordering across partitions.",
                    "The log end offset is the number of records; with no replication it is also the high watermark.",
                    "Record timestamps are the virtual time of the append.",
                ],
            },
            new Capability
            {
                Id = KafkaCapabilityIds.ExplicitPartitioning,
                Status = CapabilityStatus.SimulatedOnly,
                Level = CompatibilityLevel.ServiceSemantics,
                Summary = "Producing to an explicitly chosen partition, with an optional key.",
                Operations = ["SimulatedKafkaCluster.Produce(topic, partition, message, key)"],
                Boundaries = ["A partition outside the topic's range fails with unknown_topic_or_partition before any state changes."],
            },
            new Capability
            {
                Id = KafkaCapabilityIds.KeyPartitioning,
                Status = CapabilityStatus.SimulatedOnly,
                Level = CompatibilityLevel.ApplicationContract,
                Summary = "Keyed records are placed by a documented SimForge partitioner.",
                Operations = ["SimulatedKafkaCluster.Produce(topic, key, message)", "SimulatedKafkaCluster.PartitionForKey"],
                Boundaries = ["Partition = (FNV-1a 32-bit hash of the UTF-8 key bytes & 0x7FFFFFFF) mod partition count; equal keys always map to the same partition."],
                Deviations = ["This is not the Java client's murmur2 partitioner or librdkafka's; a key can land on a different partition than in production."],
            },
            new Capability
            {
                Id = KafkaCapabilityIds.ConsumerGroups,
                Status = CapabilityStatus.SimulatedOnly,
                Level = CompatibilityLevel.ServiceSemantics,
                Summary = "Consumer groups with deterministic range assignment and one member per partition; independent groups read the same records.",
                Operations = ["SimulatedKafkaCluster.JoinGroup", "KafkaConsumer.Close", "SimulatedKafkaCluster.GetGroup"],
                Assumptions =
                [
                    "Range assignment per topic: partitions in order are split into contiguous ranges over the subscribed members in join order; earlier members get the extra partitions.",
                    "Member IDs are deterministic: <group>-member-<n>.",
                ],
            },
            new Capability
            {
                Id = KafkaCapabilityIds.Rebalancing,
                Status = CapabilityStatus.SimulatedOnly,
                Level = CompatibilityLevel.ApplicationContract,
                Summary = "Simplified reassignment: joins and leaves take effect when the group's rebalance runs as scheduled work.",
                Boundaries =
                [
                    "Until the rebalance runs, existing members keep their assignment and a new member has none.",
                    "Every rebalance resets the positions of all assigned partitions to the committed offsets (eager rebalancing), so uncommitted progress is read again.",
                ],
                Deviations = ["There is no group coordinator protocol: no heartbeats, session timeouts, or cooperative or incremental rebalancing."],
            },
            new Capability
            {
                Id = KafkaCapabilityIds.Positions,
                Status = CapabilityStatus.SimulatedOnly,
                Level = CompatibilityLevel.ServiceSemantics,
                Summary = "Per-member read positions, separate from committed offsets, with poll, seek, and seek to beginning or end.",
                Operations = ["KafkaConsumer.Poll", "KafkaConsumer.Position", "KafkaConsumer.Seek", "KafkaConsumer.SeekToBeginning", "KafkaConsumer.SeekToEnd"],
                Assumptions = ["Poll reads assigned partitions in rotating topic/partition order and returns records in offset order per partition."],
                Deviations = ["SeekToBeginning and SeekToEnd take effect immediately rather than lazily."],
            },
            new Capability
            {
                Id = KafkaCapabilityIds.OffsetCommits,
                Status = CapabilityStatus.SimulatedOnly,
                Level = CompatibilityLevel.ServiceSemantics,
                Summary = "Manual commits store the next offset to consume per group and partition.",
                Operations = ["KafkaConsumer.Commit()", "KafkaConsumer.Commit(partition, offset)", "KafkaConsumer.Committed"],
                Boundaries =
                [
                    "Commit() stores the position of every assigned partition whose position is resolved; any poll resolves all assigned partitions, including those that returned no records.",
                    "Committing a partition not assigned to the member fails with commit_failed before any state changes.",
                    "Committed offsets survive member restarts and belong to the group, not the member.",
                ],
            },
            new Capability
            {
                Id = KafkaCapabilityIds.OffsetReset,
                Status = CapabilityStatus.SimulatedOnly,
                Level = CompatibilityLevel.ServiceSemantics,
                Summary = "Earliest, Latest, and None reset policies for partitions without a committed offset or beyond the log end.",
                Boundaries =
                [
                    "The policy is chosen explicitly when joining; Kafka's default is Latest.",
                    "None fails with no_offset (no committed offset) or offset_out_of_range (beyond the log end) before any record is consumed.",
                ],
            },
            new Capability
            {
                Id = KafkaCapabilityIds.Faults,
                Status = CapabilityStatus.SimulatedOnly,
                Level = CompatibilityLevel.ApplicationContract,
                Summary = "Declared fault points for one-shot injected failures.",
                Operations =
                [
                    "produce:before (nothing is appended)",
                    "produce:after (the record is appended, but the producer receives an error instead of its offset)",
                    "commit:before (the offsets are not stored)",
                    "commit:after (the offsets are stored, but the member receives an error)",
                ],
            },
            Unsupported(KafkaCapabilityIds.ClientCompatibility, "The Kafka wire protocol, Confluent.Kafka or other unchanged clients, and bootstrap servers.", CompatibilityLevel.WireProtocol),
            Unsupported(KafkaCapabilityIds.TopicManagement, "Automatic topic creation, runtime topic creation, deletion, or partition changes, and internal '__' topics."),
            Unsupported(KafkaCapabilityIds.KeylessPartitioning, "Keyless records without an explicit partition (sticky or round-robin partitioning)."),
            Unsupported(KafkaCapabilityIds.ExactlyOnce, "Idempotent producers, transactions, read_committed isolation, and exactly-once processing; a retried produce appends a duplicate."),
            Unsupported(KafkaCapabilityIds.AutoCommit, "Automatic offset commits (enable.auto.commit); offsets change only through explicit commits."),
            Unsupported(KafkaCapabilityIds.AdvancedMembership, "Static membership, cooperative or incremental rebalancing, the consumer group protocol, heartbeats, and session timeouts."),
            Unsupported(KafkaCapabilityIds.ManualAssignment, "Manual partition assignment (assign) without a consumer group."),
            Unsupported(KafkaCapabilityIds.OffsetLookup, "Offset lookup by timestamp and the by_duration reset policy."),
            Unsupported(KafkaCapabilityIds.Replication, "Replication, in-sync replicas, leader election, acks settings, and broker failures."),
            Unsupported(KafkaCapabilityIds.RetentionAndCompaction, "Retention, log compaction, and log start offsets other than 0."),
        ]);

    private static Capability Unsupported(string id, string summary, CompatibilityLevel level = CompatibilityLevel.ServiceSemantics) => new()
    {
        Id = id,
        Status = CapabilityStatus.Unsupported,
        Level = level,
        Summary = summary,
    };
}

/// <summary>Registers Kafka-oriented cluster simulations with an environment.</summary>
public static class KafkaEnvironmentExtensions
{
    extension(SimulationEnvironment environment)
    {
        /// <summary>
        /// Adds an in-memory, Kafka-oriented partitioned-log cluster with fixed topics. It is not a Kafka broker and cannot
        /// be used with Confluent.Kafka.
        /// </summary>
        public SimulatedKafkaCluster AddKafkaCluster(string name, Action<KafkaTopologyBuilder> configureTopics)
        {
            ArgumentNullException.ThrowIfNull(environment);
            ArgumentNullException.ThrowIfNull(configureTopics);
            var builder = new KafkaTopologyBuilder();
            configureTopics(builder);
            return environment.AddResource(new SimulatedKafkaCluster(environment, name, builder.Build()));
        }
    }
}
