# How to back a repository with simulated storage

Use this when application code persists data through an interface it owns (a repository or unit of work), and you want
scenarios to run that code against a simulated PostgreSQL database.

The application project stays free of SimForge. Everything below goes in the test project.

## 1. Declare the tables the repository needs

```csharp
var database = setup.Environment.AddPostgreSqlDatabase("orders-db", schema => schema
    .Table("orders", table => table
        .Column("id", ColumnType.Uuid, primaryKey: true)
        .Column("total_cents", ColumnType.BigInt, notNull: true)));
```

## 2. Implement the application's interface over the database

Map domain objects to column dictionaries and rows back to domain objects. Translate storage errors into the exceptions
the application already expects:

```csharp
internal sealed class SimulatedOrderRepository(SimulatedPostgreSqlDatabase database) : IOrderRepository
{
    public Task AddAsync(Order order, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            database.Insert("orders", new Dictionary<string, object?>
            {
                ["id"] = order.Id,
                ["total_cents"] = order.TotalCents,
            });
        }
        catch (PostgreSqlStorageException exception) when (exception.ConstraintName == "orders_pkey")
        {
            throw new DuplicateOrderException(order.Id, exception);
        }
        catch (SimulatedServiceException exception)
        {
            throw new OrderStorageException($"Storing order {order.Id} failed ({exception.ErrorCode}).", exception);
        }

        return Task.CompletedTask;
    }

    public Task<Order?> FindAsync(Guid id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var row = database.Get("orders", id);
        return Task.FromResult(row is null ? null : new Order(row.Get<Guid>("id"), row.Get<long>("total_cents")));
    }
}
```

Catch `SimulatedServiceException` last. It also covers injected faults (`SimulatedFaultException`), which keeps fault
scenarios meaningful.

## 3. Map a unit of work to one transaction

When the application groups writes (for example an order and its outbox message), give each unit of work one
`PostgreSqlTransaction`. Write through the transaction, call `Commit()` from the unit of work's commit, and let
disposal roll back an uncommitted transaction. Only one transaction can be active per database, so dispose each unit
of work before starting the next.

`samples/Orders.Simulated` contains a complete unit-of-work adapter: `Support/SimulatedOrdersStorage.cs`.

## 4. Register the adapter and resolve the application service

```csharp
setup.AddService<IOrderRepository>(_ => new SimulatedOrderRepository(database));
setup.AddService(context => new OrderService(context.GetRequiredService<IOrderRepository>(), context.Clock));
```

In the scenario body, resolve the application service with `context.GetRequiredService<OrderService>()`. Assert on
stored state with `database.Get` and `database.Scan`, which read committed data.

See also: [SimForge.PostgreSql](../reference/postgresql.md),
[About the PostgreSQL model](../explanation/about-the-postgresql-model.md).
