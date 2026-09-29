using System.Collections.Immutable;
using System.Globalization;
using SimForge.Messaging;

namespace SimForge.RabbitMq;

/// <summary>
/// A RabbitMQ-oriented, in-memory broker model: exchanges, queues, bindings, channels, consumers, and delivery states,
/// with a fixed topology.
/// </summary>
/// <remarks>
/// <para>This is not an AMQP server and not a RabbitMQ.Client replacement. Unsupported requests throw
/// <see cref="UnsupportedCapabilityException"/> before any state changes. See <see cref="RabbitMqCapabilities.Manifest"/>.</para>
/// <para>Deliveries are pushed to consumers as scheduled work, one delivery per scheduler step, so they happen only while
/// the environment's scheduler is driven (for example by <c>RunUntilIdleAsync</c>). Publisher confirms are always on,
/// and <see cref="Publish"/> returns the confirmation synchronously.</para>
/// </remarks>
public sealed class SimulatedRabbitMqBroker : ISimulationResource
{
    public const string ProviderName = "rabbitmq";

    private static readonly FaultPoint[] DeclaredFaultPoints =
    [
        new(ProviderName, RabbitMqOperations.Publish, FaultPhases.Before),
        new(ProviderName, RabbitMqOperations.Publish, FaultPhases.After),
        new(ProviderName, RabbitMqOperations.Ack, FaultPhases.Before),
        new(ProviderName, RabbitMqOperations.Nack, FaultPhases.Before),
        new(ProviderName, RabbitMqOperations.Reject, FaultPhases.Before),
    ];

    private readonly Lock _gate = new();
    private readonly SimulationEnvironment _environment;
    private readonly Dictionary<string, ExchangeDefinition> _exchanges;
    private readonly Dictionary<string, QueueState> _queues = new(StringComparer.Ordinal);
    private readonly List<RabbitMqChannel> _channels = [];
    private long _sequence;
    private long _channelCount;
    private bool _initialized;
    private bool _disposed;

    internal SimulatedRabbitMqBroker(SimulationEnvironment environment, string name, RabbitMqTopology topology)
    {
        _environment = environment;
        Name = name;
        Topology = topology;
        _exchanges = topology.Exchanges.ToDictionary(exchange => exchange.Name, StringComparer.Ordinal);
    }

    public string Name { get; }

    public string Provider => ProviderName;

    public RabbitMqTopology Topology { get; }

    public IReadOnlyCollection<FaultPoint> FaultPoints => DeclaredFaultPoints;

    ValueTask ISimulationResource.InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            foreach (var queue in Topology.Queues)
            {
                _queues.Add(queue.Name, new QueueState(queue));
            }

            _initialized = true;
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>Closes every channel and discards all messages. Called by the owning environment.</summary>
    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (!_disposed)
            {
                _disposed = true;
                foreach (var channel in _channels)
                {
                    channel.IsOpen = false;
                    channel.CloseReason = "broker disposed";
                    channel.Unacked.Clear();
                    channel.Consumers.ForEach(consumer => consumer.IsActive = false);
                }

                _channels.Clear();
                _queues.Clear();
            }
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>Registers a one-shot fault for this broker. See <see cref="RabbitMqOperations"/> and <see cref="FaultPoints"/>.</summary>
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

    /// <summary>
    /// Publishes a message and returns the publisher confirmation. The default exchange (<c>""</c>) routes to the queue
    /// named by <paramref name="routingKey"/>. An unroutable message is <see cref="PublishOutcome.Returned"/> when
    /// <paramref name="mandatory"/> and otherwise <see cref="PublishOutcome.Dropped"/>.
    /// </summary>
    public PublishConfirmation Publish(string exchange, string routingKey, MessageEnvelope message, bool mandatory = false)
    {
        ArgumentNullException.ThrowIfNull(exchange);
        ArgumentNullException.ThrowIfNull(routingKey);
        ArgumentNullException.ThrowIfNull(message);
        Names.ValidateRoutingKey(routingKey);
        lock (_gate)
        {
            EnsureUsable();
            if (exchange.StartsWith("amq.", StringComparison.Ordinal))
            {
                Journal(RabbitMqOperations.Publish, FaultPhases.Before, OperationOutcome.Rejected, exchange, $"message={message.MessageId}", correlationId: message.CorrelationId);
                throw new UnsupportedCapabilityException(
                    RabbitMqCapabilityIds.ReservedNames,
                    $"Exchange '{exchange}' uses the reserved 'amq.' prefix; predeclared exchanges are not modeled");
            }

            if (exchange != RabbitMqTopology.DefaultExchange && !_exchanges.ContainsKey(exchange))
            {
                var error = $"Exchange '{exchange}' does not exist.";
                Journal(RabbitMqOperations.Publish, FaultPhases.Before, OperationOutcome.Failed, exchange, $"message={message.MessageId}", error, correlationId: message.CorrelationId);
                throw new RabbitMqException(Name, RabbitMqErrorCodes.NotFound, error);
            }

            _environment.Faults.ThrowIfTriggered(this, RabbitMqOperations.Publish, FaultPhases.Before, stateChanged: false);
            var targets = Route(exchange, routingKey);
            foreach (var queue in targets)
            {
                Enqueue(queue, message, exchange, routingKey);
            }

            var outcome = targets.Count > 0 ? PublishOutcome.Routed : mandatory ? PublishOutcome.Returned : PublishOutcome.Dropped;
            var queues = targets.Select(queue => queue.Name).ToImmutableArray();
            Journal(
                RabbitMqOperations.Publish,
                FaultPhases.After,
                OperationOutcome.Succeeded,
                exchange,
                $"routingKey={routingKey}; message={message.MessageId}; outcome={outcome}; queues={string.Join(",", queues)}",
                payload: () => FormatPayload(message),
                correlationId: message.CorrelationId);

            if (_environment.Faults.Evaluate(this, RabbitMqOperations.Publish, FaultPhases.After, stateChanged: targets.Count > 0) is { } lostConfirm)
            {
                throw new SimulatedFaultException(lostConfirm);
            }

            return new PublishConfirmation(message.MessageId, outcome, queues);
        }
    }

    public RabbitMqChannel OpenChannel()
    {
        lock (_gate)
        {
            EnsureUsable();
            var channel = new RabbitMqChannel(this, ++_channelCount);
            _channels.Add(channel);
            Journal(RabbitMqOperations.OpenChannel, FaultPhases.After, OperationOutcome.Succeeded, details: $"channel={Format(channel.Number)}");
            return channel;
        }
    }

    /// <summary>Returns a snapshot of a queue: ready messages in delivery order, and counts.</summary>
    public QueueSnapshot GetQueue(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        lock (_gate)
        {
            EnsureUsable();
            var queue = _queues.TryGetValue(name, out var state)
                ? state
                : throw new KeyNotFoundException($"Queue '{name}' does not exist in broker '{Name}'.");
            return new QueueSnapshot(
                queue.Name,
                queue.Ready.Count,
                queue.Unacked,
                queue.Consumers.Count,
                [.. queue.Ready.Values.Select(message => message.Message)]);
        }
    }

    internal RabbitMqConsumer Consume(RabbitMqChannel channel, string queueName, ushort prefetchCount, Func<RabbitMqDelivery, CancellationToken, ValueTask> handler, string? consumerTag)
    {
        ArgumentNullException.ThrowIfNull(queueName);
        ArgumentNullException.ThrowIfNull(handler);
        lock (_gate)
        {
            EnsureUsable();
            EnsureOpen(channel);
            if (prefetchCount == 0)
            {
                throw new UnsupportedCapabilityException(
                    RabbitMqCapabilityIds.UnlimitedPrefetch,
                    "A prefetch count of 0 (unlimited) is not supported; use a bounded prefetch of 1 or more");
            }

            if (!_queues.TryGetValue(queueName, out var queue))
            {
                throw ChannelError(channel, RabbitMqErrorCodes.NotFound, $"Queue '{queueName}' does not exist.", RabbitMqOperations.Consume);
            }

            var tag = consumerTag ?? $"ctag-{Format(channel.Number)}.{Format(channel.ConsumerCount + 1)}";
            if (channel.Consumers.Exists(consumer => consumer.Tag == tag))
            {
                throw ChannelError(channel, RabbitMqErrorCodes.NotAllowed, $"Consumer tag '{tag}' is already in use on channel {channel.Number}.", RabbitMqOperations.Consume);
            }

            channel.ConsumerCount++;
            var created = new RabbitMqConsumer(channel, tag, queueName, prefetchCount, handler);
            channel.Consumers.Add(created);
            queue.Consumers.Add(created);
            Journal(RabbitMqOperations.Consume, FaultPhases.After, OperationOutcome.Succeeded, queueName, $"channel={Format(channel.Number)}; consumer={tag}; prefetch={Format(prefetchCount)}");
            ScheduleDispatch(queue);
            return created;
        }
    }

    internal void CancelConsumer(RabbitMqConsumer consumer)
    {
        lock (_gate)
        {
            if (!consumer.IsActive || _disposed)
            {
                return;
            }

            consumer.IsActive = false;
            _queues[consumer.Queue].Consumers.Remove(consumer);
            Journal(RabbitMqOperations.Cancel, FaultPhases.After, OperationOutcome.Succeeded, consumer.Queue, $"channel={Format(consumer.Channel.Number)}; consumer={consumer.Tag}");
        }
    }

    internal void Settle(RabbitMqChannel channel, string operation, ulong deliveryTag, bool multiple, bool requeue)
    {
        lock (_gate)
        {
            EnsureUsable();
            EnsureOpen(channel);
            var details = $"channel={Format(channel.Number)}; tag={Format(deliveryTag)}; multiple={Format(multiple)}";
            List<KeyValuePair<ulong, UnackedDelivery>> settled;
            if (multiple && deliveryTag == 0)
            {
                settled = [.. channel.Unacked];
            }
            else if (!channel.Unacked.ContainsKey(deliveryTag))
            {
                throw ChannelError(channel, RabbitMqErrorCodes.PreconditionFailed, $"Unknown delivery tag {deliveryTag} on channel {channel.Number}.", operation);
            }
            else
            {
                settled = multiple
                    ? [.. channel.Unacked.Where(pair => pair.Key <= deliveryTag)]
                    : [new KeyValuePair<ulong, UnackedDelivery>(deliveryTag, channel.Unacked[deliveryTag])];
            }

            if (_environment.Faults.Evaluate(this, operation, FaultPhases.Before, stateChanged: false) is { } lost)
            {
                CloseChannelLocked(channel, $"connection lost ({lost.RuleId})");
                throw new SimulatedFaultException(lost);
            }

            var affected = new List<QueueState>();
            foreach (var (tag, delivery) in settled)
            {
                channel.Unacked.Remove(tag);
                delivery.Consumer.UnackedCount--;
                delivery.Queue.Unacked--;
                AddOnce(affected, delivery.Queue);
                if (operation == RabbitMqOperations.Ack)
                {
                    continue;
                }

                if (requeue)
                {
                    delivery.Message.Redelivered = true;
                    delivery.Queue.Ready.Add(delivery.Message.Sequence, delivery.Message);
                }
                else
                {
                    DeadLetter(delivery.Queue, delivery.Message);
                }
            }

            var effect = operation == RabbitMqOperations.Ack ? "acked" : requeue ? "requeued" : "rejected";
            Journal(operation, FaultPhases.After, OperationOutcome.Succeeded, string.Join(",", affected.Select(queue => queue.Name)), $"{details}; {effect}={Format(settled.Count)}");
            foreach (var queue in affected)
            {
                ScheduleDispatch(queue);
            }
        }
    }

    internal void CloseChannel(RabbitMqChannel channel, string reason)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            CloseChannelLocked(channel, reason);
        }
    }

    private void CloseChannelLocked(RabbitMqChannel channel, string reason)
    {
        if (!channel.IsOpen)
        {
            return;
        }

        channel.IsOpen = false;
        channel.CloseReason = reason;
        foreach (var consumer in channel.Consumers.Where(consumer => consumer.IsActive))
        {
            consumer.IsActive = false;
            _queues[consumer.Queue].Consumers.Remove(consumer);
        }

        var affected = new List<QueueState>();
        foreach (var (_, delivery) in channel.Unacked)
        {
            delivery.Consumer.UnackedCount--;
            delivery.Queue.Unacked--;
            delivery.Message.Redelivered = true;
            delivery.Queue.Ready.Add(delivery.Message.Sequence, delivery.Message);
            AddOnce(affected, delivery.Queue);
        }

        var requeued = channel.Unacked.Count;
        channel.Unacked.Clear();
        _channels.Remove(channel);
        Journal(RabbitMqOperations.CloseChannel, FaultPhases.After, OperationOutcome.Succeeded, details: $"channel={Format(channel.Number)}; requeued={Format(requeued)}; reason={reason}");
        foreach (var queue in affected)
        {
            ScheduleDispatch(queue);
        }
    }

    private RabbitMqException ChannelError(RabbitMqChannel channel, string code, string message, string operation)
    {
        Journal(operation, FaultPhases.Before, OperationOutcome.Failed, details: $"channel={Format(channel.Number)}", error: message);
        CloseChannelLocked(channel, $"{code}: {message}");
        return new RabbitMqException(Name, code, $"{message} The channel was closed.");
    }

    private List<QueueState> Route(string exchange, string routingKey)
    {
        if (exchange == RabbitMqTopology.DefaultExchange)
        {
            return _queues.TryGetValue(routingKey, out var direct) ? [direct] : [];
        }

        var type = _exchanges[exchange].Type;
        return [.. Topology.Bindings
            .Where(binding => binding.Exchange == exchange && (type == ExchangeType.Fanout || binding.RoutingKey == routingKey))
            .Select(binding => _queues[binding.Queue])
            .Distinct()];
    }

    private void Enqueue(QueueState queue, MessageEnvelope message, string exchange, string routingKey)
    {
        var sequence = ++_sequence;
        queue.Ready.Add(sequence, new QueuedMessage(sequence, message, exchange, routingKey));
        ScheduleDispatch(queue);
    }

    private void DeadLetter(QueueState queue, QueuedMessage message)
    {
        if (queue.Definition.DeadLetterExchange is not { } exchange)
        {
            Journal(RabbitMqOperations.DeadLetter, FaultPhases.After, OperationOutcome.Succeeded, queue.Name, $"message={message.Message.MessageId}; discarded (no dead-letter exchange)", correlationId: message.Message.CorrelationId);
            return;
        }

        var routingKey = queue.Definition.DeadLetterRoutingKey ?? message.RoutingKey;
        var headers = new Dictionary<string, string>(message.Message.Headers, StringComparer.Ordinal);
        headers.TryAdd("x-first-death-queue", queue.Name);
        headers.TryAdd("x-first-death-reason", "rejected");
        headers.TryAdd("x-first-death-exchange", message.Exchange);
        headers["x-last-death-queue"] = queue.Name;
        headers["x-last-death-reason"] = "rejected";
        headers["x-last-death-exchange"] = message.Exchange;
        var deadLettered = message.Message with { Headers = headers };

        var targets = exchange == RabbitMqTopology.DefaultExchange || _exchanges.ContainsKey(exchange) ? Route(exchange, routingKey) : [];
        foreach (var target in targets)
        {
            Enqueue(target, deadLettered, exchange, routingKey);
        }

        var result = targets.Count > 0
            ? $"queues={string.Join(",", targets.Select(target => target.Name))}"
            : _exchanges.ContainsKey(exchange) || exchange == RabbitMqTopology.DefaultExchange ? "dropped (unroutable)" : "dropped (dead-letter exchange not found)";
        Journal(
            RabbitMqOperations.DeadLetter,
            FaultPhases.After,
            OperationOutcome.Succeeded,
            queue.Name,
            $"message={message.Message.MessageId}; exchange={exchange}; routingKey={routingKey}; {result}",
            correlationId: message.Message.CorrelationId);
    }

    private void ScheduleDispatch(QueueState queue)
    {
        if (queue.DispatchScheduled || queue.Ready.Count == 0 || !queue.Consumers.Exists(consumer => consumer.HasCapacity))
        {
            return;
        }

        try
        {
            _environment.Scheduler.Schedule($"rabbitmq:{Name}:{queue.Name}:deliver", TimeSpan.Zero, cancellationToken => DeliverNextAsync(queue, cancellationToken));
            queue.DispatchScheduled = true;
        }
        catch (ObjectDisposedException)
        {
            // The environment is shutting down; no further deliveries are made.
        }
    }

    private static void AddOnce(List<QueueState> queues, QueueState queue)
    {
        if (!queues.Contains(queue))
        {
            queues.Add(queue);
        }
    }

    private async ValueTask DeliverNextAsync(QueueState queue, CancellationToken cancellationToken)
    {
        RabbitMqDelivery delivery;
        RabbitMqConsumer consumer;
        lock (_gate)
        {
            queue.DispatchScheduled = false;
            if (_disposed || queue.Ready.Count == 0 || NextConsumer(queue) is not { } next)
            {
                return;
            }

            consumer = next;
            var message = queue.Ready.Values.First();
            queue.Ready.Remove(message.Sequence);
            var channel = consumer.Channel;
            var tag = ++channel.LastDeliveryTag;
            channel.Unacked.Add(tag, new UnackedDelivery(message, queue, consumer));
            consumer.UnackedCount++;
            queue.Unacked++;
            delivery = new RabbitMqDelivery(channel, tag, message.Redelivered, message.Exchange, message.RoutingKey, queue.Name, consumer.Tag, message.Message);
            Journal(
                RabbitMqOperations.Deliver,
                FaultPhases.After,
                OperationOutcome.Succeeded,
                queue.Name,
                $"channel={Format(channel.Number)}; tag={Format(tag)}; consumer={consumer.Tag}; message={message.Message.MessageId}; redelivered={Format(message.Redelivered)}",
                correlationId: message.Message.CorrelationId);
            ScheduleDispatch(queue);
        }

        await consumer.Handler(delivery, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Round-robin over the queue's consumers, in registration order, skipping consumers at their prefetch limit.</summary>
    private static RabbitMqConsumer? NextConsumer(QueueState queue)
    {
        var count = queue.Consumers.Count;
        for (var offset = 0; offset < count; offset++)
        {
            var index = (queue.NextConsumer + offset) % count;
            if (queue.Consumers[index].HasCapacity)
            {
                queue.NextConsumer = (index + 1) % count;
                return queue.Consumers[index];
            }
        }

        return null;
    }

    private void EnsureUsable()
    {
        _environment.ThrowIfNotReady();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_initialized)
        {
            throw new InvalidOperationException($"Broker '{Name}' has not been initialized.");
        }
    }

    private static void EnsureOpen(RabbitMqChannel channel)
    {
        if (!channel.IsOpen)
        {
            throw new InvalidOperationException($"Channel {channel.Number} is closed ({channel.CloseReason}).");
        }
    }

    private void Journal(string operation, string phase, OperationOutcome outcome, string? target = null, string? details = null, string? error = null, Func<string?>? payload = null, string? correlationId = null) =>
        _environment.Journal.Record(ProviderName, Name, operation, phase, outcome, target, details, error, payload, correlationId);

    private static string FormatPayload(MessageEnvelope message)
    {
        var headers = string.Join(", ", message.Headers.Select(pair => $"{pair.Key}={pair.Value}"));
        string body;
        try
        {
            body = message.GetBodyAsText();
        }
        catch (System.Text.DecoderFallbackException)
        {
            body = "base64:" + Convert.ToBase64String(message.GetBody());
        }

        return $"type={message.Type}; headers=[{headers}]; body={body}";
    }

    private static string Format(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Format(ulong value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Format(bool value) => value ? "true" : "false";
}
