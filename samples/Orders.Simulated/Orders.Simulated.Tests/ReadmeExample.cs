using SimForge;
using SimForge.Assertions;
using SimForge.PostgreSql;
using SimForge.Testing;
using SimForge.Xunit;
using Xunit;

namespace Orders.Simulated.Tests;

// Mirrors the code blocks in the repository README; compiling and running them here keeps the README truthful.
public static class OrderScenarios
{
    public static Scenario PlaceOrder { get; } = Scenario.Create(
        "orders.place",
        setup => setup.Environment.AddPostgreSqlDatabase("shop", schema => schema
            .Table("orders", table => table
                .Column("id", ColumnType.BigInt, primaryKey: true)
                .Column("customer", ColumnType.Text, notNull: true)
                .Column("total_cents", ColumnType.BigInt, notNull: true))),
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
}

public sealed class OrderTests
{
    [Fact]
    public Task Place_order() => XunitScenario.RunAsync(OrderScenarios.PlaceOrder);
}

public sealed class ReminderService(TimeProvider timeProvider)
{
    public async Task<DateTimeOffset> RemindAfterAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        await Task.Delay(delay, timeProvider, cancellationToken);
        return timeProvider.GetUtcNow();
    }
}

public static class ReminderScenarios
{
    public static Scenario RemindAfterOneHour { get; } = Scenario.Create(
        "reminders.after-one-hour",
        setup => setup.AddService(context => new ReminderService(context.Clock)),
        async (context, cancellationToken) =>
        {
            var reminder = context.GetRequiredService<ReminderService>().RemindAfterAsync(TimeSpan.FromHours(1), cancellationToken);

            await context.Scheduler.AdvanceByAsync(TimeSpan.FromHours(1), cancellationToken); // no real waiting

            SimAssert.Equal(new DateTimeOffset(2026, 1, 1, 1, 0, 0, TimeSpan.Zero), await reminder);
        });
}

public static class FaultScenarios
{
    public static Scenario LostCommitResponse { get; } = Scenario.Create(
        "orders.lost-commit-response",
        setup => setup.Environment
            .AddPostgreSqlDatabase("shop", schema => schema
                .Table("orders", table => table.Column("id", ColumnType.BigInt, primaryKey: true)))
            .InjectFault(PostgreSqlOperations.Commit, FaultPhases.After),
        (context, cancellationToken) =>
        {
            var shop = context.GetResource<SimulatedPostgreSqlDatabase>("shop");
            using var transaction = shop.BeginTransaction();
            transaction.Insert("orders", new Dictionary<string, object?> { ["id"] = 1L });

            var fault = SimAssert.Throws<SimulatedFaultException>(transaction.Commit);

            SimAssert.True(fault.StateChanged);        // the commit was applied...
            SimAssert.NotNull(shop.Get("orders", 1L)); // ...and is visible, although the caller saw an error
            return ValueTask.CompletedTask;
        });
}

public sealed class ReadmeExampleTests
{
    [Fact]
    public Task Remind_after_one_hour() => XunitScenario.RunAsync(ReminderScenarios.RemindAfterOneHour);

    [Fact]
    public Task Lost_commit_response() => XunitScenario.RunAsync(FaultScenarios.LostCommitResponse);
}
