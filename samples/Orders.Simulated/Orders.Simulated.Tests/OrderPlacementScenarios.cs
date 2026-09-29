using Orders.Application;
using Orders.Simulated.Tests.Support;
using SimForge;
using SimForge.Assertions;
using SimForge.PostgreSql;
using SimForge.Testing;

namespace Orders.Simulated.Tests;

/// <summary>
/// Runner-neutral Orders scenarios for the storage half of the outbox workflow. They use only SimForge contracts and
/// assertions, so they can run unchanged under a future SimForge runner. The broker half arrives with milestone M3.
/// </summary>
public static class OrderPlacementScenarios
{
    private static readonly Guid OrderId = Guid.Parse("7c9e6679-7425-40de-944b-e07fc1f90ae7");
    private static readonly PlaceOrder Command = new(OrderId, "customer-17", 4_250);

    public static Scenario OrderAndOutboxCommitTogether { get; } = Scenario.Create(
        "orders.place.order-and-outbox-commit-together",
        Configure,
        async (context, cancellationToken) =>
        {
            var service = context.GetRequiredService<OrderPlacementService>();
            var database = Database(context);
            await context.Scheduler.AdvanceByAsync(TimeSpan.FromMinutes(5), cancellationToken);

            using (context.Journal.BeginCorrelation($"order:{OrderId}"))
            {
                await service.PlaceOrderAsync(Command, cancellationToken);
            }

            var order = SimAssert.NotNull(database.FindCommittedOrder(OrderId));
            SimAssert.Equal(context.Clock.GetUtcNow(), order.PlacedAt);
            var message = SimAssert.Single(database.PendingOutbox());
            SimAssert.Equal(OrderPlaced.MessageType, message.Type);
            SimAssert.Equal(new OrderPlaced(OrderId, "customer-17", 4_250, order.PlacedAt), OrderPlaced.FromJson(message.Payload));

            var commit = SimAssert.Single(context.Journal.GetEntries(), entry => entry.Operation == PostgreSqlOperations.Commit);
            SimAssert.Equal($"{OrdersStorage.OrdersTable},{OrdersStorage.OutboxTable}", commit.Target);
            SimAssert.Equal($"order:{OrderId}", commit.CorrelationId);
            SimAssert.Equal(OperationOutcome.Succeeded, commit.Outcome);
        });

    public static Scenario StorageFailureBeforeCommitLeavesNothing { get; } = Scenario.Create(
        "orders.place.storage-failure-before-commit-leaves-nothing",
        Configure,
        async (context, cancellationToken) =>
        {
            var database = Database(context);
            var fault = database.InjectFault(PostgreSqlOperations.Commit, FaultPhases.Before, reason: "connection lost before commit");

            var failure = await SimAssert.ThrowsAsync<OrderPersistenceException>(
                () => context.GetRequiredService<OrderPlacementService>().PlaceOrderAsync(Command, cancellationToken));

            SimAssert.False(SimAssert.NotNull(fault.Report).StateChanged);
            SimAssert.False(((SimulatedFaultException)failure.InnerException!).StateChanged);
            SimAssert.Null(database.FindCommittedOrder(OrderId));
            SimAssert.Empty(database.PendingOutbox());
        });

    public static Scenario FailureWritingOutboxRollsBackTheOrder { get; } = Scenario.Create(
        "orders.place.outbox-write-failure-rolls-back-order",
        Configure,
        async (context, cancellationToken) =>
        {
            var database = Database(context);
            // Occurrence 2: the order insert succeeds and the outbox insert fails.
            database.InjectFault(PostgreSqlOperations.Insert, occurrence: 2);

            await SimAssert.ThrowsAsync<OrderPersistenceException>(
                () => context.GetRequiredService<OrderPlacementService>().PlaceOrderAsync(Command, cancellationToken));

            SimAssert.Null(database.FindCommittedOrder(OrderId));
            SimAssert.Empty(database.PendingOutbox());
        });

    public static Scenario LostCommitResponseIsResolvedByIdempotentRetry { get; } = Scenario.Create(
        "orders.place.lost-commit-response-idempotent-retry",
        Configure,
        async (context, cancellationToken) =>
        {
            var database = Database(context);
            var service = context.GetRequiredService<OrderPlacementService>();
            database.InjectFault(PostgreSqlOperations.Commit, FaultPhases.After, reason: "commit acknowledgement lost");

            var ambiguous = await SimAssert.ThrowsAsync<OrderPersistenceException>(() => service.PlaceOrderAsync(Command, cancellationToken));
            SimAssert.True(((SimulatedFaultException)ambiguous.InnerException!).StateChanged, "the commit was applied before the response was lost");
            SimAssert.NotNull(database.FindCommittedOrder(OrderId));

            var retried = await service.PlaceOrderAsync(Command, cancellationToken);

            SimAssert.Equal(OrderId, retried.Id);
            SimAssert.Single(database.PendingOutbox());
            SimAssert.Single(database.Scan(OrdersStorage.OrdersTable));
        });

    public static Scenario ConflictingDuplicateIsRejected { get; } = Scenario.Create(
        "orders.place.conflicting-duplicate-rejected",
        Configure,
        async (context, cancellationToken) =>
        {
            var database = Database(context);
            var service = context.GetRequiredService<OrderPlacementService>();
            await service.PlaceOrderAsync(Command, cancellationToken);

            var duplicate = await SimAssert.ThrowsAsync<DuplicateOrderException>(
                () => service.PlaceOrderAsync(Command with { TotalCents = 1 }, cancellationToken));

            SimAssert.Equal(OrderId, duplicate.OrderId);
            SimAssert.Equal(4_250L, SimAssert.NotNull(database.FindCommittedOrder(OrderId)).TotalCents);
            SimAssert.Single(database.PendingOutbox());
        });

    /// <summary>Pauses until canceled; used to show that cancellation releases the environment.</summary>
    public static Scenario PlaceOrderThenWaitUntilCanceled { get; } = Scenario.Create(
        "orders.place.then-wait-until-canceled",
        Configure,
        async (context, cancellationToken) =>
        {
            await context.GetRequiredService<OrderPlacementService>().PlaceOrderAsync(Command, cancellationToken);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        });

    private static void Configure(ScenarioSetup setup)
    {
        var database = setup.Environment.AddOrdersDatabase();
        setup.AddService<IOrdersUnitOfWorkFactory>(_ => new SimulatedOrdersUnitOfWorkFactory(database));
        setup.AddService<IIdGenerator>(context => new SimulatedIdGenerator(context.Ids));
        setup.AddService(context => new OrderPlacementService(
            context.GetRequiredService<IOrdersUnitOfWorkFactory>(),
            context.GetRequiredService<IIdGenerator>(),
            context.Clock));
    }

    private static SimulatedPostgreSqlDatabase Database(ScenarioContext context) =>
        context.GetResource<SimulatedPostgreSqlDatabase>(OrdersStorage.DatabaseName);
}
