# About the RabbitMQ model

`SimForge.RabbitMq` models the part of RabbitMQ that decides whether a message is lost, duplicated, or stuck: routing,
publisher confirmation, delivery, acknowledgement, requeueing, and dead-lettering. This page explains which choices
shape it.

## Two handoffs, not one

A message changes hands twice. The publisher hands it to the broker, and the broker hands it to a consumer. RabbitMQ
acknowledges each handoff separately. A publisher confirm says the broker accepted the message; a consumer
acknowledgement says the consumer is done with it. Code that conflates them tends to believe a message was processed
because it was published, or published because it was processed.

The model keeps them apart. `Publish` returns a confirmation (routed, returned, or dropped) whether or not anyone
consumes the message. Consumer acknowledgement happens later, per delivery, on a channel. Each handoff can fail on its
own: a publish can be rejected, or accepted with its confirmation lost, and a consumer's acknowledgement can be lost
after the work is done.

## Why lost acknowledgements close the channel

A real acknowledgement is not lost in isolation. It is lost because the connection carrying it failed. RabbitMQ then
requeues every unacknowledged delivery on that channel and redelivers it to whoever consumes next, with the
`redelivered` flag set. The model reproduces that chain: a fault on `ack`, `nack`, or `reject` closes the channel. This
is exactly the situation where a consumer must be idempotent, because the work was done and the message comes back
anyway.

For the same reason, the model is as strict as RabbitMQ about delivery tags. Acknowledging a tag twice, or on the wrong
channel, is a `precondition_failed` channel error that closes the channel and requeues its deliveries. That behavior
surprises many applications in production; the simulation lets it surprise them in a test instead.

## Deliveries as scheduled work

RabbitMQ pushes messages to consumers asynchronously. The model does the same, but through SimForge's scheduler: each
delivery is one unit of scheduled work, made when the test drives the scheduler. Delivery order is therefore
reproducible. Queues deliver in order, and requeued messages return to their original position. Consumers take turns in
a fixed round-robin order and are limited by their prefetch.

Making every delivery its own scheduler step also turns a classic bug into a visible failure. A consumer that nacks
and requeues the same message forever is caught by the scheduler's step bound, instead of hanging the test run.

The round-robin order is SimForge's own rule. RabbitMQ does not document a distribution order between consumers, so
tests should not rely on one consumer receiving a particular message in production.

## One model per broker

It is tempting to hide RabbitMQ and Kafka behind a single "message broker" abstraction. SimForge deliberately does not.
RabbitMQ settles each delivery individually and can requeue it; Kafka consumers track an offset in a log and replay by
moving it. An interface that pretends these are the same makes both look wrong. The brokers share only the immutable
`MessageEnvelope`; acknowledgement and offset semantics stay with each model.

Retry policy also stays out of the broker. Requeueing a message is a broker feature. How often to retry, and how long to
wait, is an application decision, and it belongs in application or test code, where virtual time can drive it.

## What is deliberately left out

Topic and headers exchanges, TTLs, length limits, quorum and stream queues, priorities, automatic acknowledgement, and
transactions are not modeled. Attempts to use them fail explicitly, never by approximation. Neither are durability,
clustering, or the AMQP protocol itself. A test passing against this model says nothing about how a real broker behaves
across restarts or network partitions.

## Related

- [How to test a RabbitMQ consumer](../how-to/test-a-rabbitmq-consumer.md)
- [SimForge.RabbitMq](../reference/rabbitmq.md) (reference)
- [About faults and ambiguous outcomes](about-faults-and-ambiguous-outcomes.md)
