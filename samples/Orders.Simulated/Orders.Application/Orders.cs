using System.Text.Json;

namespace Orders.Application;

public enum OrderStatus
{
    Placed,
}

public sealed record Order(Guid Id, string CustomerReference, long TotalCents, OrderStatus Status, DateTimeOffset PlacedAt);

/// <summary>A message recorded in the same transaction as the business change and published later by a dispatcher.</summary>
public sealed record OutboxMessage(Guid Id, string Type, string Payload, DateTimeOffset CreatedAt, DateTimeOffset? DispatchedAt);

/// <summary>Integration event announcing a placed order.</summary>
public sealed record OrderPlaced(Guid OrderId, string CustomerReference, long TotalCents, DateTimeOffset PlacedAt)
{
    public const string MessageType = "orders.order-placed.v1";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static OrderPlaced FromJson(string json) =>
        JsonSerializer.Deserialize<OrderPlaced>(json, JsonOptions) ?? throw new FormatException("The OrderPlaced payload is empty.");
}

public sealed record PlaceOrder(Guid OrderId, string CustomerReference, long TotalCents);

/// <summary>Application-owned persistence boundary: one unit of work maps to one storage transaction.</summary>
public interface IOrdersUnitOfWork : IAsyncDisposable
{
    Task<Order?> FindOrderAsync(Guid orderId, CancellationToken cancellationToken);

    /// <summary>Adds a new order. Throws <see cref="DuplicateOrderException"/> when the ID is already taken.</summary>
    Task AddOrderAsync(Order order, CancellationToken cancellationToken);

    Task AddOutboxMessageAsync(OutboxMessage message, CancellationToken cancellationToken);

    Task CommitAsync(CancellationToken cancellationToken);
}

public interface IOrdersUnitOfWorkFactory
{
    Task<IOrdersUnitOfWork> BeginAsync(CancellationToken cancellationToken);
}

public interface IIdGenerator
{
    Guid NewId();
}

/// <summary>Production ID generator: time-ordered version 7 GUIDs.</summary>
public sealed class TimeOrderedIdGenerator(TimeProvider timeProvider) : IIdGenerator
{
    public Guid NewId() => Guid.CreateVersion7(timeProvider.GetUtcNow());
}

/// <summary>An order with this ID already exists with different content.</summary>
public sealed class DuplicateOrderException(Guid orderId, Exception? innerException = null)
    : Exception($"Order {orderId} already exists.", innerException)
{
    public Guid OrderId { get; } = orderId;
}

/// <summary>
/// Storage failed. Whether the change was applied is unknown to the caller; retrying <see cref="OrderPlacementService.PlaceOrderAsync"/>
/// is safe because placement is idempotent per order ID.
/// </summary>
public sealed class OrderPersistenceException(string message, Exception innerException) : Exception(message, innerException);

/// <summary>Places orders using the transactional outbox pattern.</summary>
public sealed class OrderPlacementService(IOrdersUnitOfWorkFactory unitOfWorkFactory, IIdGenerator idGenerator, TimeProvider timeProvider)
{
    /// <summary>
    /// Stores the order and its <see cref="OrderPlaced"/> outbox message in one transaction. Retrying an identical
    /// command returns the stored order without a second outbox message; a conflicting command with the same order ID
    /// throws <see cref="DuplicateOrderException"/>.
    /// </summary>
    public async Task<Order> PlaceOrderAsync(PlaceOrder command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.CustomerReference);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(command.TotalCents, 0);

        var now = timeProvider.GetUtcNow();
        var order = new Order(command.OrderId, command.CustomerReference, command.TotalCents, OrderStatus.Placed, now);
        var message = new OutboxMessage(
            idGenerator.NewId(),
            OrderPlaced.MessageType,
            new OrderPlaced(order.Id, order.CustomerReference, order.TotalCents, order.PlacedAt).ToJson(),
            now,
            DispatchedAt: null);

        try
        {
            await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken);
            await unitOfWork.AddOrderAsync(order, cancellationToken);
            await unitOfWork.AddOutboxMessageAsync(message, cancellationToken);
            await unitOfWork.CommitAsync(cancellationToken);
            return order;
        }
        catch (DuplicateOrderException)
        {
            var existing = await FindAsync(command.OrderId, cancellationToken);
            if (existing is not null && existing.CustomerReference == command.CustomerReference && existing.TotalCents == command.TotalCents)
            {
                return existing;
            }

            throw;
        }
    }

    public async Task<Order?> FindAsync(Guid orderId, CancellationToken cancellationToken)
    {
        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken);
        return await unitOfWork.FindOrderAsync(orderId, cancellationToken);
    }
}
