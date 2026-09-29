using Orders.Application;
using SimForge;
using SimForge.PostgreSql;

namespace Orders.Simulated.Tests.Support;

/// <summary>Test-side schema and adapters that implement the application's persistence interfaces on SimForge.PostgreSql.</summary>
internal static class OrdersStorage
{
    public const string DatabaseName = "orders-db";
    public const string OrdersTable = "orders";
    public const string OutboxTable = "outbox_messages";

    public static SimulatedPostgreSqlDatabase AddOrdersDatabase(this SimulationEnvironment environment) =>
        environment.AddPostgreSqlDatabase(DatabaseName, schema => schema
            .Table(OrdersTable, table => table
                .Column("id", ColumnType.Uuid, primaryKey: true)
                .Column("customer_ref", ColumnType.Text, notNull: true)
                .Column("total_cents", ColumnType.BigInt, notNull: true)
                .Column("status", ColumnType.Text, notNull: true)
                .Column("placed_at", ColumnType.TimestampTz, notNull: true))
            .Table(OutboxTable, table => table
                .Column("id", ColumnType.Uuid, primaryKey: true)
                .Column("type", ColumnType.Text, notNull: true)
                .Column("payload", ColumnType.Text, notNull: true)
                .Column("created_at", ColumnType.TimestampTz, notNull: true)
                .Column("dispatched_at", ColumnType.TimestampTz)));

    /// <summary>Reads committed orders, as another session would see them.</summary>
    public static Order? FindCommittedOrder(this SimulatedPostgreSqlDatabase database, Guid orderId) =>
        database.Get(OrdersTable, orderId) is { } row ? ToOrder(row) : null;

    /// <summary>Committed outbox messages not yet dispatched, in creation order (message ID breaks ties).</summary>
    public static IReadOnlyList<OutboxMessage> PendingOutbox(this SimulatedPostgreSqlDatabase database) =>
        [.. database.Scan(OutboxTable).Select(ToOutboxMessage).Where(message => message.DispatchedAt is null).OrderBy(message => message.CreatedAt).ThenBy(message => message.Id)];

    internal static Order ToOrder(Row row) => new(
        row.Get<Guid>("id"),
        row.Get<string>("customer_ref"),
        row.Get<long>("total_cents"),
        Enum.Parse<OrderStatus>(row.Get<string>("status")),
        row.Get<DateTimeOffset>("placed_at"));

    internal static OutboxMessage ToOutboxMessage(Row row) => new(
        row.Get<Guid>("id"),
        row.Get<string>("type"),
        row.Get<string>("payload"),
        row.Get<DateTimeOffset>("created_at"),
        row.Get<DateTimeOffset?>("dispatched_at"));
}

internal sealed class SimulatedOrdersUnitOfWorkFactory(SimulatedPostgreSqlDatabase database) : IOrdersUnitOfWorkFactory
{
    public Task<IOrdersUnitOfWork> BeginAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var transaction = SimulatedOrdersUnitOfWork.Translate(database.BeginTransaction);
        return Task.FromResult<IOrdersUnitOfWork>(new SimulatedOrdersUnitOfWork(transaction));
    }
}

/// <summary>Maps the application's unit of work onto one simulated transaction, translating storage errors into application exceptions.</summary>
internal sealed class SimulatedOrdersUnitOfWork(PostgreSqlTransaction transaction) : IOrdersUnitOfWork
{
    public Task<Order?> FindOrderAsync(Guid orderId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Translate(() => transaction.Get(OrdersStorage.OrdersTable, orderId) is { } row ? OrdersStorage.ToOrder(row) : null));
    }

    public Task AddOrderAsync(Order order, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            transaction.Insert(OrdersStorage.OrdersTable, new Dictionary<string, object?>
            {
                ["id"] = order.Id,
                ["customer_ref"] = order.CustomerReference,
                ["total_cents"] = order.TotalCents,
                ["status"] = order.Status.ToString(),
                ["placed_at"] = order.PlacedAt,
            });
        }
        catch (PostgreSqlStorageException exception) when (exception.ConstraintName == $"{OrdersStorage.OrdersTable}_pkey")
        {
            throw new DuplicateOrderException(order.Id, exception);
        }
        catch (SimulatedServiceException exception)
        {
            throw Persistence("Adding the order failed.", exception);
        }

        return Task.CompletedTask;
    }

    public Task AddOutboxMessageAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Translate(() => transaction.Insert(OrdersStorage.OutboxTable, new Dictionary<string, object?>
        {
            ["id"] = message.Id,
            ["type"] = message.Type,
            ["payload"] = message.Payload,
            ["created_at"] = message.CreatedAt,
            ["dispatched_at"] = message.DispatchedAt,
        }));
        return Task.CompletedTask;
    }

    public Task CommitAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Translate(transaction.Commit);
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => transaction.DisposeAsync();

    internal static T Translate<T>(Func<T> operation)
    {
        try
        {
            return operation();
        }
        catch (SimulatedServiceException exception)
        {
            throw Persistence("Order storage failed.", exception);
        }
    }

    private static void Translate(Action operation) => Translate(() =>
    {
        operation();
        return true;
    });

    private static OrderPersistenceException Persistence(string message, SimulatedServiceException exception) =>
        new($"{message} ({exception.ErrorCode})", exception);
}

internal sealed class SimulatedIdGenerator(DeterministicIds ids) : IIdGenerator
{
    public Guid NewId() => ids.NewGuid();
}
