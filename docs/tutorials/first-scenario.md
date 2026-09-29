# Tutorial: test an order workflow with a simulated database

In this tutorial we write our first SimForge tests. We will store an order in a simulated PostgreSQL-style database,
read a failure report, move virtual time forward, and make a commit fail on purpose. At the end we will have two
passing xUnit tests that run in well under a second, with no database server and no Docker.

It takes about 20 minutes.

## Before we start

We need:

- the .NET SDK version pinned in the repository's `global.json` (10.0.4xx)
- a clone of the SimForge repository

SimForge is not published as a package yet, so we build our tests inside the clone. Every command below runs from the
root of the repository.

## Step 1: create a test project

Create a folder for our project:

```bash
mkdir -p tutorial/FirstScenario
```

Create `tutorial/FirstScenario/FirstScenario.csproj` with this content:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="xunit.v3" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="../../src/SimForge.PostgreSql/SimForge.PostgreSql.csproj" />
    <ProjectReference Include="../../src/SimForge.Assertions/SimForge.Assertions.csproj" />
    <ProjectReference Include="../../src/SimForge.Xunit/SimForge.Xunit.csproj" />
  </ItemGroup>
</Project>
```

Build it:

```bash
dotnet build tutorial/FirstScenario
```

The build ends with:

```text
    0 Warning(s)
    0 Error(s)
```

We have an empty xUnit test project that can use SimForge.

## Step 2: store an order

Create `tutorial/FirstScenario/OrderScenarios.cs`:

```csharp
using SimForge;
using SimForge.Assertions;
using SimForge.PostgreSql;
using SimForge.Testing;

namespace FirstScenario;

public static class OrderScenarios
{
    public static Scenario PlaceOrder { get; } = Scenario.Create(
        "tutorial.place-order",
        setup => AddShop(setup),
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

    private static SimulatedPostgreSqlDatabase AddShop(ScenarioSetup setup) =>
        setup.Environment.AddPostgreSqlDatabase("shop", schema => schema
            .Table("orders", table => table
                .Column("id", ColumnType.BigInt, primaryKey: true)
                .Column("customer", ColumnType.Text, notNull: true)
                .Column("total_cents", ColumnType.BigInt, notNull: true)));
}
```

The first part of the scenario, `AddShop`, gives it a database named `shop` with an `orders` table. The second part
stores an order in a transaction and reads it back.

Now create `tutorial/FirstScenario/OrderTests.cs`, which lets xUnit run the scenario:

```csharp
using SimForge.Xunit;
using Xunit;

namespace FirstScenario;

public sealed class OrderTests
{
    [Fact]
    public Task Place_order() => XunitScenario.RunAsync(OrderScenarios.PlaceOrder);
}
```

Run the tests:

```bash
dotnet test --project tutorial/FirstScenario
```

We see:

```text
Test run summary: Passed!
  total: 1
  failed: 0
  succeeded: 1
```

Our order was stored and read back.

## Step 3: read a failure report

Let's see what a failure looks like. In `OrderScenarios.cs`, change the expected customer from `"ada"` to `"grace"`:

```csharp
SimAssert.Equal("grace", order.Get<string>("customer"));
```

Run the tests again:

```bash
dotnet test --project tutorial/FirstScenario
```

This time the test fails, and the output contains a report like this (your timing and path will differ):

```text
Scenario tutorial.place-order: Failed (Assertion) after 18.3 ms; seed 2031535255
Defined at .../tutorial/FirstScenario/OrderScenarios.cs:10
Primary error: SimForge.SimForgeAssertionException: SimAssert.Equal failed for `order.Get<string>("customer")`.
Expected: "grace"
Actual:   "ada"
Scenario 'tutorial.place-order' seed 2031535255, virtual time 2026-01-01T00:00:00.0000000+00:00, environment Disposed
Journal (6 of 6 entries):
  #1 2026-01-01T00:00:00.0000000+00:00 postgresql/shop initialize:after Succeeded
  #2 2026-01-01T00:00:00.0000000+00:00 postgresql/shop begin:after Succeeded details=tx=1
  #3 2026-01-01T00:00:00.0000000+00:00 postgresql/shop insert:after Succeeded target=orders details=tx=1; rows=1
  #4 2026-01-01T00:00:00.0000000+00:00 postgresql/shop commit:after Succeeded target=orders details=tx=1; changes=1
  #5 2026-01-01T00:00:00.0000000+00:00 postgresql/shop get:after Succeeded target=orders details=autocommit; rows=1
  #6 2026-01-01T00:00:00.0000000+00:00 postgresql/shop dispose:after Succeeded
```

Notice the journal: it lists every database operation the scenario performed, in order. Change `"grace"` back to
`"ada"` and run the tests once more to see them pass again.

## Step 4: move time forward

Orders usually record when they were placed. Our database uses a virtual clock, so we can decide exactly what time it
is.

First add a `placed_at` column to the table in `AddShop`:

```csharp
                .Column("total_cents", ColumnType.BigInt, notNull: true)
                .Column("placed_at", ColumnType.TimestampTz)));
```

Then replace the `PlaceOrder` scenario with this version. It advances virtual time by five minutes before placing the
order:

```csharp
    public static Scenario PlaceOrder { get; } = Scenario.Create(
        "tutorial.place-order",
        setup => AddShop(setup),
        async (context, cancellationToken) =>
        {
            var shop = context.GetResource<SimulatedPostgreSqlDatabase>("shop");
            await context.Scheduler.AdvanceByAsync(TimeSpan.FromMinutes(5), cancellationToken);

            using (var transaction = shop.BeginTransaction())
            {
                transaction.Insert("orders", new Dictionary<string, object?>
                {
                    ["id"] = 1L,
                    ["customer"] = "ada",
                    ["total_cents"] = 4_250L,
                    ["placed_at"] = context.Clock.GetUtcNow(),
                });
                transaction.Commit();
            }

            var order = SimAssert.NotNull(shop.Get("orders", 1L));
            SimAssert.Equal("ada", order.Get<string>("customer"));
            SimAssert.Equal(new DateTimeOffset(2026, 1, 1, 0, 5, 0, TimeSpan.Zero), order.Get<DateTimeOffset>("placed_at"));
        });
```

Run the tests:

```bash
dotnet test --project tutorial/FirstScenario
```

The test passes. Virtual time starts at midnight on 1 January 2026, so the order was placed at exactly 00:05, and the
test did not wait five real minutes.

## Step 5: make a commit fail

Finally, let's check what happens when the database fails. Add a second scenario to `OrderScenarios`, just above
`AddShop`:

```csharp
    public static Scenario CommitFails { get; } = Scenario.Create(
        "tutorial.commit-fails",
        setup => AddShop(setup).InjectFault(PostgreSqlOperations.Commit, reason: "database restarted"),
        (context, cancellationToken) =>
        {
            var shop = context.GetResource<SimulatedPostgreSqlDatabase>("shop");

            using var transaction = shop.BeginTransaction();
            transaction.Insert("orders", new Dictionary<string, object?>
            {
                ["id"] = 2L,
                ["customer"] = "grace",
                ["total_cents"] = 990L,
            });

            var failure = SimAssert.Throws<SimulatedFaultException>(transaction.Commit);
            SimAssert.False(failure.StateChanged);
            SimAssert.Null(shop.Get("orders", 2L));
            return ValueTask.CompletedTask;
        });
```

`InjectFault` tells the database to fail its next commit. The scenario checks three things: the commit throws, nothing
changed (`StateChanged` is false), and the order was not stored.

Add a test for it in `OrderTests`:

```csharp
    [Fact]
    public Task Commit_failure_stores_nothing() => XunitScenario.RunAsync(OrderScenarios.CommitFails);
```

Run the tests:

```bash
dotnet test --project tutorial/FirstScenario
```

```text
Test run summary: Passed!
  total: 2
  failed: 0
  succeeded: 2
```

## What we built

We wrote two scenarios against a simulated database. The first stores an order at a chosen virtual time. The second
proves that a failed commit stores nothing. We also saw that every run leaves a journal of what it did.

When you are done, delete the `tutorial` folder. It is not part of the repository.

## Next steps

- [How to simulate database failures](../how-to/simulate-database-failures.md), including a commit that succeeds but
  whose response is lost
- [How to back a repository with simulated storage](../how-to/back-a-repository-with-simulated-storage.md)
- [About simulated infrastructure](../explanation/about-simulated-infrastructure.md)
