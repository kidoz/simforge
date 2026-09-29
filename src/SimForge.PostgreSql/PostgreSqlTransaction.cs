namespace SimForge.PostgreSql;

public enum PostgreSqlTransactionState
{
    Active,

    /// <summary>A statement failed. Every further statement and commit is rejected until the transaction is rolled back or disposed.</summary>
    Failed,
    Committed,
    RolledBack,
}

/// <summary>
/// Explicit transaction on a <see cref="SimulatedPostgreSqlDatabase"/>. It reads its own writes and publishes all of them
/// atomically on <see cref="Commit"/>. Disposing it without committing rolls it back.
/// </summary>
/// <remarks>
/// A statement error (constraint violation, undefined table or column, datatype mismatch, or injected fault) aborts the
/// transaction, as in PostgreSQL. Unlike PostgreSQL, where COMMIT of an aborted transaction reports ROLLBACK,
/// <see cref="Commit"/> throws, so callers cannot mistake it for success. Unsupported requests are rejected without
/// aborting the transaction.
/// </remarks>
public sealed class PostgreSqlTransaction : IDisposable, IAsyncDisposable
{
    private readonly SimulatedPostgreSqlDatabase _database;
    private int _state;

    internal PostgreSqlTransaction(SimulatedPostgreSqlDatabase database, long id)
    {
        _database = database;
        Id = id;
        Candidate = new Candidate();
    }

    /// <summary>Sequential transaction number within the database.</summary>
    public long Id { get; }

    public PostgreSqlTransactionState State => (PostgreSqlTransactionState)Volatile.Read(ref _state);

    internal Candidate? Candidate { get; private set; }

    internal Exception? FailureCause { get; private set; }

    public Row? Get(string table, object key) => _database.GetCore(this, table, key);

    public IReadOnlyList<Row> Scan(string table) => _database.ScanCore(this, table);

    public void Insert(string table, IReadOnlyDictionary<string, object?> values) => _database.InsertCore(this, table, values);

    public bool Update(string table, object key, IReadOnlyDictionary<string, object?> changes) => _database.UpdateCore(this, table, key, changes);

    public bool Delete(string table, object key) => _database.DeleteCore(this, table, key);

    /// <summary>
    /// Validates the candidate state and makes every change visible at once. It throws
    /// <see cref="PostgreSqlStorageException"/> (<see cref="PostgreSqlErrorCodes.InFailedTransaction"/>) and rolls back when
    /// the transaction had failed. An injected fault at <see cref="FaultPhases.After"/> throws after the changes became
    /// visible.
    /// </summary>
    public void Commit() => _database.Commit(this);

    /// <summary>Discards all changes. Throws when the transaction has already completed.</summary>
    public void Rollback() => _database.Rollback(this, explicitRollback: true);

    /// <summary>Rolls back unless the transaction has completed. Safe to call repeatedly.</summary>
    public void Dispose() => _database.Rollback(this, explicitRollback: false);

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    internal void Fail(Exception cause)
    {
        if (State == PostgreSqlTransactionState.Active)
        {
            FailureCause = cause;
            Volatile.Write(ref _state, (int)PostgreSqlTransactionState.Failed);
        }
    }

    internal void Complete(PostgreSqlTransactionState state)
    {
        Candidate = null;
        Volatile.Write(ref _state, (int)state);
    }
}
