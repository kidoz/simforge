# Packages

All packages target .NET 10 (`net10.0`) and C# 14. None of them is published to a package feed yet.

| Package | Namespace | Contents | Depends on |
|---|---|---|---|
| `SimForge.Core` | `SimForge` | Environments, resources, virtual time and scheduling, deterministic IDs, fault injection, operation journal, diagnostics, capability manifests, exceptions. See [core](core.md). | .NET runtime only |
| `SimForge.Testing` | `SimForge.Testing` | Scenario contracts, per-run services, `ScenarioExecutor`, results. See [testing](testing.md). | `SimForge.Core` |
| `SimForge.Assertions` | `SimForge.Assertions` | `SimAssert`. See [assertions](assertions.md). | `SimForge.Core` |
| `SimForge.PostgreSql` | `SimForge.PostgreSql` | PostgreSQL-oriented application-contract storage model. See [PostgreSQL](postgresql.md). | `SimForge.Core` |
| `SimForge.Messaging` | `SimForge.Messaging` | Immutable `MessageEnvelope` shared by broker models. See [messaging](messaging.md). | none |
| `SimForge.RabbitMq` | `SimForge.RabbitMq` | RabbitMQ-oriented exchange, queue, and delivery model. See [RabbitMQ](rabbitmq.md). | `SimForge.Core`, `SimForge.Messaging` |
| `SimForge.Xunit` | `SimForge.Xunit` | xUnit v3 adapter. See [xUnit adapter](xunit.md). | `SimForge.Testing`, `xunit.v3.extensibility.core`, `xunit.v3.assert` |

## Dependency rules

- `SimForge.Xunit` is the only package that references a test framework.
- `SimForge.Core` references no other SimForge package and no third-party package.
- Storage providers (`SimForge.PostgreSql`) reference only `SimForge.Core`. Broker providers (`SimForge.RabbitMq`)
  reference only `SimForge.Core` and `SimForge.Messaging`.
- `SimForge.Messaging` references nothing and contains no broker behavior.
- Application code under test never references SimForge. Simulation adapters live in test projects.

`tests/SimForge.Architecture.Tests` enforces these rules against both the project files and the compiled assemblies.
