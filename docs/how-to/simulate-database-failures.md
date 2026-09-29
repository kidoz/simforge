# How to simulate database failures

Use this to test how a workflow behaves when a simulated PostgreSQL database fails at a chosen point.

Register faults on the database, either in the scenario setup or in the body before the operation you want to fail.
Each rule fires once.

## Make the next commit fail without applying anything

```csharp
var fault = shop.InjectFault(PostgreSqlOperations.Commit);
```

`Commit()` throws `SimulatedFaultException`. Nothing is stored, and the transaction ends rolled back.

## Make a later call fail

```csharp
shop.InjectFault(PostgreSqlOperations.Insert, occurrence: 2); // the second insert fails
```

Occurrences are counted per operation *and* phase, from the moment the rule is added. A commit that fails at `before`
is never evaluated at `after`, so it does not count towards an `after` rule.

## Apply the change but lose the success response

```csharp
shop.InjectFault(PostgreSqlOperations.Commit, FaultPhases.After, reason: "acknowledgement lost");
```

The changes become visible, but `Commit()` throws a `SimulatedFaultException` whose `StateChanged` is true. Code that
handles this case can check the flag:

```csharp
catch (SimulatedFaultException exception) when (exception.StateChanged)
{
    // The commit was applied; retry idempotently instead of assuming nothing was written.
}
```

## Check that the fault fired as intended

```csharp
SimAssert.True(fault.HasFired);
SimAssert.False(fault.Report!.StateChanged);
```

Rules that never fired are listed under "Unfired faults" in the failure report.

## Target a fault point directly

`InjectFault` is shorthand for a `FaultRule`. Use the rule form when you need every field:

```csharp
context.Faults.Add(new FaultRule
{
    Provider = SimulatedPostgreSqlDatabase.ProviderName,
    Resource = "shop",
    Operation = PostgreSqlOperations.Delete,
});
```

`Add` rejects a rule for a point that no registered resource declares. The available points are listed under
[PostgreSQL fault points](../reference/postgresql.md#fault-points).

See also: [About faults and ambiguous outcomes](../explanation/about-faults-and-ambiguous-outcomes.md).
