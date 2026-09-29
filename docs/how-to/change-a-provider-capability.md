# How to change a provider's documented capabilities

For contributors. Use this when you add, remove, or change behavior of a provider, so that its capability manifest,
capability table, and JSON file stay in agreement.

1. **Change the manifest in code.** For PostgreSQL, edit `PostgreSqlCapabilities.Manifest` in
   `src/SimForge.PostgreSql/PostgreSqlCapabilities.cs`; for RabbitMQ, `RabbitMqCapabilities.Manifest` in
   `src/SimForge.RabbitMq/RabbitMqCapabilities.cs`. For a new capability, add its ID constant to the provider's
   `...CapabilityIds` class; a test fails when a constant is missing from the manifest.
2. **Throw the right ID.** Code that rejects an unsupported request throws
   `UnsupportedCapabilityException(PostgreSqlCapabilityIds.<Name>, ...)` before changing any state.
3. **Update the capability table** in `docs/reference/<provider>.md`. Every capability needs a row that starts with
   ``| `<id>` | <Status> |``.
4. **Regenerate the JSON file:**

   ```bash
   SIMFORGE_UPDATE_CAPABILITY_DOCS=1 dotnet test --project tests/SimForge.PostgreSql.Tests -c Release
   ```

   Use the provider's own test project, for example `tests/SimForge.RabbitMq.Tests`. Review the diff of
   `docs/reference/<provider>.capabilities.json`.
5. **Run the tests again without the variable.** They fail if the JSON, the table, or the ID constants disagree with
   the manifest:

   ```bash
   dotnet test --project tests/SimForge.PostgreSql.Tests -c Release
   ```

Do not set `VerifiedSubset` without a pinned `ReferenceVersion` and passing `VerificationCases`; the manifest
constructor rejects it.

See also: [Capability manifests](../reference/capabilities.md).
