# SimForge

[![Language](https://img.shields.io/badge/language-C%23-512BD4)](https://learn.microsoft.com/dotnet/csharp/)
[![.NET SDK](https://img.shields.io/badge/.NET%20SDK-10.0.401-512BD4)](global.json)
[![License](https://img.shields.io/badge/license-MIT-blue)](LICENSE)

**SimForge** is an experimental framework for .NET 10 and C# 14. It tests application workflows against
in-process simulations of databases and message brokers. Simulations run inside the test process, with no Docker,
service processes, listening sockets, or credentials. They use deterministic virtual time, explicit fault injection,
and an operation journal that records what happened. It currently implements:

- isolated simulation environments with explicit lifecycle stages. Disposal runs in reverse order, keeps both the
  original error and every cleanup error, and bounds the wait for work that ignores cancellation;
- virtual time exposed as a `TimeProvider`, with a single-driver deterministic scheduler (`RunNext`, `RunUntilIdle`,
  `AdvanceBy`) whose step bounds report runaway work instead of hanging;
- seeded, per-environment deterministic identifiers;
- one-shot fault injection at declared fault points. It separates an operation that did not happen from one that
  happened but whose success response was lost;
- an ordered operation journal with correlation scopes, opt-in payload capture, and JSON diagnostic export;
- runner-neutral scenarios, and a `ScenarioExecutor` that classifies passed, failed, skipped, canceled, and timed-out
  runs and keeps primary and cleanup errors side by side;
- `SimAssert`, assertions with no test-framework dependency;
- a thin xUnit v3 adapter, verified through xUnit's own runner;
- a PostgreSQL-oriented storage model with a declared schema, key-based reads and writes, deterministic scans,
  primary-key, not-null, and unique constraints, and single-writer transactions with atomic multi-table commit;
- immutable message envelopes whose bodies are copied on creation and on every read;
- a RabbitMQ-oriented broker model with direct, fanout, and default exchanges and publisher confirms separate from
  consumer acknowledgement. It also has per-channel delivery tags, bounded prefetch, ack/nack/reject with requeue,
  redelivery on channel close, and dead-lettering of rejected messages;
- a Kafka-oriented partitioned-log model with fixed partitions, monotonic offsets, and a documented key partitioner. Its
  consumer groups have range assignment and rebalances at scheduler boundaries, and read positions separate from
  committed offsets, with seek, replay, and earliest, latest, and none reset policies;
- machine-readable capability manifests and compatibility tables, checked against the code by tests;
- an Orders sample that uses a transactional outbox over test-side repository adapters.

Documentation lives under [`docs/`](docs/README.md): a tutorial, how-to guides, reference pages per package, and
explanations of the design.

SimForge is intentionally a test-time simulation. It is not a database engine, a driver replacement, or a wire-protocol
server. Tests built with it are component tests with simulated infrastructure, and application code keeps its own
interfaces, implemented by small adapters in the test project. A passing simulation does not prove production SQL,
client configuration, or distributed consistency; real-service tests remain the tool for that. Every provider publishes
what it models. Requests outside that fail with `UnsupportedCapabilityException` before any state changes, and every
supported capability is `SimulatedOnly` until it has passed contract tests against the real service.

## Packages

All packages target .NET 10. None is published to a package feed yet; reference the projects from a clone.

| Package | Purpose |
| --- | --- |
| `SimForge.Core` | Environments, virtual time and scheduling, deterministic IDs, fault injection, journal, diagnostics, capability manifests |
| `SimForge.Testing` | Runner-neutral scenarios, per-run services, `ScenarioExecutor`, and outcome classification |
| `SimForge.Assertions` | `SimAssert` runner-neutral assertions |
| `SimForge.PostgreSql` | PostgreSQL-oriented application-contract storage model |
| `SimForge.Messaging` | Immutable `MessageEnvelope` shared by broker models |
| `SimForge.RabbitMq` | RabbitMQ-oriented exchange, queue, and delivery model |
| `SimForge.Kafka` | Kafka-oriented partitioned-log model with consumer groups |
| `SimForge.Xunit` | Thin xUnit v3 adapter (`XunitScenario.RunAsync`) |

Only `SimForge.Xunit` references a test framework, and application code never references SimForge. Tests enforce both
rules.

## Quick start

A scenario declares its simulated resources and then runs against them. An xUnit test is a one-line wrapper:

```csharp
using SimForge;
using SimForge.Assertions;
using SimForge.PostgreSql;
using SimForge.Testing;
using SimForge.Xunit;
using Xunit;

public static class OrderScenarios
{
    public static Scenario PlaceOrder { get; } = Scenario.Create(
        "orders.place",
        setup => setup.Environment.AddPostgreSqlDatabase("shop", schema => schema
            .Table("orders", table => table
                .Column("id", ColumnType.BigInt, primaryKey: true)
                .Column("customer", ColumnType.Text, notNull: true)
                .Column("total_cents", ColumnType.BigInt, notNull: true))),
        (context, cancellationToken) =>
        {
            var shop = context.GetResource<SimulatedPostgreSqlDatabase>("shop");

            using (var transaction = shop.BeginTransaction())
            {
                transaction.Insert("orders", new Dictionary<string, object?>
                {
                    ["id"] = 1L,
                    ["customer"] = "ada",
                    ["total_cents"] = 4_250L,
                });
                transaction.Commit();
            }

            var order = SimAssert.NotNull(shop.Get("orders", 1L));
            SimAssert.Equal("ada", order.Get<string>("customer"));
            return ValueTask.CompletedTask;
        });
}

public sealed class OrderTests
{
    [Fact]
    public Task Place_order() => XunitScenario.RunAsync(OrderScenarios.PlaceOrder);
}
```

Each run gets a fresh, isolated environment, which is disposed afterwards. The
[tutorial](docs/tutorials/first-scenario.md) builds this step by step.

## Virtual time

Application code takes a `TimeProvider`. In a scenario it receives the virtual clock, and time moves only when the
scenario advances it:

```csharp
public sealed class ReminderService(TimeProvider timeProvider)
{
    public async Task<DateTimeOffset> RemindAfterAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        await Task.Delay(delay, timeProvider, cancellationToken);
        return timeProvider.GetUtcNow();
    }
}

public static class ReminderScenarios
{
    public static Scenario RemindAfterOneHour { get; } = Scenario.Create(
        "reminders.after-one-hour",
        setup => setup.AddService(context => new ReminderService(context.Clock)),
        async (context, cancellationToken) =>
        {
            var reminder = context.GetRequiredService<ReminderService>().RemindAfterAsync(TimeSpan.FromHours(1), cancellationToken);

            await context.Scheduler.AdvanceByAsync(TimeSpan.FromHours(1), cancellationToken); // no real waiting

            SimAssert.Equal(new DateTimeOffset(2026, 1, 1, 1, 0, 0, TimeSpan.Zero), await reminder);
        });
}
```

`RunUntilIdleAsync` runs work that is due now and never moves time. Work due at the same instant runs in the order it
was scheduled.

## Fault injection

A fault rule fails one chosen operation. The `after` phase models the hardest case for outbox and idempotency logic:
the change is applied, but the caller receives an error.

```csharp
public static class FaultScenarios
{
    public static Scenario LostCommitResponse { get; } = Scenario.Create(
        "orders.lost-commit-response",
        setup => setup.Environment
            .AddPostgreSqlDatabase("shop", schema => schema
                .Table("orders", table => table.Column("id", ColumnType.BigInt, primaryKey: true)))
            .InjectFault(PostgreSqlOperations.Commit, FaultPhases.After),
        (context, cancellationToken) =>
        {
            var shop = context.GetResource<SimulatedPostgreSqlDatabase>("shop");
            using var transaction = shop.BeginTransaction();
            transaction.Insert("orders", new Dictionary<string, object?> { ["id"] = 1L });

            var fault = SimAssert.Throws<SimulatedFaultException>(transaction.Commit);

            SimAssert.True(fault.StateChanged);        // the commit was applied...
            SimAssert.NotNull(shop.Get("orders", 1L)); // ...and is visible, although the caller saw an error
            return ValueTask.CompletedTask;
        });
}
```

Rules can target only fault points that a resource declares, so a mistyped rule fails when it is added instead of
silently never firing.

## Diagnostics

A scenario that does not pass fails its xUnit test with a report: the outcome, the seed that reproduces the run, where
the scenario is defined, and the journal of operations. For example, if the quick-start assertion expected `"grace"`:

```text
Scenario orders.place: Failed (Assertion) after 18.2 ms; seed -1600725367
Defined at .../OrderScenarios.cs:10
Primary error: SimForge.SimForgeAssertionException: SimAssert.Equal failed for `order.Get<string>("customer")`.
Expected: "grace"
Actual:   "ada"
Scenario 'orders.place' seed -1600725367, virtual time 2026-01-01T00:00:00.0000000+00:00, environment Disposed
Journal (6 of 6 entries):
  #1 2026-01-01T00:00:00.0000000+00:00 postgresql/shop initialize:after Succeeded
  #2 2026-01-01T00:00:00.0000000+00:00 postgresql/shop begin:after Succeeded details=tx=1
  #3 2026-01-01T00:00:00.0000000+00:00 postgresql/shop insert:after Succeeded target=orders details=tx=1; rows=1
  #4 2026-01-01T00:00:00.0000000+00:00 postgresql/shop commit:after Succeeded target=orders details=tx=1; changes=1
  #5 2026-01-01T00:00:00.0000000+00:00 postgresql/shop get:after Succeeded target=orders details=autocommit; rows=1
  #6 2026-01-01T00:00:00.0000000+00:00 postgresql/shop dispose:after Succeeded
```

The full diagnostics are attached to the test result as JSON, and they can also be exported to a directory you choose.
Payloads (keys and values) are recorded only when you opt in.

## PostgreSQL-oriented storage

`SimForge.PostgreSql` models what a persistence layer relies on, through an explicit API: tables, keys, constraints,
and transactions. It does not execute SQL, is not an Npgsql or EF Core provider, and exposes no connection string. One
transaction may be active per database. Within that, a transaction reads its own writes, other readers never see
uncommitted writes, a commit publishes every change at once, and a statement error aborts the transaction.

The capability table, value rules, and error codes are in the [PostgreSQL reference](docs/reference/postgresql.md). The
machine-readable manifest is [`postgresql.capabilities.json`](docs/reference/postgresql.capabilities.json).

## RabbitMQ-oriented messaging

`SimForge.RabbitMq` models exchanges, queues, bindings, channels, and consumers. It is not an AMQP server and does not
accept RabbitMQ.Client connections. Publishing returns a confirmation (routed, returned, or dropped), independent of
whether anyone consumes the message. Deliveries are pushed to consumer callbacks as scheduled work, so they happen when
the scenario drives the scheduler:

```csharp
var broker = context.GetResource<SimulatedRabbitMqBroker>("broker");
broker.OpenChannel().Consume("order-placed", prefetchCount: 10, async (delivery, cancellationToken) =>
{
    await handler.HandleAsync(delivery.Message.ReadJson<OrderPlaced>(), cancellationToken);
    delivery.Ack();
});

broker.Publish("orders", "order.placed", MessageEnvelope.FromJson("msg-1", new OrderPlaced(orderId, 4_250)));
await context.Scheduler.RunUntilIdleAsync(cancellationToken); // deliver
```

A lost acknowledgement (`broker.InjectFault(RabbitMqOperations.Ack)`) closes the channel and requeues the message as
redelivered, which is the case that tests consumer idempotency. See
[How to test a RabbitMQ consumer](docs/how-to/test-a-rabbitmq-consumer.md) and the
[RabbitMQ reference](docs/reference/rabbitmq.md).

## Kafka-oriented logs

`SimForge.Kafka` models topics as append-only partition logs read by consumer groups. It is not a Kafka broker and
does not accept Confluent.Kafka connections. A member's read position is separate from its group's committed offset,
and offsets are never committed automatically:

```csharp
var cluster = context.GetResource<SimulatedKafkaCluster>("kafka");
var consumer = cluster.JoinGroup("billing", ["orders"], KafkaOffsetReset.Earliest);
await context.Scheduler.RunUntilIdleAsync(cancellationToken); // runs the rebalance

cluster.Produce("orders", orderId.ToString(), MessageEnvelope.FromJson("msg-1", new OrderPlaced(orderId, 4_250)));

foreach (var record in consumer.Poll())
{
    await handler.HandleAsync(record.Message.ReadJson<OrderPlaced>(), cancellationToken);
}

consumer.Commit(); // stores the next offset to read; without it, a restart reads the records again
```

Membership changes take effect when the scheduler runs the group's rebalance, and every rebalance restarts reads from
the committed offsets. Closing a member before it commits reproduces at-least-once redelivery. See
[How to test a Kafka consumer](docs/how-to/test-a-kafka-consumer.md) and the [Kafka reference](docs/reference/kafka.md).

## Samples

[`samples/Orders.Simulated`](samples/Orders.Simulated/README.md) places orders with a transactional outbox. The
application project has no SimForge dependency. The test project implements its unit of work over the simulated
database and covers atomic commit, a failure before commit, a failed outbox write, a lost commit response resolved by an
idempotent retry, and conflicting duplicates.

## Requirements

- [.NET SDK 10.0.401](https://dotnet.microsoft.com/download/dotnet/10.0) or a later 10.0.4xx patch (see
  [global.json](global.json))
- Nothing else: no Docker, database, broker, or credentials

## Build and verify

```text
dotnet restore SimForge.slnx --locked-mode
dotnet build SimForge.slnx -c Release --no-restore
dotnet test --solution SimForge.slnx -c Release --no-build
```

`global.json` selects Microsoft Testing Platform for `dotnet test`. To run one project, use
`dotnet test --project tests/SimForge.PostgreSql.Tests -c Release`. Restore is the only step that needs the network;
the suite is deterministic and in-process. `tests/SimForge.Xunit.Fixtures` fails on purpose and is excluded from
`dotnet test`: `SimForge.Xunit.Tests` runs it with xUnit's native runner and checks xUnit's report.

When a provider's behavior changes, follow
[How to change a provider's documented capabilities](docs/how-to/change-a-provider-capability.md). The
[CI workflow](.github/workflows/simulation.yml) is set up to run the same commands on Linux, Windows, and macOS.

## Repository map

```text
src/SimForge.Core/               environment, scheduler and virtual clock, IDs, faults, journal, diagnostics, capabilities
src/SimForge.Testing/            scenarios, per-run services, executor, results
src/SimForge.Assertions/         SimAssert
src/SimForge.PostgreSql/         schema, rows, transactions, capability manifest
src/SimForge.Messaging/          immutable message envelopes
src/SimForge.RabbitMq/           exchanges, queues, channels, deliveries, capability manifest
src/SimForge.Kafka/              topics, partition logs, consumer groups, offsets, capability manifest
src/SimForge.Xunit/              xUnit v3 adapter
tests/SimForge.Core.Tests/       scheduler, timers, lifecycle, faults, journal, isolation
tests/SimForge.Testing.Tests/    outcome classification, cancellation, timeouts, services, SimAssert
tests/SimForge.PostgreSql.Tests/ constraints, transactions, state-machine walks, capability-document drift
tests/SimForge.Messaging.Tests/  envelope copy isolation, equality, JSON and text helpers
tests/SimForge.RabbitMq.Tests/   routing, confirms, deliveries, acknowledgement, dead-lettering, faults, state-machine walks
tests/SimForge.Kafka.Tests/      logs, partitioning, groups, rebalances, positions and commits, resets, faults, state-machine walks
tests/SimForge.Xunit.Tests/      outcome mapping and end-to-end checks through xUnit
tests/SimForge.Xunit.Fixtures/   deliberately failing xUnit tests, run by SimForge.Xunit.Tests
tests/SimForge.Architecture.Tests/ dependency direction and runner independence
samples/Orders.Simulated/        Orders and outbox sample with test-side adapters
docs/                            tutorial, how-to guides, reference, and explanations
```

## Roadmap

1. The Orders outbox dispatcher and idempotent consumer over RabbitMQ and Kafka, with the broker failure cases: broker
   failure after the database commit, a duplicate publish after a failed outbox update, and a lost acknowledgement or
   offset commit.
2. Fidelity evidence: an opt-in contract suite against pinned real services, so that capabilities can earn
   `VerifiedSubset`, and verification on Linux, Windows, and macOS.
3. Redis, then MongoDB, each as its own tested provider.
4. A native SimForge runner with listing, filtering, JSON results, and exit codes, running the same scenarios as
   xUnit; optional Microsoft Testing Platform integration.

## License

SimForge is licensed under the [MIT License](LICENSE).

Copyright (c) 2026 Aleksandr Pavlov.
