namespace SimForge.PostgreSql.Tests;

/// <summary>An initialized environment with one simulated database, disposed with the test.</summary>
internal sealed class TestDatabase : IAsyncDisposable
{
    private TestDatabase(SimulationEnvironment environment, SimulatedPostgreSqlDatabase database)
    {
        Environment = environment;
        Database = database;
    }

    public SimulationEnvironment Environment { get; }

    public SimulatedPostgreSqlDatabase Database { get; }

    public static void OrdersSchema(DatabaseSchemaBuilder schema) => schema
        .Table("orders", table => table
            .Column("id", ColumnType.Uuid, primaryKey: true)
            .Column("customer", ColumnType.Text, notNull: true)
            .Column("email", ColumnType.Text, unique: true)
            .Column("total", ColumnType.Numeric, notNull: true)
            .Column("placed_at", ColumnType.TimestampTz)
            .Column("note", ColumnType.Text))
        .Table("outbox", table => table
            .Column("id", ColumnType.Uuid, primaryKey: true)
            .Column("order_id", ColumnType.Uuid, notNull: true)
            .Column("kind", ColumnType.Text, notNull: true)
            .Column("dispatched_at", ColumnType.TimestampTz));

    public static async Task<TestDatabase> CreateAsync(Action<DatabaseSchemaBuilder>? schema = null, SimulationEnvironmentOptions? options = null, CancellationToken cancellationToken = default)
    {
        var environment = new SimulationEnvironment(options ?? new SimulationEnvironmentOptions { ScenarioId = "postgresql-tests" });
        var database = environment.AddPostgreSqlDatabase("db", schema ?? OrdersSchema);
        await environment.InitializeAsync(cancellationToken);
        return new TestDatabase(environment, database);
    }

    public static Dictionary<string, object?> Order(Guid id, string customer = "c-1", string? email = null, decimal total = 10m) => new()
    {
        ["id"] = id,
        ["customer"] = customer,
        ["email"] = email,
        ["total"] = total,
    };

    public static Dictionary<string, object?> Outbox(Guid id, Guid orderId, string kind = "OrderPlaced") => new()
    {
        ["id"] = id,
        ["order_id"] = orderId,
        ["kind"] = kind,
    };

    public ValueTask DisposeAsync() => Environment.DisposeAsync();
}
