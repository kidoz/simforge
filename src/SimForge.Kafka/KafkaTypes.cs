using System.Collections.Immutable;
using System.Globalization;
using SimForge.Messaging;

namespace SimForge.Kafka;

/// <summary>A topic and one of its partitions.</summary>
public readonly record struct TopicPartition(string Topic, int Partition) : IComparable<TopicPartition>
{
    public int CompareTo(TopicPartition other)
    {
        var byTopic = string.CompareOrdinal(Topic, other.Topic);
        return byTopic != 0 ? byTopic : Partition.CompareTo(other.Partition);
    }

    public override string ToString() => $"{Topic}-{Partition.ToString(CultureInfo.InvariantCulture)}";
}

/// <summary>A record in a partition log. <see cref="Timestamp"/> is the virtual time at which the record was appended.</summary>
public sealed record KafkaRecord(string Topic, int Partition, long Offset, string? Key, MessageEnvelope Message, DateTimeOffset Timestamp)
{
    public TopicPartition TopicPartition => new(Topic, Partition);
}

/// <summary>Where a produced record was appended.</summary>
public sealed record ProduceResult(string Topic, int Partition, long Offset, DateTimeOffset Timestamp);

/// <summary>What a consumer does when a partition has no committed offset, or its offset is beyond the log end.</summary>
public enum KafkaOffsetReset
{
    /// <summary>Start from the first record (offset 0).</summary>
    Earliest,

    /// <summary>Start from the log end offset: only records appended afterwards are read. This is Kafka's default.</summary>
    Latest,

    /// <summary>Throw <see cref="KafkaException"/> instead of choosing a position.</summary>
    None,
}

/// <summary>Operation names used in the journal and by fault rules.</summary>
public static class KafkaOperations
{
    public const string Produce = "produce";
    public const string Poll = "poll";
    public const string Commit = "commit";
    public const string Seek = "seek";
    public const string Join = "join";
    public const string Leave = "leave";
    public const string Rebalance = "rebalance";
}

/// <summary>SimForge error codes for the Kafka-oriented model. The names follow Kafka error names, but no protocol parity is claimed.</summary>
public static class KafkaErrorCodes
{
    /// <summary>The topic or partition does not exist.</summary>
    public const string UnknownTopicOrPartition = "unknown_topic_or_partition";

    /// <summary>The position is beyond the log end and the reset policy is <see cref="KafkaOffsetReset.None"/>.</summary>
    public const string OffsetOutOfRange = "offset_out_of_range";

    /// <summary>A partition has no committed offset and the reset policy is <see cref="KafkaOffsetReset.None"/>.</summary>
    public const string NoOffset = "no_offset";

    /// <summary>An offset was committed for a partition not assigned to the member, for example after a rebalance.</summary>
    public const string CommitFailed = "commit_failed";
}

/// <summary>An error reported by the simulated cluster.</summary>
public sealed class KafkaException : SimulatedServiceException
{
    public KafkaException(string resource, string errorCode, string message)
        : base(SimulatedKafkaCluster.ProviderName, resource, errorCode, message)
    {
    }
}

/// <summary>Point-in-time view of a partition log.</summary>
public sealed record PartitionSnapshot(string Topic, int Partition, long LogEndOffset, ImmutableArray<KafkaRecord> Records);

/// <summary>Point-in-time view of one group member.</summary>
public sealed record GroupMemberSnapshot(string MemberId, ImmutableArray<string> Topics, ImmutableArray<TopicPartition> Assignment);

/// <summary>Point-in-time view of a consumer group.</summary>
public sealed record GroupSnapshot(
    string GroupId,
    int Generation,
    bool RebalancePending,
    ImmutableArray<GroupMemberSnapshot> Members,
    ImmutableSortedDictionary<TopicPartition, long> CommittedOffsets);

/// <summary>
/// A consumer-group member. It reads its assigned partitions with <see cref="Poll"/>; its read position is separate
/// from the group's committed offsets, which change only through <see cref="Commit()"/>.
/// </summary>
public sealed class KafkaConsumer : IDisposable, IAsyncDisposable
{
    internal KafkaConsumer(SimulatedKafkaCluster cluster, string groupId, string memberId, ImmutableArray<string> topics, KafkaOffsetReset autoOffsetReset)
    {
        Cluster = cluster;
        GroupId = groupId;
        MemberId = memberId;
        Topics = topics;
        AutoOffsetReset = autoOffsetReset;
    }

    public string GroupId { get; }

    /// <summary>Deterministic member ID: <c>&lt;group&gt;-member-&lt;n&gt;</c>, numbered in join order.</summary>
    public string MemberId { get; }

    /// <summary>Subscribed topics, in ordinal order.</summary>
    public ImmutableArray<string> Topics { get; }

    public KafkaOffsetReset AutoOffsetReset { get; }

    /// <summary>Partitions assigned by the last rebalance, in topic/partition order. Empty until the first rebalance runs.</summary>
    public ImmutableArray<TopicPartition> Assignment { get; internal set; } = [];

    public bool IsClosed { get; internal set; }

    internal SimulatedKafkaCluster Cluster { get; }

    internal Dictionary<TopicPartition, long> Positions { get; } = [];

    internal int PollCursor { get; set; }

    /// <summary>
    /// Returns up to <paramref name="maxRecords"/> records from the assigned partitions, starting at each partition's
    /// position, and advances the positions. Partitions are read in rotating topic/partition order. Records within a
    /// partition are in offset order.
    /// </summary>
    public IReadOnlyList<KafkaRecord> Poll(int maxRecords = 500) => Cluster.Poll(this, maxRecords);

    /// <summary>The offset of the next record this member will read from <paramref name="partition"/>.</summary>
    public long Position(TopicPartition partition) => Cluster.GetPosition(this, partition);

    /// <summary>Moves the position. An offset beyond the log end is resolved with the reset policy on the next read.</summary>
    public void Seek(TopicPartition partition, long offset) => Cluster.Seek(this, partition, offset);

    public void SeekToBeginning(TopicPartition partition) => Cluster.SeekToBoundary(this, partition, beginning: true);

    public void SeekToEnd(TopicPartition partition) => Cluster.SeekToBoundary(this, partition, beginning: false);

    /// <summary>
    /// Commits, as the next offset to consume, the position of every assigned partition whose position is resolved. Any
    /// poll resolves every assigned partition, so partitions that returned no records are committed too, as with Kafka's
    /// <c>commitSync()</c>.
    /// </summary>
    public void Commit() => Cluster.Commit(this, explicitOffsets: null);

    /// <summary>Commits <paramref name="offset"/> (the next offset to consume) for an assigned partition.</summary>
    public void Commit(TopicPartition partition, long offset) => Cluster.Commit(this, new Dictionary<TopicPartition, long> { [partition] = offset });

    /// <summary>The group's committed offset for <paramref name="partition"/>, or null.</summary>
    public long? Committed(TopicPartition partition) => Cluster.GetCommitted(GroupId, partition);

    /// <summary>Leaves the group without committing. The group rebalances when the scheduler next runs.</summary>
    public void Close() => Cluster.Leave(this);

    public void Dispose() => Close();

    public ValueTask DisposeAsync()
    {
        Close();
        return ValueTask.CompletedTask;
    }

    public override string ToString() => $"{MemberId}{(IsClosed ? " (closed)" : string.Empty)}";
}
