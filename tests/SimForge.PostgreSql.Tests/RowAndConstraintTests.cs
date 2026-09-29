using Xunit;
using static SimForge.PostgreSql.Tests.TestDatabase;

namespace SimForge.PostgreSql.Tests;

public sealed class RowAndConstraintTests
{
    private static readonly Guid First = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid Second = Guid.Parse("00000000-0000-0000-0000-000000000002");

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Insert_get_update_delete_by_key()
    {
        await using var test = await CreateAsync(cancellationToken: Token);
        var database = test.Database;

        database.Insert("orders", Order(First, customer: "alice", total: 12.5m));
        var inserted = database.Get("orders", First)!;
        Assert.Equal(("alice", 12.5m), (inserted.Get<string>("customer"), inserted.Get<decimal>("total")));
        Assert.True(inserted.IsNull("note"));
        Assert.Null(database.Get("orders", Second));

        Assert.True(database.Update("orders", First, new Dictionary<string, object?> { ["note"] = "gift", ["total"] = 15m }));
        var updated = database.Get("orders", First)!;
        Assert.Equal(("alice", 15m, "gift"), (updated.Get<string>("customer"), updated.Get<decimal>("total"), updated.Get<string>("note")));
        Assert.False(database.Update("orders", Second, new Dictionary<string, object?> { ["note"] = "x" }));

        Assert.True(database.Delete("orders", First));
        Assert.False(database.Delete("orders", First));
        Assert.Null(database.Get("orders", First));
    }

    [Fact]
    public async Task Duplicate_or_null_primary_keys_are_rejected_without_changing_state()
    {
        await using var test = await CreateAsync(cancellationToken: Token);
        test.Database.Insert("orders", Order(First, customer: "original"));
        var nullKey = Order(Second);
        nullKey["id"] = null;

        var duplicate = Assert.Throws<PostgreSqlStorageException>(() => test.Database.Insert("orders", Order(First, customer: "replacement")));
        var missingKey = Assert.Throws<PostgreSqlStorageException>(() => test.Database.Insert("orders", nullKey));

        Assert.Equal((PostgreSqlErrorCodes.UniqueViolation, "orders_pkey"), (duplicate.ErrorCode, duplicate.ConstraintName));
        Assert.Equal((PostgreSqlErrorCodes.NotNullViolation, "id"), (missingKey.ErrorCode, missingKey.Column));
        Assert.Equal("original", Assert.Single(test.Database.Scan("orders")).Get<string>("customer"));
    }

    [Fact]
    public async Task Not_null_columns_reject_null_on_insert_and_update()
    {
        await using var test = await CreateAsync(cancellationToken: Token);
        var omitted = Order(First);
        omitted.Remove("customer");
        test.Database.Insert("orders", Order(Second));

        Assert.Equal("customer", Assert.Throws<PostgreSqlStorageException>(() => test.Database.Insert("orders", omitted)).Column);
        Assert.Equal(
            PostgreSqlErrorCodes.NotNullViolation,
            Assert.Throws<PostgreSqlStorageException>(() => test.Database.Update("orders", Second, new Dictionary<string, object?> { ["total"] = null })).ErrorCode);
        Assert.Equal(10m, test.Database.Get("orders", Second)!.Get<decimal>("total"));
    }

    [Fact]
    public async Task Unique_columns_allow_many_nulls_but_no_duplicate_values()
    {
        await using var test = await CreateAsync(cancellationToken: Token);
        var database = test.Database;
        database.Insert("orders", Order(First, email: null));
        database.Insert("orders", Order(Second, email: null));
        var third = Guid.NewGuid();
        database.Insert("orders", Order(third, email: "a@example.test"));

        var duplicate = Assert.Throws<PostgreSqlStorageException>(() => database.Insert("orders", Order(Guid.NewGuid(), email: "a@example.test")));
        var duplicateByUpdate = Assert.Throws<PostgreSqlStorageException>(
            () => database.Update("orders", First, new Dictionary<string, object?> { ["email"] = "a@example.test" }));

        Assert.Equal("orders_email_key", duplicate.ConstraintName);
        Assert.DoesNotContain("a@example.test", duplicate.Message, StringComparison.Ordinal);
        Assert.Equal(PostgreSqlErrorCodes.UniqueViolation, duplicateByUpdate.ErrorCode);
        Assert.Equal(3, database.Scan("orders").Count);
    }

    [Fact]
    public async Task Unique_values_are_released_by_update_and_delete_and_a_row_may_keep_its_own_value()
    {
        await using var test = await CreateAsync(cancellationToken: Token);
        var database = test.Database;
        database.Insert("orders", Order(First, email: "first@example.test"));

        Assert.True(database.Update("orders", First, new Dictionary<string, object?> { ["email"] = "first@example.test", ["note"] = "same value" }));
        Assert.True(database.Update("orders", First, new Dictionary<string, object?> { ["email"] = "renamed@example.test" }));
        database.Insert("orders", Order(Second, email: "first@example.test"));
        Assert.True(database.Delete("orders", First));
        database.Insert("orders", Order(Guid.NewGuid(), email: "renamed@example.test"));

        Assert.Equal(2, database.Scan("orders").Count);
    }

    [Fact]
    public async Task Primary_key_updates_are_unsupported_and_change_nothing()
    {
        await using var test = await CreateAsync(cancellationToken: Token);
        test.Database.Insert("orders", Order(First));

        var exception = Assert.Throws<UnsupportedCapabilityException>(
            () => test.Database.Update("orders", First, new Dictionary<string, object?> { ["id"] = Second, ["note"] = "moved" }));

        Assert.Equal(PostgreSqlCapabilityIds.PrimaryKeyUpdate, exception.CapabilityId);
        Assert.True(test.Database.Get("orders", First)!.IsNull("note"));
        Assert.Contains(test.Environment.Journal.GetEntries(), entry => entry.Operation == "update" && entry.Outcome == OperationOutcome.Rejected);
    }

    [Fact]
    public async Task Updates_must_change_at_least_one_column()
    {
        await using var test = await CreateAsync(cancellationToken: Token);

        Assert.Throws<ArgumentException>(() => test.Database.Update("orders", First, new Dictionary<string, object?>()));
    }

    [Fact]
    public async Task Mutating_inputs_or_returned_values_never_changes_stored_state()
    {
        await using var test = await CreateAsync(schema => schema.Table("files", table => table
            .Column("id", ColumnType.BigInt, primaryKey: true)
            .Column("content", ColumnType.Bytea)
            .Column("name", ColumnType.Text)), cancellationToken: Token);
        var bytes = new byte[] { 1, 2, 3 };
        var values = new Dictionary<string, object?> { ["id"] = 1L, ["content"] = bytes, ["name"] = "a.txt" };

        test.Database.Insert("files", values);
        bytes[0] = 99;
        values["name"] = "changed.txt";
        var row = test.Database.Get("files", 1L)!;
        row.Get<byte[]>("content")[1] = 99;
        ((byte[])row["content"]!)[2] = 99;
        ((byte[])row.Single(pair => pair.Key == "content").Value!)[0] = 42;

        var reread = test.Database.Get("files", 1L)!;
        Assert.Equal([1, 2, 3], reread.Get<byte[]>("content"));
        Assert.Equal("a.txt", reread.Get<string>("name"));
    }

    [Fact]
    public async Task Rows_can_be_reinserted_as_values_into_another_database()
    {
        await using var source = await CreateAsync(cancellationToken: Token);
        await using var target = await CreateAsync(cancellationToken: Token);
        source.Database.Insert("orders", Order(First, customer: "copied"));

        target.Database.Insert("orders", source.Database.Get("orders", First)!);

        Assert.Equal("copied", target.Database.Get("orders", First)!.Get<string>("customer"));
    }

    [Fact]
    public async Task Parallel_environments_with_the_same_database_name_are_isolated()
    {
        async Task<IReadOnlyList<Row>> RunAsync(string customer)
        {
            await using var test = await CreateAsync(cancellationToken: Token);
            for (var index = 0; index < 100; index++)
            {
                test.Database.Insert("orders", Order(test.Environment.Ids.NewGuid(), customer: customer));
                await Task.Yield();
            }

            return test.Database.Scan("orders");
        }

        var results = await Task.WhenAll(Task.Run(() => RunAsync("left"), Token), Task.Run(() => RunAsync("right"), Token));

        Assert.All(results[0], row => Assert.Equal("left", row.Get<string>("customer")));
        Assert.All(results[1], row => Assert.Equal("right", row.Get<string>("customer")));
        Assert.Equal(100, results[0].Count);
        Assert.Equal(results[0].Select(row => row.Get<Guid>("id")), results[1].Select(row => row.Get<Guid>("id")));
    }

    [Fact]
    public async Task Database_is_unusable_before_initialization_and_after_disposal()
    {
        var environment = new SimulationEnvironment();
        var database = environment.AddPostgreSqlDatabase("db", OrdersSchema);

        Assert.Throws<InvalidOperationException>(() => database.Scan("orders"));
        await environment.InitializeAsync(Token);
        var transaction = database.BeginTransaction();
        transaction.Insert("orders", Order(First));
        await environment.DisposeAsync();

        Assert.Equal(PostgreSqlTransactionState.RolledBack, transaction.State);
        Assert.Throws<ObjectDisposedException>(() => database.Scan("orders"));
        Assert.Throws<ObjectDisposedException>(() => database.BeginTransaction());
        Assert.Throws<ObjectDisposedException>(() => transaction.Commit());
        transaction.Dispose();
    }

    [Fact]
    public async Task Journal_records_operations_without_payloads_unless_capture_is_enabled()
    {
        await using var quiet = await CreateAsync(cancellationToken: Token);
        await using var capturing = await CreateAsync(options: new SimulationEnvironmentOptions { CapturePayloads = true }, cancellationToken: Token);

        using (quiet.Environment.Journal.BeginCorrelation("order-1"))
        {
            quiet.Database.Insert("orders", Order(First, customer: "secret-customer"));
        }

        capturing.Database.Insert("orders", Order(First, customer: "visible-customer"));

        var quietEntry = quiet.Environment.Journal.GetEntries().Single(entry => entry.Operation == "insert");
        var capturingEntry = capturing.Environment.Journal.GetEntries().Single(entry => entry.Operation == "insert");
        Assert.Equal(("postgresql", "db", "orders", FaultPhases.After, "order-1"), (quietEntry.Provider, quietEntry.Resource, quietEntry.Target, quietEntry.Phase, quietEntry.CorrelationId));
        Assert.Null(quietEntry.Payload);
        Assert.DoesNotContain("secret-customer", quiet.Environment.CaptureDiagnostics().ToJson(), StringComparison.Ordinal);
        Assert.Contains("customer='visible-customer'", capturingEntry.Payload, StringComparison.Ordinal);
    }
}
