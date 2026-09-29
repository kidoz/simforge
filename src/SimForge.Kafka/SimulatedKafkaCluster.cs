using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using SimForge.Messaging;

namespace SimForge.Kafka;

/// <summary>
/// A Kafka-oriented, in-memory partitioned-log model: topics with fixed partitions, append-only records, and consumer
/// groups whose read positions are separate from their committed offsets.
/// </summary>
/// <remarks>
/// <para>This is not a Kafka broker and not wire compatible; Confluent.Kafka and other clients cannot connect to it.
/// Unsupported requests throw <see cref="UnsupportedCapabilityException"/>. See <see cref="KafkaCapabilities.Manifest"/>.</para>
/// <para>Group membership changes (join and leave) take effect in a rebalance that runs as scheduled work, so a member
/// has no assignment until the scheduler is driven. Every rebalance resets the positions of all assigned partitions
/// to the group's committed offsets, as eager rebalancing does: progress that was not committed is read again.</para>
/// </remarks>
public sealed class SimulatedKafkaCluster : ISimulationResource
{
    public const string ProviderName = "kafka";

    private static readonly FaultPoint[] DeclaredFaultPoints =
    [
        new(ProviderName, KafkaOperations.Produce, FaultPhases.Before),
        new(ProviderName, KafkaOperations.Produce, FaultPhases.After),
        new(ProviderName, KafkaOperations.Commit, FaultPhases.Before),
        new(ProviderName, KafkaOperations.Commit, FaultPhases.After),
    ];

    private readonly Lock _gate = new();
    private readonly SimulationEnvironment _environment;
    private readonly Dictionary<string, List<KafkaRecord>[]> _logs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, GroupState> _groups = new(StringComparer.Ordinal);
    private bool _initialized;
    private bool _disposed;

    internal SimulatedKafkaCluster(SimulationEnvironment environment, string name, KafkaTopology topology)
    {
        _environment = environment;
        Name = name;
        Topology = topology;
    }

    public string Name { get; }

    public string Provider => ProviderName;

    public KafkaTopology Topology { get; }

    public IReadOnlyCollection<FaultPoint> FaultPoints => DeclaredFaultPoints;

    /// <summary>
    /// SimForge's key partitioner: the 32-bit FNV-1a hash of the key's UTF-8 bytes, masked to a non-negative value, modulo
    /// the partition count. Equal keys always map to the same partition of a topic. It is not the Java client's murmur2
    /// partitioner or librdkafka's partitioner.
    /// </summary>
    public static int PartitionForKey(string key, int partitionCount)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentOutOfRangeException.ThrowIfLessThan(partitionCount, 1);
        var hash = 2166136261u;
        foreach (var value in Encoding.UTF8.GetBytes(key))
        {
            hash = (hash ^ value) * 16777619u;
        }

        return (int)(hash & 0x7FFFFFFF) % partitionCount;
    }

    ValueTask ISimulationResource.InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            foreach (var topic in Topology.Topics)
            {
                _logs.Add(topic.Name, [.. Enumerable.Range(0, topic.Partitions).Select(_ => new List<KafkaRecord>())]);
            }

            _initialized = true;
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>Closes every member and discards all records and offsets. Called by the owning environment.</summary>
    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (!_disposed)
            {
                _disposed = true;
                foreach (var member in _groups.Values.SelectMany(group => group.Members))
                {
                    member.IsClosed = true;
                    member.Assignment = [];
                    member.Positions.Clear();
                }

                _groups.Clear();
                _logs.Clear();
            }
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>Registers a one-shot fault for this cluster. See <see cref="KafkaOperations"/> and <see cref="FaultPoints"/>.</summary>
    public ActiveFault InjectFault(string operation, string phase = FaultPhases.Before, int occurrence = 1, string? reason = null) =>
        _environment.Faults.Add(new FaultRule
        {
            Provider = ProviderName,
            Resource = Name,
            Operation = operation,
            Phase = phase,
            Occurrence = occurrence,
            Reason = reason,
        });

    /// <summary>Appends a record to an explicitly chosen partition and returns its offset.</summary>
    public ProduceResult Produce(string topic, int partition, MessageEnvelope message, string? key = null)
    {
        ArgumentNullException.ThrowIfNull(topic);
        ArgumentNullException.ThrowIfNull(message);
        lock (_gate)
        {
            EnsureUsable();
            var log = GetLog(topic, partition, KafkaOperations.Produce, message.CorrelationId);
            _environment.Faults.ThrowIfTriggered(this, KafkaOperations.Produce, FaultPhases.Before, stateChanged: false);
            var record = new KafkaRecord(topic, partition, log.Count, key, message, _environment.Scheduler.Now);
            log.Add(record);
            Journal(
                KafkaOperations.Produce,
                FaultPhases.After,
                OperationOutcome.Succeeded,
                record.TopicPartition.ToString(),
                $"offset={Format(record.Offset)}; message={message.MessageId}",
                payload: () => $"key={key}; body={FormatBody(message)}",
                correlationId: message.CorrelationId);
            if (_environment.Faults.Evaluate(this, KafkaOperations.Produce, FaultPhases.After, stateChanged: true) is { } lostAck)
            {
                throw new SimulatedFaultException(lostAck);
            }

            return new ProduceResult(topic, partition, record.Offset, record.Timestamp);
        }
    }

    /// <summary>Appends a keyed record to the partition chosen by <see cref="PartitionForKey"/>.</summary>
    public ProduceResult Produce(string topic, string key, MessageEnvelope message)
    {
        ArgumentNullException.ThrowIfNull(topic);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(message);
        int partitions;
        lock (_gate)
        {
            EnsureUsable();
            partitions = _logs.TryGetValue(topic, out var logs)
                ? logs.Length
                : throw UnknownTopicOrPartition(topic, partition: null, KafkaOperations.Produce, message.CorrelationId);
        }

        return Produce(topic, PartitionForKey(key, partitions), message, key);
    }

    /// <summary>
    /// Adds a member to a consumer group (creating the group if needed) subscribed to <paramref name="topics"/>. The
    /// member receives an assignment when the group's rebalance runs on the scheduler.
    /// </summary>
    public KafkaConsumer JoinGroup(string groupId, IEnumerable<string> topics, KafkaOffsetReset autoOffsetReset)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);
        ArgumentNullException.ThrowIfNull(topics);
        if (!Enum.IsDefined(autoOffsetReset))
        {
            throw new ArgumentOutOfRangeException(nameof(autoOffsetReset), autoOffsetReset, "Unknown offset reset policy.");
        }

        var subscription = topics.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToImmutableArray();
        if (subscription.IsEmpty)
        {
            throw new ArgumentException("A member must subscribe to at least one topic.", nameof(topics));
        }

        lock (_gate)
        {
            EnsureUsable();
            if (subscription.FirstOrDefault(topic => !_logs.ContainsKey(topic)) is { } unknown)
            {
                throw UnknownTopicOrPartition(unknown, partition: null, KafkaOperations.Join, correlationId: null);
            }

            if (!_groups.TryGetValue(groupId, out var group))
            {
                group = new GroupState(groupId);
                _groups.Add(groupId, group);
            }

            var member = new KafkaConsumer(this, groupId, $"{groupId}-member-{Format(++group.MemberCount)}", subscription, autoOffsetReset);
            group.Members.Add(member);
            Journal(KafkaOperations.Join, FaultPhases.After, OperationOutcome.Succeeded, groupId, $"member={member.MemberId}; topics={string.Join(",", subscription)}; reset={autoOffsetReset}");
            ScheduleRebalance(group);
            return member;
        }
    }

    /// <summary>Returns a snapshot of a partition log.</summary>
    public PartitionSnapshot GetPartition(string topic, int partition)
    {
        lock (_gate)
        {
            EnsureUsable();
            var log = _logs.TryGetValue(topic, out var logs) && partition >= 0 && partition < logs.Length
                ? logs[partition]
                : throw new KeyNotFoundException($"Partition {topic}-{partition} does not exist in cluster '{Name}'.");
            return new PartitionSnapshot(topic, partition, log.Count, [.. log]);
        }
    }

    /// <summary>Returns a snapshot of a consumer group: generation, members and assignments, and committed offsets.</summary>
    public GroupSnapshot GetGroup(string groupId)
    {
        lock (_gate)
        {
            EnsureUsable();
            var group = _groups.TryGetValue(groupId, out var state)
                ? state
                : throw new KeyNotFoundException($"Consumer group '{groupId}' does not exist in cluster '{Name}'.");
            return new GroupSnapshot(
                group.GroupId,
                group.Generation,
                group.RebalanceScheduled,
                [.. group.Members.Select(member => new GroupMemberSnapshot(member.MemberId, member.Topics, member.Assignment))],
                group.Committed.ToImmutableSortedDictionary());
        }
    }

    internal long? GetCommitted(string groupId, TopicPartition partition)
    {
        lock (_gate)
        {
            EnsureUsable();
            return _groups.TryGetValue(groupId, out var group) && group.Committed.TryGetValue(partition, out var offset) ? offset : null;
        }
    }

    internal IReadOnlyList<KafkaRecord> Poll(KafkaConsumer member, int maxRecords)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxRecords, 1);
        lock (_gate)
        {
            EnsureUsable();
            EnsureOpen(member);
            var assignment = member.Assignment;

            // Resolve every position before reading, so a reset failure consumes nothing.
            foreach (var partition in assignment)
            {
                ResolvePosition(member, partition);
            }

            var records = new List<KafkaRecord>();
            var ranges = new List<string>();
            for (var step = 0; step < assignment.Length && records.Count < maxRecords; step++)
            {
                var partition = assignment[(member.PollCursor + step) % assignment.Length];
                var log = _logs[partition.Topic][partition.Partition];
                var position = member.Positions[partition];
                var take = (int)Math.Min(maxRecords - records.Count, log.Count - position);
                if (take <= 0)
                {
                    continue;
                }

                records.AddRange(log.GetRange((int)position, take));
                member.Positions[partition] = position + take;
                ranges.Add($"{partition}@{Format(position)}..{Format(position + take - 1)}");
            }

            if (assignment.Length > 0)
            {
                member.PollCursor = (member.PollCursor + 1) % assignment.Length;
            }

            Journal(
                KafkaOperations.Poll,
                FaultPhases.After,
                OperationOutcome.Succeeded,
                member.GroupId,
                $"member={member.MemberId}; records={Format(records.Count)}{(ranges.Count > 0 ? "; " + string.Join(", ", ranges) : string.Empty)}");
            return records;
        }
    }

    internal long GetPosition(KafkaConsumer member, TopicPartition partition)
    {
        lock (_gate)
        {
            EnsureUsable();
            EnsureOpen(member);
            EnsureAssigned(member, partition);
            return ResolvePosition(member, partition);
        }
    }

    internal void Seek(KafkaConsumer member, TopicPartition partition, long offset)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        lock (_gate)
        {
            EnsureUsable();
            EnsureOpen(member);
            EnsureAssigned(member, partition);
            member.Positions[partition] = offset;
            Journal(KafkaOperations.Seek, FaultPhases.After, OperationOutcome.Succeeded, partition.ToString(), $"member={member.MemberId}; offset={Format(offset)}");
        }
    }

    internal void SeekToBoundary(KafkaConsumer member, TopicPartition partition, bool beginning)
    {
        lock (_gate)
        {
            EnsureUsable();
            EnsureOpen(member);
            EnsureAssigned(member, partition);
            var offset = beginning ? 0 : _logs[partition.Topic][partition.Partition].Count;
            member.Positions[partition] = offset;
            Journal(KafkaOperations.Seek, FaultPhases.After, OperationOutcome.Succeeded, partition.ToString(), $"member={member.MemberId}; offset={Format(offset)} ({(beginning ? "beginning" : "end")})");
        }
    }

    internal void Commit(KafkaConsumer member, IReadOnlyDictionary<TopicPartition, long>? explicitOffsets)
    {
        lock (_gate)
        {
            EnsureUsable();
            EnsureOpen(member);
            var group = _groups[member.GroupId];
            var offsets = explicitOffsets is null
                ? member.Assignment.Where(member.Positions.ContainsKey).ToDictionary(partition => partition, partition => member.Positions[partition])
                : explicitOffsets.ToDictionary();
            foreach (var (partition, offset) in offsets)
            {
                ArgumentOutOfRangeException.ThrowIfNegative(offset);
                if (!member.Assignment.Contains(partition))
                {
                    var error = $"Partition {partition} is not assigned to {member.MemberId} in generation {group.Generation}; the group has rebalanced.";
                    Journal(KafkaOperations.Commit, FaultPhases.Before, OperationOutcome.Failed, member.GroupId, $"member={member.MemberId}", error);
                    throw new KafkaException(Name, KafkaErrorCodes.CommitFailed, error);
                }
            }

            _environment.Faults.ThrowIfTriggered(this, KafkaOperations.Commit, FaultPhases.Before, stateChanged: false);
            foreach (var (partition, offset) in offsets)
            {
                group.Committed[partition] = offset;
            }

            Journal(
                KafkaOperations.Commit,
                FaultPhases.After,
                OperationOutcome.Succeeded,
                member.GroupId,
                $"member={member.MemberId}; {string.Join(", ", offsets.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}={Format(pair.Value)}"))}");
            if (_environment.Faults.Evaluate(this, KafkaOperations.Commit, FaultPhases.After, stateChanged: offsets.Count > 0) is { } lostResponse)
            {
                throw new SimulatedFaultException(lostResponse);
            }
        }
    }

    internal void Leave(KafkaConsumer member)
    {
        lock (_gate)
        {
            if (_disposed || member.IsClosed)
            {
                return;
            }

            member.IsClosed = true;
            member.Assignment = [];
            member.Positions.Clear();
            var group = _groups[member.GroupId];
            group.Members.Remove(member);
            Journal(KafkaOperations.Leave, FaultPhases.After, OperationOutcome.Succeeded, member.GroupId, $"member={member.MemberId}");
            ScheduleRebalance(group);
        }
    }

    private void ScheduleRebalance(GroupState group)
    {
        if (group.RebalanceScheduled)
        {
            return;
        }

        try
        {
            _environment.Scheduler.Schedule($"kafka:{Name}:{group.GroupId}:rebalance", TimeSpan.Zero, () => Rebalance(group));
            group.RebalanceScheduled = true;
        }
        catch (ObjectDisposedException)
        {
            // The environment is shutting down; membership no longer changes.
        }
    }

    /// <summary>
    /// Range assignment per topic: the topic's partitions, in order, are split into contiguous ranges over the members
    /// subscribed to it, in join order; the first members get one extra partition when the division is uneven.
    /// </summary>
    private void Rebalance(GroupState group)
    {
        lock (_gate)
        {
            group.RebalanceScheduled = false;
            if (_disposed)
            {
                return;
            }

            group.Generation++;
            var assignments = group.Members.ToDictionary(member => member, _ => new List<TopicPartition>());
            foreach (var topic in group.Members.SelectMany(member => member.Topics).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                var subscribers = group.Members.Where(member => member.Topics.Contains(topic)).ToList();
                var partitions = _logs[topic].Length;
                var next = 0;
                for (var index = 0; index < subscribers.Count; index++)
                {
                    var count = (partitions / subscribers.Count) + (index < partitions % subscribers.Count ? 1 : 0);
                    for (var offset = 0; offset < count; offset++)
                    {
                        assignments[subscribers[index]].Add(new TopicPartition(topic, next++));
                    }
                }
            }

            foreach (var (member, assigned) in assignments)
            {
                member.Assignment = [.. assigned.Order()];
                member.Positions.Clear();
                member.PollCursor = 0;
            }

            Journal(
                KafkaOperations.Rebalance,
                FaultPhases.After,
                OperationOutcome.Succeeded,
                group.GroupId,
                $"generation={Format(group.Generation)}; {string.Join("; ", group.Members.Select(member => $"{member.MemberId}=[{string.Join(",", member.Assignment)}]"))}");
        }
    }

    /// <summary>Position if already known; otherwise the committed offset; otherwise, or when beyond the log end, the reset policy.</summary>
    private long ResolvePosition(KafkaConsumer member, TopicPartition partition)
    {
        var logEnd = _logs[partition.Topic][partition.Partition].Count;
        var group = _groups[member.GroupId];
        long? candidate = member.Positions.TryGetValue(partition, out var position)
            ? position
            : group.Committed.TryGetValue(partition, out var committed) ? committed : null;

        if (candidate is { } known && known <= logEnd)
        {
            member.Positions[partition] = known;
            return known;
        }

        var reset = member.AutoOffsetReset switch
        {
            KafkaOffsetReset.Earliest => 0L,
            KafkaOffsetReset.Latest => logEnd,
            _ => throw (candidate is null
                ? new KafkaException(Name, KafkaErrorCodes.NoOffset, $"Partition {partition} has no committed offset for group '{member.GroupId}' and the reset policy is None.")
                : new KafkaException(Name, KafkaErrorCodes.OffsetOutOfRange, $"Offset {candidate} of {partition} is beyond the log end {logEnd} and the reset policy is None.")),
        };
        member.Positions[partition] = reset;
        return reset;
    }

    private List<KafkaRecord> GetLog(string topic, int partition, string operation, string? correlationId) =>
        _logs.TryGetValue(topic, out var logs) && partition >= 0 && partition < logs.Length
            ? logs[partition]
            : throw UnknownTopicOrPartition(topic, partition, operation, correlationId);

    private KafkaException UnknownTopicOrPartition(string topic, int? partition, string operation, string? correlationId)
    {
        var target = partition is null ? $"Topic '{topic}'" : $"Partition {topic}-{partition}";
        var message = $"{target} does not exist in cluster '{Name}'; topics are not created automatically.";
        Journal(operation, FaultPhases.Before, OperationOutcome.Failed, topic, error: message, correlationId: correlationId);
        return new KafkaException(Name, KafkaErrorCodes.UnknownTopicOrPartition, message);
    }

    private static void EnsureOpen(KafkaConsumer member)
    {
        if (member.IsClosed)
        {
            throw new InvalidOperationException($"Consumer {member.MemberId} is closed.");
        }
    }

    private static void EnsureAssigned(KafkaConsumer member, TopicPartition partition)
    {
        if (!member.Assignment.Contains(partition))
        {
            throw new InvalidOperationException($"Partition {partition} is not assigned to {member.MemberId}.");
        }
    }

    private void EnsureUsable()
    {
        _environment.ThrowIfNotReady();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_initialized)
        {
            throw new InvalidOperationException($"Cluster '{Name}' has not been initialized.");
        }
    }

    private void Journal(string operation, string phase, OperationOutcome outcome, string? target = null, string? details = null, string? error = null, Func<string?>? payload = null, string? correlationId = null) =>
        _environment.Journal.Record(ProviderName, Name, operation, phase, outcome, target, details, error, payload, correlationId);

    private static string FormatBody(MessageEnvelope message)
    {
        try
        {
            return message.GetBodyAsText();
        }
        catch (DecoderFallbackException)
        {
            return "base64:" + Convert.ToBase64String(message.GetBody());
        }
    }

    private static string Format(long value) => value.ToString(CultureInfo.InvariantCulture);

    private sealed class GroupState(string groupId)
    {
        public string GroupId { get; } = groupId;

        public List<KafkaConsumer> Members { get; } = [];

        public Dictionary<TopicPartition, long> Committed { get; } = [];

        public int Generation { get; set; }

        public int MemberCount { get; set; }

        public bool RebalanceScheduled { get; set; }
    }
}
