namespace SimForge.RabbitMq;

/// <summary>Capability identifiers of the RabbitMQ-oriented provider.</summary>
public static class RabbitMqCapabilityIds
{
    public const string Exchanges = "rabbitmq.topology.exchanges";
    public const string Queues = "rabbitmq.topology.queues";
    public const string Bindings = "rabbitmq.topology.bindings";
    public const string DefaultExchangeRouting = "rabbitmq.routing.default-exchange";
    public const string DirectRouting = "rabbitmq.routing.direct";
    public const string FanoutRouting = "rabbitmq.routing.fanout";
    public const string PublisherConfirms = "rabbitmq.publishing.confirms";
    public const string Channels = "rabbitmq.consumers.channels";
    public const string ManualAcknowledgement = "rabbitmq.consumers.manual-acknowledgement";
    public const string Prefetch = "rabbitmq.consumers.prefetch";
    public const string Redelivery = "rabbitmq.consumers.redelivery";
    public const string DeadLettering = "rabbitmq.dead-lettering.rejected";
    public const string Faults = "rabbitmq.faults";

    public const string ClientCompatibility = "rabbitmq.client-compatibility";
    public const string TopicRouting = "rabbitmq.routing.topic";
    public const string HeadersRouting = "rabbitmq.routing.headers";
    public const string ReservedNames = "rabbitmq.topology.reserved-names";
    public const string RuntimeDeclaration = "rabbitmq.topology.runtime-declaration";
    public const string ExchangeBindings = "rabbitmq.topology.exchange-bindings";
    public const string TtlAndLengthLimits = "rabbitmq.queues.ttl-and-length-limits";
    public const string QueueTypes = "rabbitmq.queues.types";
    public const string AutomaticAcknowledgement = "rabbitmq.consumers.automatic-acknowledgement";
    public const string UnlimitedPrefetch = "rabbitmq.consumers.unlimited-prefetch";
    public const string BasicGet = "rabbitmq.consumers.basic-get";
    public const string Transactions = "rabbitmq.transactions";
    public const string DurabilityAndClustering = "rabbitmq.durability-and-clustering";
}

/// <summary>Machine-readable capability manifest of <see cref="SimulatedRabbitMqBroker"/>.</summary>
public static class RabbitMqCapabilities
{
    public static CapabilityManifest Manifest { get; } = new(
        SimulatedRabbitMqBroker.ProviderName,
        "RabbitMQ-oriented exchange, queue, and delivery model",
        [
            new Capability
            {
                Id = RabbitMqCapabilityIds.Exchanges,
                Status = CapabilityStatus.SimulatedOnly,
                Level = CompatibilityLevel.ServiceSemantics,
                Summary = "Named direct and fanout exchanges plus the implicit default exchange, fixed when the broker is added.",
                Operations = ["AddRabbitMqBroker", "RabbitMqTopologyBuilder.Exchange"],
                Boundaries =
                [
                    "Names are non-empty, at most 255 UTF-8 bytes, and unique; the 'amq.' prefix is rejected.",
                    "Exchanges are not durable or auto-deleted; they exist for the lifetime of the environment.",
                ],
            },
            new Capability
            {
                Id = RabbitMqCapabilityIds.Queues,
                Status = CapabilityStatus.SimulatedOnly,
                Level = CompatibilityLevel.ServiceSemantics,
                Summary = "Named FIFO queues with ready and unacknowledged messages.",
                Operations = ["RabbitMqTopologyBuilder.Queue", "SimulatedRabbitMqBroker.GetQueue"],
                Assumptions = ["Ready messages are delivered in enqueue order; a requeued message returns to its original position."],
                Deviations = ["Queues keep messages in memory only; durability, persistence, and queue arguments are not modeled."],
            },
            new Capability
            {
                Id = RabbitMqCapabilityIds.Bindings,
                Status = CapabilityStatus.SimulatedOnly,
                Level = CompatibilityLevel.ServiceSemantics,
                Summary = "Queue-to-exchange bindings with a routing key.",
                Operations = ["RabbitMqTopologyBuilder.Bind"],
                Boundaries =
                [
                    "The queue and exchange must be declared first; binding to the default exchange is rejected.",
                    "Repeating an identical binding has no effect.",
                ],
            },
            new Capability
            {
                Id = RabbitMqCapabilityIds.DefaultExchangeRouting,
                Status = CapabilityStatus.SimulatedOnly,
                Level = CompatibilityLevel.ServiceSemantics,
                Summary = "The default exchange (\"\") routes to the queue whose name equals the routing key.",
            },
            new Capability
            {
                Id = RabbitMqCapabilityIds.DirectRouting,
                Status = CapabilityStatus.SimulatedOnly,
                Level = CompatibilityLevel.ServiceSemantics,
                Summary = "Direct exchanges route to every queue bound with a routing key equal to the message's routing key.",
                Assumptions = ["A message routed to several queues is enqueued once per queue, in binding declaration order."],
            },
            new Capability
            {
                Id = RabbitMqCapabilityIds.FanoutRouting,
                Status = CapabilityStatus.SimulatedOnly,
                Level = CompatibilityLevel.ServiceSemantics,
                Summary = "Fanout exchanges route to every bound queue and ignore the routing key.",
            },
            new Capability
            {
                Id = RabbitMqCapabilityIds.PublisherConfirms,
                Status = CapabilityStatus.SimulatedOnly,
                Level = CompatibilityLevel.ServiceSemantics,
                Summary = "Publishing returns a publisher confirmation (Routed, Returned, or Dropped), separate from consumer acknowledgement.",
                Operations = ["SimulatedRabbitMqBroker.Publish"],
                Boundaries =
                [
                    "Routed: enqueued on every target queue before the confirmation is returned.",
                    "Returned: unroutable with mandatory set; nothing is enqueued.",
                    "Dropped: unroutable without mandatory; nothing is enqueued.",
                    "Publishing to an undeclared exchange fails with not_found before any state changes.",
                ],
                Deviations =
                [
                    "Confirms are always enabled and returned synchronously; there are no publisher channels or confirm sequence numbers.",
                    "The broker never nacks a publication on its own; broker-side rejection is modeled with a fault rule.",
                ],
            },
            new Capability
            {
                Id = RabbitMqCapabilityIds.Channels,
                Status = CapabilityStatus.SimulatedOnly,
                Level = CompatibilityLevel.ServiceSemantics,
                Summary = "Consumer channels own their deliveries and delivery tags (1, 2, … per channel).",
                Operations = ["SimulatedRabbitMqBroker.OpenChannel", "RabbitMqChannel.Close"],
                Boundaries =
                [
                    "A channel error (unknown delivery tag, reused consumer tag, consuming from a missing queue) closes the channel.",
                    "Closing a channel cancels its consumers and requeues its unacknowledged deliveries.",
                    "A closed channel rejects further operations with InvalidOperationException.",
                ],
            },
            new Capability
            {
                Id = RabbitMqCapabilityIds.ManualAcknowledgement,
                Status = CapabilityStatus.SimulatedOnly,
                Level = CompatibilityLevel.ServiceSemantics,
                Summary = "Ack, nack, and reject with the multiple and requeue flags.",
                Operations = ["RabbitMqChannel.Ack", "RabbitMqChannel.Nack", "RabbitMqChannel.Reject", "RabbitMqDelivery.Ack", "RabbitMqDelivery.Nack", "RabbitMqDelivery.Reject"],
                Boundaries =
                [
                    "multiple settles every outstanding delivery up to and including the tag; tag 0 with multiple settles all.",
                    "An unknown or already settled tag fails with precondition_failed and closes the channel.",
                    "requeue: false dead-letters the message when the queue has a dead-letter exchange, and discards it otherwise.",
                ],
            },
            new Capability
            {
                Id = RabbitMqCapabilityIds.Prefetch,
                Status = CapabilityStatus.SimulatedOnly,
                Level = CompatibilityLevel.ServiceSemantics,
                Summary = "Per-consumer bounded prefetch (1-65535) and deterministic round-robin consumer selection.",
                Operations = ["RabbitMqChannel.Consume"],
                Assumptions =
                [
                    "Deliveries are pushed as scheduled work, one delivery per scheduler step, only while the scheduler is driven.",
                    "Consumers of a queue are selected round-robin in registration order, skipping consumers at their prefetch limit. This is a SimForge rule, not a documented RabbitMQ guarantee.",
                ],
                Deviations = ["Delivery handlers run one at a time in scheduler order; concurrent consumer execution is not modeled."],
            },
            new Capability
            {
                Id = RabbitMqCapabilityIds.Redelivery,
                Status = CapabilityStatus.SimulatedOnly,
                Level = CompatibilityLevel.ServiceSemantics,
                Summary = "Requeued messages (nack or reject with requeue, or channel close) are redelivered with Redelivered set.",
                Assumptions = ["Requeued messages return to their original queue position."],
            },
            new Capability
            {
                Id = RabbitMqCapabilityIds.DeadLettering,
                Status = CapabilityStatus.SimulatedOnly,
                Level = CompatibilityLevel.ServiceSemantics,
                Summary = "Rejected messages are dead-lettered to a configured exchange.",
                Operations = ["QueueBuilder.DeadLetterTo"],
                Boundaries =
                [
                    "Only rejection (reject or nack with requeue: false) dead-letters; expiry and length limits are not modeled.",
                    "The routing key is the queue's dead-letter routing key, or the message's routing key when none is set.",
                    "A missing dead-letter exchange, or an unroutable dead-lettered message, drops the message.",
                    "Dead-lettered messages carry x-first-death-queue, x-first-death-reason, x-first-death-exchange and the matching x-last-death-* headers.",
                ],
                Deviations = ["The x-death header array is not produced; headers are strings only."],
            },
            new Capability
            {
                Id = RabbitMqCapabilityIds.Faults,
                Status = CapabilityStatus.SimulatedOnly,
                Level = CompatibilityLevel.ApplicationContract,
                Summary = "Declared fault points for one-shot injected failures.",
                Operations =
                [
                    "publish:before (the broker does not accept the message; nothing is enqueued)",
                    "publish:after (the message is enqueued, but the publisher receives an error instead of the confirmation)",
                    "ack:before, nack:before, reject:before (the connection is lost: the settlement is not applied and the channel closes, requeueing its unacknowledged deliveries)",
                ],
            },
            Unsupported(RabbitMqCapabilityIds.ClientCompatibility, "AMQP 0-9-1 protocol, RabbitMQ.Client or other unchanged clients, connections, and connection strings.", CompatibilityLevel.WireProtocol),
            Unsupported(RabbitMqCapabilityIds.TopicRouting, "Topic exchanges and wildcard routing keys."),
            Unsupported(RabbitMqCapabilityIds.HeadersRouting, "Headers exchanges."),
            Unsupported(RabbitMqCapabilityIds.ReservedNames, "Predeclared 'amq.*' exchanges and server-named queues."),
            Unsupported(RabbitMqCapabilityIds.RuntimeDeclaration, "Declaring or deleting exchanges, queues, or bindings after the broker is added; purging queues."),
            Unsupported(RabbitMqCapabilityIds.ExchangeBindings, "Exchange-to-exchange bindings and alternate exchanges."),
            Unsupported(RabbitMqCapabilityIds.TtlAndLengthLimits, "Message and queue TTL, length limits and overflow, and dead-lettering for expired or dropped messages."),
            Unsupported(RabbitMqCapabilityIds.QueueTypes, "Quorum and stream queues, priorities, exclusive and auto-delete queues, and single active consumer."),
            Unsupported(RabbitMqCapabilityIds.AutomaticAcknowledgement, "Automatic acknowledgement mode."),
            Unsupported(RabbitMqCapabilityIds.UnlimitedPrefetch, "Prefetch 0 (unlimited) and channel-global QoS."),
            Unsupported(RabbitMqCapabilityIds.BasicGet, "Polling with basic.get."),
            Unsupported(RabbitMqCapabilityIds.Transactions, "AMQP transactions (tx.select, tx.commit, tx.rollback)."),
            Unsupported(RabbitMqCapabilityIds.DurabilityAndClustering, "Persistence, broker restarts, clustering, federation, and shovels."),
        ]);

    private static Capability Unsupported(string id, string summary, CompatibilityLevel level = CompatibilityLevel.ServiceSemantics) => new()
    {
        Id = id,
        Status = CapabilityStatus.Unsupported,
        Level = level,
        Summary = summary,
    };
}

/// <summary>Registers RabbitMQ-oriented broker simulations with an environment.</summary>
public static class RabbitMqEnvironmentExtensions
{
    extension(SimulationEnvironment environment)
    {
        /// <summary>
        /// Adds an in-memory, RabbitMQ-oriented broker with a fixed topology. It is not an AMQP server and cannot be used
        /// with RabbitMQ.Client.
        /// </summary>
        public SimulatedRabbitMqBroker AddRabbitMqBroker(string name, Action<RabbitMqTopologyBuilder> configureTopology)
        {
            ArgumentNullException.ThrowIfNull(environment);
            ArgumentNullException.ThrowIfNull(configureTopology);
            var builder = new RabbitMqTopologyBuilder();
            configureTopology(builder);
            return environment.AddResource(new SimulatedRabbitMqBroker(environment, name, builder.Build()));
        }
    }
}
