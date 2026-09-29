# Orders sample (simulated storage)

The storage half of a transactional-outbox workflow.

| Project | Role |
|---|---|
| `Orders.Application` | Production-style code: `OrderPlacementService` plus application-owned interfaces (`IOrdersUnitOfWork`, `IIdGenerator`). It does **not** reference SimForge. |
| `Orders.Simulated.Tests` | Test-side adapters in `Support/` that implement those interfaces on `SimForge.PostgreSql`, runner-neutral scenarios in `OrderPlacementScenarios`, and thin xUnit wrappers in `OrderPlacementTests`. |

Covered behavior:

- An order and its `OrderPlaced` outbox message commit in one transaction, and the journal shows one correlated commit
  that touched both tables.
- A storage failure before commit leaves neither the order nor the outbox message.
- A failure while writing the outbox rolls back the order.
- A lost commit response (the commit applied, but the caller saw an error) is resolved by an idempotent retry, with no
  duplicate outbox message.
- A conflicting duplicate order ID is rejected.
- Canceled and repeated runs leak no state into later runs.

Not covered yet (milestone M3): the outbox dispatcher, RabbitMQ and Kafka adapters, consumer idempotency, and broker
failure cases.

```bash
dotnet test --project samples/Orders.Simulated/Orders.Simulated.Tests -c Release
```
