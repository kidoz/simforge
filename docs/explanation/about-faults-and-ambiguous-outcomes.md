# About faults and ambiguous outcomes

Most bugs in data-handling workflows hide in failure paths: the commit that fails, the message that is delivered twice,
the acknowledgement that never arrives. SimForge's fault injection is designed to reach those paths precisely and
repeatably.

## Two different failures

When a call to a database fails, one of two very different things has happened:

1. **The operation did not happen.** The commit was rejected, and nothing changed.
2. **The operation happened, but the caller never learned of it.** The commit was applied, and then the connection
   dropped before the success response arrived.

Code that treats both as "it failed, so nothing changed" loses data or duplicates it. Take a service that commits an
order and then sees an error. If it assumes nothing was written and places the order again, it writes a duplicate. The
correct response to an ambiguous outcome is usually an idempotent retry, one that recognizes the work was already done.

SimForge keeps these cases distinct. A fault rule fires at a named phase. `before` means state is unchanged, and
`after` means state changed but the caller receives an error. The resulting `SimulatedFaultException` carries a report
saying which rule fired and whether state changed, so a test can assert on the actual shape of the failure. Neither
case is ever converted into a generic rollback.

## Precise rather than random

Chaos-style testing often injects failures at random. That is useful against a running system, but a poor fit for a
regression suite, where a failure has to be reproducible. SimForge rules are one-shot and exact. A rule names a
provider, optionally a resource, an operation, a phase, and which matching occurrence should fail: "the second insert",
"the next commit's response". The same scenario hits the same failure every time.

Rules can target only fault points that a resource declares. A misspelled operation name is rejected when the rule is
added, rather than producing a test that silently never injects anything. Rules that were configured but never fired
show up in the diagnostics.

## Faults inside transactions

The PostgreSQL model follows PostgreSQL's rule that an error aborts the current transaction. An injected statement
failure therefore behaves like any other statement error: the transaction is marked failed, and it cannot commit. That
is what lets a test prove that an order and its outbox message commit together or not at all.

## Related

- [How to simulate database failures](../how-to/simulate-database-failures.md)
- [FaultInjector](../reference/core.md#faultinjector) and [PostgreSQL fault points](../reference/postgresql.md#fault-points) (reference)
