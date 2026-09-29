using System.Collections.Immutable;
using SimForge.Messaging;

namespace SimForge.RabbitMq;

/// <summary>Operation names used in the journal and by fault rules.</summary>
public static class RabbitMqOperations
{
    public const string Publish = "publish";
    public const string Deliver = "deliver";
    public const string Ack = "ack";
    public const string Nack = "nack";
    public const string Reject = "reject";
    public const string DeadLetter = "dead-letter";
    public const string OpenChannel = "open-channel";
    public const string CloseChannel = "close-channel";
    public const string Consume = "consume";
    public const string Cancel = "cancel";
}

/// <summary>SimForge error codes for the RabbitMQ-oriented model. The names follow AMQP reply codes, but no protocol parity is claimed.</summary>
public static class RabbitMqErrorCodes
{
    /// <summary>The exchange or queue does not exist.</summary>
    public const string NotFound = "not_found";

    /// <summary>An unknown or already settled delivery tag was acknowledged; the channel is closed.</summary>
    public const string PreconditionFailed = "precondition_failed";

    /// <summary>A consumer tag was reused on a channel; the channel is closed.</summary>
    public const string NotAllowed = "not_allowed";
}

/// <summary>An error reported by the simulated broker. Errors raised on a channel also close that channel.</summary>
public sealed class RabbitMqException : SimulatedServiceException
{
    public RabbitMqException(string resource, string errorCode, string message)
        : base(SimulatedRabbitMqBroker.ProviderName, resource, errorCode, message)
    {
    }
}

/// <summary>What the broker did with a published message.</summary>
public enum PublishOutcome
{
    /// <summary>Enqueued on at least one queue.</summary>
    Routed,

    /// <summary>Unroutable and published with <c>mandatory: true</c>; returned to the publisher and not enqueued.</summary>
    Returned,

    /// <summary>Unroutable and published with <c>mandatory: false</c>; discarded.</summary>
    Dropped,
}

/// <summary>Publisher confirmation. It reports acceptance by the broker and is unrelated to consumer acknowledgement.</summary>
public sealed record PublishConfirmation(string MessageId, PublishOutcome Outcome, ImmutableArray<string> Queues);

/// <summary>Point-in-time view of a queue.</summary>
public sealed record QueueSnapshot(string Name, int ReadyCount, int UnackedCount, int ConsumerCount, ImmutableArray<MessageEnvelope> ReadyMessages);

/// <summary>A message delivered to a consumer. It must be settled with ack, nack, or reject on the channel it arrived on.</summary>
public sealed class RabbitMqDelivery
{
    internal RabbitMqDelivery(RabbitMqChannel channel, ulong deliveryTag, bool redelivered, string exchange, string routingKey, string queue, string consumerTag, MessageEnvelope message)
    {
        Channel = channel;
        DeliveryTag = deliveryTag;
        Redelivered = redelivered;
        Exchange = exchange;
        RoutingKey = routingKey;
        Queue = queue;
        ConsumerTag = consumerTag;
        Message = message;
    }

    public RabbitMqChannel Channel { get; }

    /// <summary>Channel-scoped tag: 1, 2, … in delivery order on the channel.</summary>
    public ulong DeliveryTag { get; }

    /// <summary>True when the message was delivered before and then requeued.</summary>
    public bool Redelivered { get; }

    /// <summary>Exchange the message was last published or dead-lettered to.</summary>
    public string Exchange { get; }

    public string RoutingKey { get; }

    public string Queue { get; }

    public string ConsumerTag { get; }

    public MessageEnvelope Message { get; }

    public void Ack() => Channel.Ack(DeliveryTag);

    public void Nack(bool requeue = true) => Channel.Nack(DeliveryTag, multiple: false, requeue);

    public void Reject(bool requeue = true) => Channel.Reject(DeliveryTag, requeue);

    public override string ToString() => $"Delivery {DeliveryTag} of {Message} from '{Queue}'{(Redelivered ? " (redelivered)" : string.Empty)}";
}

/// <summary>A consumer registered on a channel. Cancelling it stops new deliveries; its unacknowledged deliveries stay on the channel.</summary>
public sealed class RabbitMqConsumer
{
    internal RabbitMqConsumer(RabbitMqChannel channel, string tag, string queue, ushort prefetchCount, Func<RabbitMqDelivery, CancellationToken, ValueTask> handler)
    {
        Channel = channel;
        Tag = tag;
        Queue = queue;
        PrefetchCount = prefetchCount;
        Handler = handler;
    }

    public RabbitMqChannel Channel { get; }

    public string Tag { get; }

    public string Queue { get; }

    /// <summary>Maximum number of unacknowledged deliveries to this consumer.</summary>
    public ushort PrefetchCount { get; }

    public bool IsActive { get; internal set; } = true;

    /// <summary>Deliveries to this consumer that are not yet settled.</summary>
    public int UnackedCount { get; internal set; }

    internal Func<RabbitMqDelivery, CancellationToken, ValueTask> Handler { get; }

    internal bool HasCapacity => IsActive && UnackedCount < PrefetchCount;

    public void Cancel() => Channel.Broker.CancelConsumer(this);
}

/// <summary>
/// A consumer session. It owns the delivery tags of its deliveries. Closing it, explicitly, through disposal, or because
/// of a channel error, requeues its unacknowledged deliveries and cancels its consumers.
/// </summary>
public sealed class RabbitMqChannel : IDisposable, IAsyncDisposable
{
    internal RabbitMqChannel(SimulatedRabbitMqBroker broker, long number)
    {
        Broker = broker;
        Number = number;
    }

    public long Number { get; }

    public bool IsOpen { get; internal set; } = true;

    /// <summary>Why the channel closed, or null while it is open.</summary>
    public string? CloseReason { get; internal set; }

    /// <summary>Deliveries on this channel that are not yet settled.</summary>
    public int UnackedCount => Unacked.Count;

    internal SimulatedRabbitMqBroker Broker { get; }

    internal SortedDictionary<ulong, UnackedDelivery> Unacked { get; } = [];

    internal List<RabbitMqConsumer> Consumers { get; } = [];

    internal ulong LastDeliveryTag { get; set; }

    internal int ConsumerCount { get; set; }

    /// <summary>
    /// Registers a consumer. Deliveries are pushed to <paramref name="onDelivery"/> as scheduled work, one delivery per
    /// scheduler step, while at most <paramref name="prefetchCount"/> deliveries to this consumer are unacknowledged.
    /// </summary>
    public RabbitMqConsumer Consume(string queue, ushort prefetchCount, Func<RabbitMqDelivery, CancellationToken, ValueTask> onDelivery, string? consumerTag = null) =>
        Broker.Consume(this, queue, prefetchCount, onDelivery, consumerTag);

    /// <summary>Acknowledges a delivery, or with <paramref name="multiple"/> every outstanding delivery up to and including it (tag 0: all).</summary>
    public void Ack(ulong deliveryTag, bool multiple = false) => Broker.Settle(this, RabbitMqOperations.Ack, deliveryTag, multiple, requeue: false);

    /// <summary>Negatively acknowledges deliveries. Requeued messages return to their original position; others are dead-lettered or discarded.</summary>
    public void Nack(ulong deliveryTag, bool multiple = false, bool requeue = true) => Broker.Settle(this, RabbitMqOperations.Nack, deliveryTag, multiple, requeue);

    /// <summary>Rejects one delivery, like <see cref="Nack"/> without <c>multiple</c>.</summary>
    public void Reject(ulong deliveryTag, bool requeue = true) => Broker.Settle(this, RabbitMqOperations.Reject, deliveryTag, multiple: false, requeue);

    /// <summary>Closes the channel, requeueing its unacknowledged deliveries. Closing a closed channel does nothing.</summary>
    public void Close() => Broker.CloseChannel(this, "closed by the application");

    public void Dispose() => Close();

    public ValueTask DisposeAsync()
    {
        Close();
        return ValueTask.CompletedTask;
    }

    public override string ToString() => $"Channel {Number}{(IsOpen ? string.Empty : $" (closed: {CloseReason})")}";
}

internal sealed class QueuedMessage(long sequence, MessageEnvelope message, string exchange, string routingKey)
{
    /// <summary>Enqueue order; a requeued message keeps it and so returns to its original position.</summary>
    public long Sequence { get; } = sequence;

    public MessageEnvelope Message { get; } = message;

    public string Exchange { get; } = exchange;

    public string RoutingKey { get; } = routingKey;

    public bool Redelivered { get; set; }
}

internal sealed record UnackedDelivery(QueuedMessage Message, QueueState Queue, RabbitMqConsumer Consumer);

internal sealed class QueueState(QueueDefinition definition)
{
    public QueueDefinition Definition { get; } = definition;

    public string Name => Definition.Name;

    public SortedDictionary<long, QueuedMessage> Ready { get; } = [];

    public List<RabbitMqConsumer> Consumers { get; } = [];

    public int NextConsumer { get; set; }

    public int Unacked { get; set; }

    public bool DispatchScheduled { get; set; }
}
