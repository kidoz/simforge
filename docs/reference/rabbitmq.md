# SimForge.RabbitMq

Namespace `SimForge.RabbitMq`. An in-memory model of RabbitMQ exchanges, queues, bindings, channels, consumers, and
delivery states. It is not an AMQP server, does not accept RabbitMQ.Client connections, and exposes no connection
string. Messages are [`MessageEnvelope`](messaging.md) values.

- [Registration and topology](#registration-and-topology)
- [SimulatedRabbitMqBroker](#simulatedrabbitmqbroker)
- [RabbitMqChannel](#rabbitmqchannel)
- [RabbitMqConsumer and RabbitMqDelivery](#rabbitmqconsumer-and-rabbitmqdelivery)
- [Delivery model](#delivery-model)
- [Fault points](#fault-points)
- [Capability table](#capability-table)
- [Error codes](#error-codes)

## Registration and topology

```csharp
SimulatedRabbitMqBroker AddRabbitMqBroker(this SimulationEnvironment environment, string name, Action<RabbitMqTopologyBuilder> configureTopology)
```

`AddRabbitMqBroker` is a C# 14 extension member. It builds the topology, creates the broker, and registers it with the
environment. The environment must be in state `Created`. The topology is fixed from then on.

| Builder member | Description |
|---|---|
| `Exchange(name, type)` | Declares an exchange. `ExchangeType.Topic` and `ExchangeType.Headers` throw `UnsupportedCapabilityException`. |
| `Queue(name, configure = null)` | Declares a queue. At least one queue is required. |
| `QueueBuilder.DeadLetterTo(exchange, routingKey = null)` | Dead-letters rejected messages to `exchange` (`""` is the default exchange). The exchange need not be declared. |
| `Bind(queue, exchange, routingKey = "")` | Binds a declared queue to a declared exchange. Binding to the default exchange throws `ArgumentException`. An identical binding is ignored. |

Names and routing keys have at most 255 UTF-8 bytes. Exchange and queue names must be non-empty and unique. Names
starting with `amq.` throw `UnsupportedCapabilityException` (`rabbitmq.topology.reserved-names`).

| `ExchangeType` | Routing |
|---|---|
| `Direct` | To every queue bound with a routing key equal to the message's routing key. |
| `Fanout` | To every bound queue; the routing key is ignored. |
| `Topic`, `Headers` | Unsupported. |

The default exchange `""` is implicit. It routes to the queue whose name equals the routing key.

## SimulatedRabbitMqBroker

Implements `ISimulationResource`. `ProviderName` is `rabbitmq`.

| Member | Description |
|---|---|
| `Name`, `Provider`, `Topology`, `FaultPoints` | Resource identity, topology, and declared fault points. |
| `Publish(exchange, routingKey, message, mandatory = false)` | Routes and enqueues the message, then returns a `PublishConfirmation`. |
| `OpenChannel()` | Opens a consumer channel. |
| `GetQueue(name)` | Returns a `QueueSnapshot(Name, ReadyCount, UnackedCount, ConsumerCount, ReadyMessages)`, with ready messages in delivery order. An unknown queue throws `KeyNotFoundException`. |
| `InjectFault(operation, phase = FaultPhases.Before, occurrence = 1, reason = null)` | Registers a fault rule for this broker. |

`PublishConfirmation(MessageId, Outcome, Queues)` reports acceptance by the broker:

| `PublishOutcome` | Condition |
|---|---|
| `Routed` | Enqueued on every queue in `Queues`. |
| `Returned` | Unroutable, with `mandatory: true`. Nothing is enqueued. |
| `Dropped` | Unroutable, with `mandatory: false`. Nothing is enqueued. |

Publishing to an undeclared exchange throws `RabbitMqException` (`not_found`) before any state changes.

## RabbitMqChannel

Implements `IDisposable` and `IAsyncDisposable`. Disposal closes the channel.

| Member | Description |
|---|---|
| `Number` | Sequential channel number within the broker. |
| `IsOpen`, `CloseReason` | Channel state; `CloseReason` is null while open. |
| `UnackedCount` | Unsettled deliveries on the channel. |
| `Consume(queue, prefetchCount, onDelivery, consumerTag = null)` | Registers a consumer and returns it. `prefetchCount` must be 1–65535; `0` throws `UnsupportedCapabilityException` (`rabbitmq.consumers.unlimited-prefetch`). The default consumer tag is `ctag-<channel>.<n>`. |
| `Ack(deliveryTag, multiple = false)` | Acknowledges the delivery. With `multiple`, acknowledges every outstanding delivery up to and including the tag. Tag `0` with `multiple` means all outstanding deliveries. |
| `Nack(deliveryTag, multiple = false, requeue = true)` | Negatively acknowledges deliveries (same tag rules as `Ack`). |
| `Reject(deliveryTag, requeue = true)` | Negatively acknowledges one delivery. |
| `Close()` | Cancels the channel's consumers and requeues its unacknowledged deliveries. Closing a closed channel does nothing. |

Channel errors close the channel and throw `RabbitMqException`:

| Condition | Error code |
|---|---|
| `Ack`, `Nack`, or `Reject` with a tag that is not outstanding on the channel | `precondition_failed` |
| `Consume` from an undeclared queue | `not_found` |
| `Consume` with a consumer tag already used on the channel | `not_allowed` |

After a channel closes, every member except `Close` and `Dispose` throws `InvalidOperationException`.

## RabbitMqConsumer and RabbitMqDelivery

| `RabbitMqConsumer` member | Description |
|---|---|
| `Channel`, `Tag`, `Queue`, `PrefetchCount` | Registration values. |
| `IsActive` | False after `Cancel` or channel close. |
| `UnackedCount` | Unsettled deliveries to this consumer. |
| `Cancel()` | Stops new deliveries. Unsettled deliveries stay on the channel and can still be settled. |

| `RabbitMqDelivery` member | Description |
|---|---|
| `Channel`, `DeliveryTag` | The channel and its tag for this delivery. |
| `Redelivered` | True when the message was delivered before and then requeued. |
| `Exchange`, `RoutingKey` | As last published or dead-lettered. |
| `Queue`, `ConsumerTag` | Where and to whom it was delivered. |
| `Message` | The immutable `MessageEnvelope`. |
| `Ack()`, `Nack(requeue = true)`, `Reject(requeue = true)` | Shorthand for the channel methods with `DeliveryTag`. |

## Delivery model

- Deliveries are pushed to `onDelivery` as scheduled work named `rabbitmq:<broker>:<queue>:deliver`. Each work item runs
  at the current virtual instant and makes one delivery. Nothing is delivered unless the scheduler is driven.
- A queue delivers its ready messages in enqueue order. Consumers are selected round-robin in registration order,
  skipping a consumer whose `UnackedCount` has reached its `PrefetchCount`.
- A requeued message (nack or reject with `requeue: true`, or channel close) returns to its original position with
  `Redelivered` set.
- `requeue: false` dead-letters the message when the queue has a dead-letter exchange, and discards it otherwise. The
  dead-letter routing key is the queue's configured key, or the message's routing key. A missing dead-letter exchange,
  or an unroutable dead-lettered message, drops the message. Dead-lettered messages gain the headers
  `x-first-death-queue`, `x-first-death-reason` (`rejected`), and `x-first-death-exchange` (set once), and the matching
  `x-last-death-*` headers (overwritten).
- An exception thrown by `onDelivery` propagates to the caller driving the scheduler. The delivery stays unacknowledged.
- The journal records `publish`, `deliver`, `ack`, `nack`, `reject`, `dead-letter`, `open-channel`, `close-channel`,
  `consume`, and `cancel`. Entries carry the message's `CorrelationId` unless a correlation scope is active. Message
  headers and bodies are recorded only when payload capture is enabled.

## Fault points

| Operation (`RabbitMqOperations`) | Phase | Effect when a rule fires |
|---|---|---|
| `publish` | `before` | The broker does not accept the message; nothing is enqueued. `SimulatedFaultException.StateChanged` is false. |
| `publish` | `after` | The message is enqueued, but `Publish` throws `SimulatedFaultException` instead of returning the confirmation. `StateChanged` is true when the message was routed. |
| `ack`, `nack`, `reject` | `before` | The settlement is not applied and the channel closes, as after a lost connection. The channel's unacknowledged deliveries are requeued. |

Settlement fault points are evaluated after the delivery tag is validated.

## Capability table

The machine-readable form is [`rabbitmq.capabilities.json`](rabbitmq.capabilities.json), generated from
`RabbitMqCapabilities.Manifest`. No capability is verified against a reference RabbitMQ version.

| Capability | Status | Level | What it covers |
|---|---|---|---|
| `rabbitmq.topology.exchanges` | SimulatedOnly | ServiceSemantics | Direct and fanout exchanges plus the implicit default exchange, fixed at registration. |
| `rabbitmq.topology.queues` | SimulatedOnly | ServiceSemantics | Named in-memory FIFO queues with ready and unacknowledged messages. |
| `rabbitmq.topology.bindings` | SimulatedOnly | ServiceSemantics | Queue-to-exchange bindings with a routing key. |
| `rabbitmq.routing.default-exchange` | SimulatedOnly | ServiceSemantics | `""` routes to the queue named by the routing key. |
| `rabbitmq.routing.direct` | SimulatedOnly | ServiceSemantics | Exact routing-key matches; one copy per target queue. |
| `rabbitmq.routing.fanout` | SimulatedOnly | ServiceSemantics | Every bound queue; routing key ignored. |
| `rabbitmq.publishing.confirms` | SimulatedOnly | ServiceSemantics | Synchronous confirmation: `Routed`, `Returned` (mandatory), or `Dropped`. Separate from consumer acknowledgement. |
| `rabbitmq.consumers.channels` | SimulatedOnly | ServiceSemantics | Per-channel delivery tags; channel errors close the channel; close requeues unacknowledged deliveries. |
| `rabbitmq.consumers.manual-acknowledgement` | SimulatedOnly | ServiceSemantics | Ack, nack, and reject with `multiple` and `requeue`. |
| `rabbitmq.consumers.prefetch` | SimulatedOnly | ServiceSemantics | Per-consumer bounded prefetch and deterministic round-robin selection. |
| `rabbitmq.consumers.redelivery` | SimulatedOnly | ServiceSemantics | Requeued messages are redelivered from their original position with `Redelivered` set. |
| `rabbitmq.dead-lettering.rejected` | SimulatedOnly | ServiceSemantics | Rejected messages go to a configured dead-letter exchange with death headers. |
| `rabbitmq.faults` | SimulatedOnly | ApplicationContract | Fault points for publish (before and after) and for lost settlements. |
| `rabbitmq.client-compatibility` | Unsupported | WireProtocol | AMQP 0-9-1, RabbitMQ.Client, connections, and connection strings. |
| `rabbitmq.routing.topic` | Unsupported | ServiceSemantics | Topic exchanges and wildcard routing keys. |
| `rabbitmq.routing.headers` | Unsupported | ServiceSemantics | Headers exchanges. |
| `rabbitmq.topology.reserved-names` | Unsupported | ServiceSemantics | Predeclared `amq.*` exchanges and server-named queues. |
| `rabbitmq.topology.runtime-declaration` | Unsupported | ServiceSemantics | Declaring, deleting, or purging after registration. |
| `rabbitmq.topology.exchange-bindings` | Unsupported | ServiceSemantics | Exchange-to-exchange bindings and alternate exchanges. |
| `rabbitmq.queues.ttl-and-length-limits` | Unsupported | ServiceSemantics | TTLs, length limits, and dead-lettering of expired or dropped messages. |
| `rabbitmq.queues.types` | Unsupported | ServiceSemantics | Quorum and stream queues, priorities, exclusive and auto-delete queues, and single active consumer. |
| `rabbitmq.consumers.automatic-acknowledgement` | Unsupported | ServiceSemantics | Automatic acknowledgement mode. |
| `rabbitmq.consumers.unlimited-prefetch` | Unsupported | ServiceSemantics | Prefetch 0 and channel-global QoS. |
| `rabbitmq.consumers.basic-get` | Unsupported | ServiceSemantics | Polling with basic.get. |
| `rabbitmq.transactions` | Unsupported | ServiceSemantics | AMQP transactions. |
| `rabbitmq.durability-and-clustering` | Unsupported | ServiceSemantics | Persistence, restarts, clustering, federation, and shovels. |

Deviations from RabbitMQ:
- Confirms are returned synchronously, without publisher channels or confirm sequence numbers.
- The broker never nacks a publication on its own.
- The `x-death` header array is not produced; headers are strings.
- Delivery handlers run one at a time.

## Error codes

SimForge error codes reuse AMQP reply-code names. No protocol parity is claimed.

| Code | Raised when |
|---|---|
| `not_found` | Publishing to an undeclared exchange, or consuming from an undeclared queue (the channel closes). |
| `precondition_failed` | Settling a tag that is not outstanding on the channel (the channel closes). |
| `not_allowed` | Reusing a consumer tag on a channel (the channel closes). |
| `injected_fault` | A fault rule fired (`SimulatedFaultException`). |
