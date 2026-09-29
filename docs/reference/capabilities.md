# Capability manifests

Namespace `SimForge` (package `SimForge.Core`). Every provider publishes its capabilities in two forms:

- a machine-readable `CapabilityManifest`, rendered to `docs/reference/<provider>.capabilities.json`, and
- a human-readable capability table in `docs/reference/<provider>.md`.

The provider's test suite fails when the JSON file differs from the manifest in code, or when the table omits a
capability.

## CapabilityStatus

| Value | Meaning | Required evidence |
|---|---|---|
| `SimulatedOnly` | Implemented and covered by SimForge's own behavioral tests. Not compared with the real service. | Passing simulation tests. |
| `VerifiedSubset` | Implemented, with passing contract cases against a pinned reference service version. | `ReferenceVersion` and `VerificationCases`. The `CapabilityManifest` constructor rejects this status without both. |
| `Unsupported` | Not implemented. Requests throw `UnsupportedCapabilityException`, carrying the capability ID, before any state changes. | Tests showing the rejection happens before mutation. |

## CompatibilityLevel

| Value | Meaning |
|---|---|
| `ApplicationContract` | Replaces an application-owned repository, cache, producer, or consumer interface. |
| `ServiceSemantics` | Implements a declared subset of observable database or broker behavior. |
| `ClientApi` | Supports a named API surface of a production client library. |
| `WireProtocol` | Accepts requests from an unchanged production driver. |
| `RealEngine` | Executes the actual service engine. |

No provider implements `ClientApi`, `WireProtocol`, or `RealEngine`.

## Capability

| Property | Required | Description |
|---|---|---|
| `Id` | yes | Stable identifier prefixed with the provider, for example `postgresql.constraints.unique`. |
| `Status` | yes | A `CapabilityStatus`. |
| `Level` | yes | A `CompatibilityLevel`. |
| `Summary` | yes | One-line description. |
| `Operations` | no | Supported operations or fault points. |
| `DataTypes` | no | Supported types and their representations. |
| `Boundaries` | no | Limits and exact behavior at the edges. |
| `Deviations` | no | Known differences from the real service. |
| `Assumptions` | no | Ordering and concurrency assumptions. |
| `ReferenceVersion` | for `VerifiedSubset` | Reference service and client versions. |
| `VerificationCases` | for `VerifiedSubset` | Contract cases proving the claim. |

## CapabilityManifest

| Member | Description |
|---|---|
| `CapabilityManifest(provider, title, capabilities)` | Rejects duplicate IDs, IDs without the `<provider>.` prefix, and `VerifiedSubset` entries without evidence. |
| `Provider`, `Title`, `Capabilities` | Manifest content in declaration order. |
| `Get(id)` | Returns a capability, or throws `KeyNotFoundException`. |
| `ToJson()` | Indented JSON with LF line endings and a trailing newline. List properties are always present; `referenceVersion` is `null` when unset. |

## Published manifests

| Provider | Package | Table | Manifest |
|---|---|---|---|
| `postgresql` | `SimForge.PostgreSql` | [postgresql.md](postgresql.md#capability-table) | [postgresql.capabilities.json](postgresql.capabilities.json) |
| `rabbitmq` | `SimForge.RabbitMq` | [rabbitmq.md](rabbitmq.md#capability-table) | [rabbitmq.capabilities.json](rabbitmq.capabilities.json) |
| `kafka` | `SimForge.Kafka` | [kafka.md](kafka.md#capability-table) | [kafka.capabilities.json](kafka.capabilities.json) |
