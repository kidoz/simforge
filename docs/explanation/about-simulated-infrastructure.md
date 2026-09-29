# About simulated infrastructure

SimForge exists for a particular kind of test: an application workflow that crosses a database or a message broker,
exercised quickly, repeatably, and with failures you choose. This page covers what that kind of test can tell you, what
it cannot, and why SimForge is shaped the way it is.

## Component tests with simulated infrastructure

A SimForge test runs your application code against models of a database or broker that live inside the test process.
There is no Docker, no service process, no listening socket, and no credential. Two things follow. The tests are fast
and isolated enough to run in parallel on any machine. And their results depend only on the code and the scenario, not
on the state of some shared server.

The right name for such a test is a *component test with simulated infrastructure*. Running it with a unit-test runner
does not make it an integration test against the real service, and SimForge avoids suggesting otherwise. A passing
simulation shows that your workflow behaves correctly against the *modeled* behavior. It does not prove that your
production SQL is correct, that a client library is configured properly, that the network recovers, or that a
distributed system stays consistent.

## Why applications keep their own interfaces

The obvious shortcut would be a fake that impersonates a production client, such as an in-memory object that looks
like an `NpgsqlConnection`. SimForge deliberately does not do that. A stateful fake of a driver quietly implies
compatibility with everything the driver can do, and every gap then turns into a silent wrong answer.

Instead, application code talks to interfaces it owns, such as a repository or a publisher. Tests implement those
interfaces with small adapters over SimForge's explicit APIs. The adapter is visible and reviewable. Its limits are the
limits of the model, which are written down.

## Why unsupported behavior fails loudly

Every provider declares what it models in a capability manifest. Anything outside that declaration throws
`UnsupportedCapabilityException` *before* any state changes. Returning an empty result, ignoring an option, or falling
back to in-memory LINQ would all make a test pass that should not. An explicit failure costs you a moment and tells the
truth; a plausible-looking answer can cost you a production incident.

## Evidence, not assumptions

Every capability carries a status. `SimulatedOnly` means SimForge's own tests cover the behavior, but nobody has compared
it with the real service. `VerifiedSubset` is reserved for behavior with passing contract cases against a pinned
version of the real service, and the manifest type refuses that status without the evidence. Today every supported
capability is `SimulatedOnly`. The vocabulary exists so that fidelity claims can grow only as fast as the evidence does.

Compatibility levels describe a different axis: how close to the real service an integration sits. SimForge implements
the `ApplicationContract` and `ServiceSemantics` levels. Client-library, wire-protocol, and real-engine modes are
possible future directions. Package names never imply them.

## Alternatives, and where they fit

- **Real services in containers** (for example, Testcontainers) give the highest fidelity. They are the right tool for
  verifying SQL, drivers, and configuration. They are slower, need a container runtime, and make precise failure
  injection hard. SimForge complements them rather than replacing them.
- **EF Core InMemory or SQLite** have their own semantics. They are useful for their own purposes, but SimForge will not
  present them as PostgreSQL.
- **Mocks** verify interactions but model no state. They cannot tell you whether an order and its outbox message
  committed together.

## Related

- [About determinism and virtual time](about-determinism-and-virtual-time.md)
- [About the PostgreSQL model](about-the-postgresql-model.md)
- [Capability manifests](../reference/capabilities.md) (reference)
