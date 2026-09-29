using Xunit;
using static SimForge.PostgreSql.Tests.TestDatabase;

namespace SimForge.PostgreSql.Tests;

public sealed class TransactionTests
{
    private static readonly Guid OrderId = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid MessageId = Guid.Parse("20000000-0000-0000-0000-000000000001");

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Transaction_reads_its_own_writes_while_others_see_only_committed_state()
    {
        await using var test = await CreateAsync(cancellationToken: Token);
        using var transaction = test.Database.BeginTransaction();

        transaction.Insert("orders", Order(OrderId));
        transaction.Update("orders", OrderId, new Dictionary<string, object?> { ["note"] = "draft" });

        Assert.Equal("draft", transaction.Get("orders", OrderId)!.Get<string>("note"));
        Assert.Single(transaction.Scan("orders"));
        Assert.Null(test.Database.Get("orders", OrderId));
        Assert.Empty(test.Database.Scan("orders"));

        transaction.Commit();
        Assert.Equal("draft", test.Database.Get("orders", OrderId)!.Get<string>("note"));
        Assert.Equal(PostgreSqlTransactionState.Committed, transaction.State);
    }

    [Fact]
    public async Task Multi_table_commit_is_atomic()
    {
        await using var test = await CreateAsync(cancellationToken: Token);
        using var transaction = test.Database.BeginTransaction();
        transaction.Insert("orders", Order(OrderId));
        transaction.Insert("outbox", Outbox(MessageId, OrderId));

        Assert.Empty(test.Database.Scan("orders"));
        Assert.Empty(test.Database.Scan("outbox"));
        transaction.Commit();

        Assert.Single(test.Database.Scan("orders"));
        Assert.Single(test.Database.Scan("outbox"));
        var commit = test.Environment.Journal.GetEntries().Single(entry => entry.Operation == PostgreSqlOperations.Commit);
        Assert.Equal(("orders,outbox", OperationOutcome.Succeeded), (commit.Target, commit.Outcome));
        Assert.Contains("changes=2", commit.Details, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rollback_and_disposal_discard_every_uncommitted_change()
    {
        await using var test = await CreateAsync(cancellationToken: Token);
        test.Database.Insert("orders", Order(OrderId, customer: "committed"));

        var rolledBack = test.Database.BeginTransaction();
        rolledBack.Update("orders", OrderId, new Dictionary<string, object?> { ["customer"] = "changed" });
        rolledBack.Insert("outbox", Outbox(MessageId, OrderId));
        rolledBack.Rollback();

        using (var disposed = test.Database.BeginTransaction())
        {
            disposed.Delete("orders", OrderId);
        }

        Assert.Equal("committed", test.Database.Get("orders", OrderId)!.Get<string>("customer"));
        Assert.Empty(test.Database.Scan("outbox"));
        Assert.Equal(PostgreSqlTransactionState.RolledBack, rolledBack.State);
    }

    [Fact]
    public async Task A_statement_error_aborts_the_transaction_and_commit_fails_without_publishing_earlier_writes()
    {
        await using var test = await CreateAsync(cancellationToken: Token);
        test.Database.Insert("orders", Order(OrderId));
        var transaction = test.Database.BeginTransaction();
        transaction.Insert("outbox", Outbox(MessageId, OrderId));

        var violation = Assert.Throws<PostgreSqlStorageException>(() => transaction.Insert("orders", Order(OrderId)));
        Assert.Equal(PostgreSqlTransactionState.Failed, transaction.State);

        var rejected = Assert.Throws<PostgreSqlStorageException>(() => transaction.Get("orders", OrderId));
        var commit = Assert.Throws<PostgreSqlStorageException>(transaction.Commit);

        Assert.Equal(PostgreSqlErrorCodes.UniqueViolation, violation.ErrorCode);
        Assert.Equal(PostgreSqlErrorCodes.InFailedTransaction, rejected.ErrorCode);
        Assert.Equal(PostgreSqlErrorCodes.InFailedTransaction, commit.ErrorCode);
        Assert.Same(violation, commit.InnerException);
        Assert.Equal(PostgreSqlTransactionState.RolledBack, transaction.State);
        Assert.Empty(test.Database.Scan("outbox"));
        using var next = test.Database.BeginTransaction();
    }

    [Fact]
    public async Task Unsupported_requests_inside_a_transaction_do_not_abort_it()
    {
        await using var test = await CreateAsync(cancellationToken: Token);
        using var transaction = test.Database.BeginTransaction();
        transaction.Insert("orders", Order(OrderId));

        Assert.Throws<UnsupportedCapabilityException>(
            () => transaction.Update("orders", OrderId, new Dictionary<string, object?> { ["id"] = Guid.NewGuid() }));
        Assert.Equal(PostgreSqlTransactionState.Active, transaction.State);

        transaction.Commit();
        Assert.NotNull(test.Database.Get("orders", OrderId));
    }

    [Fact]
    public async Task Primary_key_change_is_unsupported_regardless_of_column_order_and_keeps_the_transaction_active()
    {
        await using var test = await CreateAsync(cancellationToken: Token);
        using var transaction = test.Database.BeginTransaction();
        transaction.Insert("orders", Order(OrderId));
        var badValueFirst = new Dictionary<string, object?> { ["total"] = "not a number", ["id"] = Guid.NewGuid() };
        var unknownColumnFirst = new Dictionary<string, object?> { ["colour"] = "red", ["id"] = Guid.NewGuid() };

        Assert.Throws<UnsupportedCapabilityException>(() => transaction.Update("orders", OrderId, badValueFirst));
        Assert.Throws<UnsupportedCapabilityException>(() => transaction.Update("orders", OrderId, unknownColumnFirst));

        Assert.Equal(PostgreSqlTransactionState.Active, transaction.State);
        transaction.Commit();
        Assert.NotNull(test.Database.Get("orders", OrderId));
    }

    [Fact]
    public async Task Only_one_transaction_may_be_active_and_autocommit_writes_are_rejected_meanwhile()
    {
        await using var test = await CreateAsync(cancellationToken: Token);
        test.Database.Insert("orders", Order(OrderId));
        var transaction = test.Database.BeginTransaction();
        transaction.Insert("outbox", Outbox(MessageId, OrderId));

        var overlapping = Assert.Throws<UnsupportedCapabilityException>(test.Database.BeginTransaction);
        var autocommitWrite = Assert.Throws<UnsupportedCapabilityException>(() => test.Database.Delete("orders", OrderId));

        Assert.Equal(PostgreSqlCapabilityIds.ConcurrentTransactions, overlapping.CapabilityId);
        Assert.Equal(PostgreSqlCapabilityIds.ConcurrentTransactions, autocommitWrite.CapabilityId);
        Assert.NotNull(test.Database.Get("orders", OrderId));
        Assert.Equal(PostgreSqlTransactionState.Active, transaction.State);

        transaction.Commit();
        using var next = test.Database.BeginTransaction();
        Assert.Single(next.Scan("outbox"));
    }

    [Fact]
    public async Task A_failed_transaction_keeps_the_writer_slot_until_it_is_rolled_back()
    {
        await using var test = await CreateAsync(cancellationToken: Token);
        var failed = test.Database.BeginTransaction();
        Assert.Throws<PostgreSqlStorageException>(() => failed.Insert("missing_table", Order(OrderId)));

        Assert.Throws<UnsupportedCapabilityException>(test.Database.BeginTransaction);
        failed.Rollback();
        using var next = test.Database.BeginTransaction();
        Assert.Equal(PostgreSqlTransactionState.Active, next.State);
    }

    [Fact]
    public async Task Completed_transactions_cannot_be_reused_but_can_be_disposed()
    {
        await using var test = await CreateAsync(cancellationToken: Token);
        var committed = test.Database.BeginTransaction();
        committed.Commit();
        var rolledBack = test.Database.BeginTransaction();
        rolledBack.Rollback();

        Assert.Throws<InvalidOperationException>(committed.Commit);
        Assert.Throws<InvalidOperationException>(committed.Rollback);
        Assert.Throws<InvalidOperationException>(() => committed.Insert("orders", Order(OrderId)));
        Assert.Throws<InvalidOperationException>(rolledBack.Rollback);
        committed.Dispose();
        await rolledBack.DisposeAsync();
        Assert.Equal(PostgreSqlTransactionState.Committed, committed.State);
    }

    [Fact]
    public async Task Commit_fault_before_commit_publishes_nothing_and_reports_no_state_change()
    {
        await using var test = await CreateAsync(cancellationToken: Token);
        var fault = test.Database.InjectFault(PostgreSqlOperations.Commit, FaultPhases.Before, reason: "connection reset before commit");
        var transaction = test.Database.BeginTransaction();
        transaction.Insert("orders", Order(OrderId));
        transaction.Insert("outbox", Outbox(MessageId, OrderId));

        var exception = Assert.Throws<SimulatedFaultException>(transaction.Commit);

        Assert.False(exception.StateChanged);
        Assert.Equal(fault.Id, exception.Report.RuleId);
        Assert.Equal(PostgreSqlTransactionState.RolledBack, transaction.State);
        Assert.Empty(test.Database.Scan("orders"));
        Assert.Empty(test.Database.Scan("outbox"));
        Assert.DoesNotContain(test.Environment.Journal.GetEntries(), entry => entry.Operation == PostgreSqlOperations.Commit && entry.Outcome == OperationOutcome.Succeeded);
    }

    [Fact]
    public async Task Commit_fault_after_commit_publishes_changes_but_reports_an_ambiguous_outcome()
    {
        await using var test = await CreateAsync(cancellationToken: Token);
        test.Database.InjectFault(PostgreSqlOperations.Commit, FaultPhases.After);
        var transaction = test.Database.BeginTransaction();
        transaction.Insert("orders", Order(OrderId));

        var exception = Assert.Throws<SimulatedFaultException>(transaction.Commit);

        Assert.True(exception.StateChanged);
        Assert.Equal(PostgreSqlTransactionState.Committed, transaction.State);
        Assert.NotNull(test.Database.Get("orders", OrderId));
        var commitEntries = test.Environment.Journal.GetEntries().Where(entry => entry.Operation == PostgreSqlOperations.Commit).ToList();
        Assert.Equal([OperationOutcome.Succeeded, OperationOutcome.Faulted], commitEntries.Select(entry => entry.Outcome));
    }

    [Fact]
    public async Task Statement_fault_fires_at_the_exact_occurrence_and_aborts_the_transaction()
    {
        await using var test = await CreateAsync(cancellationToken: Token);
        test.Database.InjectFault(PostgreSqlOperations.Insert, occurrence: 2);
        var transaction = test.Database.BeginTransaction();

        transaction.Insert("orders", Order(OrderId));
        var exception = Assert.Throws<SimulatedFaultException>(() => transaction.Insert("outbox", Outbox(MessageId, OrderId)));

        Assert.False(exception.StateChanged);
        Assert.Equal(2, exception.Report.Occurrence);
        Assert.Equal(PostgreSqlTransactionState.Failed, transaction.State);
        Assert.Throws<PostgreSqlStorageException>(transaction.Commit);
        Assert.Empty(test.Database.Scan("orders"));
    }

    [Fact]
    public async Task Autocommit_statement_fault_leaves_state_unchanged_and_the_next_call_succeeds()
    {
        await using var test = await CreateAsync(cancellationToken: Token);
        test.Database.InjectFault(PostgreSqlOperations.Insert);

        Assert.Throws<SimulatedFaultException>(() => test.Database.Insert("orders", Order(OrderId)));
        Assert.Empty(test.Database.Scan("orders"));

        test.Database.Insert("orders", Order(OrderId));
        Assert.Single(test.Database.Scan("orders"));
    }

    [Fact]
    public async Task Fault_rules_for_undeclared_points_are_rejected()
    {
        await using var test = await CreateAsync(cancellationToken: Token);

        Assert.Throws<UnsupportedCapabilityException>(() => test.Database.InjectFault(PostgreSqlOperations.Rollback));
        Assert.Throws<UnsupportedCapabilityException>(() => test.Database.InjectFault(PostgreSqlOperations.Insert, FaultPhases.After));
    }
}
