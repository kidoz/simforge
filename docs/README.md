# SimForge documentation

The documentation is organized with [Diátaxis](https://diataxis.fr/). Each page serves one need: learning, doing a task,
looking something up, or understanding.

## Tutorials: learning by doing

- [Test an order workflow with a simulated database](tutorials/first-scenario.md). Start here.

## How-to guides: solving a specific problem

- [How to test time-dependent code](how-to/test-time-dependent-code.md)
- [How to simulate database failures](how-to/simulate-database-failures.md)
- [How to back a repository with simulated storage](how-to/back-a-repository-with-simulated-storage.md)
- [How to reproduce and diagnose a failing scenario](how-to/reproduce-a-failing-scenario.md)
- [How to change a provider's documented capabilities](how-to/change-a-provider-capability.md) (contributors)

## Reference: technical description

- [Packages](reference/packages.md): packages, namespaces, and dependency rules
- [SimForge.Core](reference/core.md): environment, scheduler and virtual clock, deterministic IDs, faults, journal, diagnostics, exceptions
- [SimForge.Testing](reference/testing.md): scenarios, run options, executor, results, and outcome classification
- [SimForge.Assertions](reference/assertions.md): `SimAssert`
- [SimForge.Xunit](reference/xunit.md): the xUnit v3 adapter
- [SimForge.PostgreSql](reference/postgresql.md): API, fault points, capability table, value rules, transaction model, and error codes ([JSON manifest](reference/postgresql.capabilities.json))
- [Capability manifests](reference/capabilities.md): capability statuses and compatibility levels

## Explanation: understanding the design

- [About simulated infrastructure](explanation/about-simulated-infrastructure.md)
- [About determinism and virtual time](explanation/about-determinism-and-virtual-time.md)
- [About faults and ambiguous outcomes](explanation/about-faults-and-ambiguous-outcomes.md)
- [About the PostgreSQL model](explanation/about-the-postgresql-model.md)
- [About runner independence](explanation/about-runner-independence.md)
