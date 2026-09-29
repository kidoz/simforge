using System.Collections.Immutable;
using System.Globalization;

namespace SimForge.PostgreSql;

/// <summary>Operation names used in the journal and by fault rules.</summary>
public static class PostgreSqlOperations
{
    public const string Begin = "begin";
    public const string Get = "get";
    public const string Scan = "scan";
    public const string Insert = "insert";
    public const string Update = "update";
    public const string Delete = "delete";
    public const string Commit = "commit";
    public const string Rollback = "rollback";
}

/// <summary>
/// A PostgreSQL-oriented, in-memory <b>application-contract</b> storage simulation: explicit schema, row, key, and
/// transaction operations for test-side repository adapters.
/// </summary>
/// <remarks>
/// <para>This is not a SQL engine, not an Npgsql or EF Core provider, and not wire compatible. It exposes no connection
/// string and executes no SQL. Unsupported requests throw <see cref="UnsupportedCapabilityException"/> before any state
/// changes. See <see cref="PostgreSqlCapabilities.Manifest"/> for the declared subset and its known deviations.</para>
/// <para>Methods on the database itself are autocommit statements. Reads see committed state only, so another caller's
/// uncommitted writes are never visible. <see cref="BeginTransaction"/> starts an explicit transaction. The transaction
/// model is deliberately restricted: at most one transaction may be active per database, and autocommit writes are
/// rejected while one is. This is <b>not</b> PostgreSQL Read Committed, Repeatable Read, or Serializable.</para>
/// </remarks>
public sealed class SimulatedPostgreSqlDatabase : ISimulationResource
{
    public const string ProviderName = "postgresql";

    private static readonly FaultPoint[] DeclaredFaultPoints =
    [
        new(ProviderName, PostgreSqlOperations.Begin, FaultPhases.Before),
        new(ProviderName, PostgreSqlOperations.Get, FaultPhases.Before),
        new(ProviderName, PostgreSqlOperations.Scan, FaultPhases.Before),
        new(ProviderName, PostgreSqlOperations.Insert, FaultPhases.Before),
        new(ProviderName, PostgreSqlOperations.Update, FaultPhases.Before),
        new(ProviderName, PostgreSqlOperations.Delete, FaultPhases.Before),
        new(ProviderName, PostgreSqlOperations.Commit, FaultPhases.Before),
        new(ProviderName, PostgreSqlOperations.Commit, FaultPhases.After),
    ];

    private readonly Lock _gate = new();
    private readonly SimulationEnvironment _environment;
    private ImmutableDictionary<string, TableState> _committed = ImmutableDictionary.Create<string, TableState>(StringComparer.Ordinal);
    private PostgreSqlTransaction? _active;
    private long _transactionCount;
    private bool _initialized;
    private bool _disposed;

    internal SimulatedPostgreSqlDatabase(SimulationEnvironment environment, string name, DatabaseSchema schema)
    {
        _environment = environment;
        Name = name;
        Schema = schema;
    }

    public string Name { get; }

    public string Provider => ProviderName;

    public DatabaseSchema Schema { get; }

    public IReadOnlyCollection<FaultPoint> FaultPoints => DeclaredFaultPoints;

    ValueTask ISimulationResource.InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _committed = Schema.Tables.ToImmutableDictionary(table => table.Name, TableState.Empty, StringComparer.Ordinal);
            _initialized = true;
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>Discards all state and rolls back an active transaction. Called by the owning environment.</summary>
    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (!_disposed)
            {
                _disposed = true;
                if (_active is { } transaction)
                {
                    Finish(transaction, PostgreSqlTransactionState.RolledBack);
                }

                _committed = _committed.Clear();
            }
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>Registers a one-shot fault for this database. See <see cref="PostgreSqlOperations"/> and <see cref="FaultPoints"/>.</summary>
    public ActiveFault InjectFault(string operation, string phase = FaultPhases.Before, int occurrence = 1, string? reason = null) =>
        _environment.Faults.Add(new FaultRule
        {
            Provider = ProviderName,
            Resource = Name,
            Operation = operation,
            Phase = phase,
            Occurrence = occurrence,
            Reason = reason,
        });

    /// <summary>Reads a committed row by primary key, or returns null.</summary>
    public Row? Get(string table, object key) => GetCore(null, table, key);

    /// <summary>Returns all committed rows, ordered by primary key. See the capability manifest for the ordering rules.</summary>
    public IReadOnlyList<Row> Scan(string table) => ScanCore(null, table);

    /// <summary>Autocommit insert. Omitted columns are NULL; column defaults are not supported.</summary>
    public void Insert(string table, IReadOnlyDictionary<string, object?> values) => InsertCore(null, table, values);

    /// <summary>Autocommit update by primary key. Returns false when no row has the key; the primary key itself cannot be changed.</summary>
    public bool Update(string table, object key, IReadOnlyDictionary<string, object?> changes) => UpdateCore(null, table, key, changes);

    /// <summary>Autocommit delete by primary key. Returns false when no row has the key.</summary>
    public bool Delete(string table, object key) => DeleteCore(null, table, key);

    /// <summary>
    /// Starts the database's single write transaction. Throws <see cref="UnsupportedCapabilityException"/> while another
    /// transaction is active, including a failed one that has not been rolled back or disposed.
    /// </summary>
    public PostgreSqlTransaction BeginTransaction()
    {
        lock (_gate)
        {
            EnsureUsable();
            if (_active is { } active)
            {
                Journal(PostgreSqlOperations.Begin, FaultPhases.Before, OperationOutcome.Rejected, details: $"active tx={Format(active.Id)}");
                throw new UnsupportedCapabilityException(
                    PostgreSqlCapabilityIds.ConcurrentTransactions,
                    $"Database '{Name}' already has active transaction {active.Id}; overlapping transactions are not supported");
            }

            _environment.Faults.ThrowIfTriggered(this, PostgreSqlOperations.Begin, FaultPhases.Before, stateChanged: false);
            var transaction = new PostgreSqlTransaction(this, ++_transactionCount);
            _active = transaction;
            Journal(PostgreSqlOperations.Begin, FaultPhases.After, OperationOutcome.Succeeded, details: $"tx={Format(transaction.Id)}");
            return transaction;
        }
    }

    internal Row? GetCore(PostgreSqlTransaction? transaction, string table, object key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Statement(transaction, PostgreSqlOperations.Get, table, write: false, definition =>
        {
            var normalizedKey = NormalizeKey(definition, key);
            _environment.Faults.ThrowIfTriggered(this, PostgreSqlOperations.Get, FaultPhases.Before, stateChanged: false);
            var row = View(transaction, definition.Name).Find(normalizedKey);
            return (row, row is null ? "rows=0" : "rows=1");
        }, () => $"key={PostgreSqlValues.Format(key)}");
    }

    internal IReadOnlyList<Row> ScanCore(PostgreSqlTransaction? transaction, string table) =>
        Statement(transaction, PostgreSqlOperations.Scan, table, write: false, definition =>
        {
            _environment.Faults.ThrowIfTriggered(this, PostgreSqlOperations.Scan, FaultPhases.Before, stateChanged: false);
            IReadOnlyList<Row> rows = [.. View(transaction, definition.Name).Rows.Values];
            return (rows, $"rows={Format(rows.Count)}");
        }, payload: null);

    internal void InsertCore(PostgreSqlTransaction? transaction, string table, IReadOnlyDictionary<string, object?> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        Statement(transaction, PostgreSqlOperations.Insert, table, write: true, definition =>
        {
            var row = BuildRow(definition, values);
            _environment.Faults.ThrowIfTriggered(this, PostgreSqlOperations.Insert, FaultPhases.Before, stateChanged: false);
            var candidate = transaction?.Candidate ?? new Candidate();
            var next = candidate.Read(_committed, definition.Name).Insert(Name, row);
            candidate.Write(_committed, definition.Name, next, row.PrimaryKey);
            PublishIfAutocommit(transaction, candidate);
            return (true, "rows=1");
        }, () => FormatValues(values));
    }

    internal bool UpdateCore(PostgreSqlTransaction? transaction, string table, object key, IReadOnlyDictionary<string, object?> changes)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(changes);
        if (changes.Count == 0)
        {
            throw new ArgumentException("An update must change at least one column.", nameof(changes));
        }

        return Statement(transaction, PostgreSqlOperations.Update, table, write: true, definition =>
        {
            // Checked before any column is validated, so the request is rejected as unsupported regardless of dictionary order.
            if (changes.ContainsKey(definition.PrimaryKey.Name))
            {
                throw new UnsupportedCapabilityException(
                    PostgreSqlCapabilityIds.PrimaryKeyUpdate,
                    $"Changing primary key '{definition.Name}.{definition.PrimaryKey.Name}' is not supported; delete and insert instead");
            }

            var normalizedKey = NormalizeKey(definition, key);
            var resolved = new List<(int Ordinal, object? Value)>(changes.Count);
            foreach (var (columnName, value) in changes)
            {
                var column = ResolveColumn(definition, columnName);
                resolved.Add((column.Ordinal, PostgreSqlValues.Normalize(Name, definition, column, value)));
            }

            _environment.Faults.ThrowIfTriggered(this, PostgreSqlOperations.Update, FaultPhases.Before, stateChanged: false);
            var candidate = transaction?.Candidate ?? new Candidate();
            var state = candidate.Read(_committed, definition.Name);
            if (state.Find(normalizedKey) is not { } current)
            {
                return (false, "rows=0");
            }

            var next = state.Update(Name, current, current.With(resolved));
            candidate.Write(_committed, definition.Name, next, normalizedKey);
            PublishIfAutocommit(transaction, candidate);
            return (true, "rows=1");
        }, () => $"key={PostgreSqlValues.Format(key)}; {FormatValues(changes)}");
    }

    internal bool DeleteCore(PostgreSqlTransaction? transaction, string table, object key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Statement(transaction, PostgreSqlOperations.Delete, table, write: true, definition =>
        {
            var normalizedKey = NormalizeKey(definition, key);
            _environment.Faults.ThrowIfTriggered(this, PostgreSqlOperations.Delete, FaultPhases.Before, stateChanged: false);
            var candidate = transaction?.Candidate ?? new Candidate();
            var state = candidate.Read(_committed, definition.Name);
            if (state.Find(normalizedKey) is not { } current)
            {
                return (false, "rows=0");
            }

            candidate.Write(_committed, definition.Name, state.Delete(current), normalizedKey);
            PublishIfAutocommit(transaction, candidate);
            return (true, "rows=1");
        }, () => $"key={PostgreSqlValues.Format(key)}");
    }

    internal void Commit(PostgreSqlTransaction transaction)
    {
        lock (_gate)
        {
            EnsureUsable();
            EnsureNotCompleted(transaction);
            var details = $"tx={Format(transaction.Id)}";
            if (transaction.State == PostgreSqlTransactionState.Failed)
            {
                Finish(transaction, PostgreSqlTransactionState.RolledBack);
                const string message = "The transaction was aborted by an earlier error; it was rolled back instead of committed.";
                Journal(PostgreSqlOperations.Commit, FaultPhases.Before, OperationOutcome.Failed, details: details, error: message);
                throw new PostgreSqlStorageException(Name, PostgreSqlErrorCodes.InFailedTransaction, message, innerException: transaction.FailureCause);
            }

            if (_environment.Faults.Evaluate(this, PostgreSqlOperations.Commit, FaultPhases.Before, stateChanged: false) is { } rejected)
            {
                Finish(transaction, PostgreSqlTransactionState.RolledBack);
                throw new SimulatedFaultException(rejected);
            }

            var candidate = transaction.Candidate!;
            try
            {
                Publish(candidate);
            }
            catch (Exception exception) when (exception is PostgreSqlStorageException or SimForgeInternalException)
            {
                Finish(transaction, PostgreSqlTransactionState.RolledBack);
                Journal(PostgreSqlOperations.Commit, FaultPhases.Before, OperationOutcome.Failed, details: details, error: exception.Message);
                throw;
            }

            Finish(transaction, PostgreSqlTransactionState.Committed);
            Journal(
                PostgreSqlOperations.Commit,
                FaultPhases.After,
                OperationOutcome.Succeeded,
                target: string.Join(",", candidate.ChangedTables),
                details: $"{details}; changes={Format(candidate.ChangeCount)}");

            if (_environment.Faults.Evaluate(this, PostgreSqlOperations.Commit, FaultPhases.After, stateChanged: true) is { } lostResponse)
            {
                throw new SimulatedFaultException(lostResponse);
            }
        }
    }

    internal void Rollback(PostgreSqlTransaction transaction, bool explicitRollback)
    {
        lock (_gate)
        {
            if (transaction.State is PostgreSqlTransactionState.Committed or PostgreSqlTransactionState.RolledBack)
            {
                if (explicitRollback)
                {
                    EnsureNotCompleted(transaction);
                }

                return;
            }

            var discarded = transaction.Candidate?.ChangeCount ?? 0;
            Finish(transaction, PostgreSqlTransactionState.RolledBack);
            Journal(
                PostgreSqlOperations.Rollback,
                FaultPhases.After,
                OperationOutcome.Succeeded,
                details: $"tx={Format(transaction.Id)}; discarded={Format(discarded)}; {(explicitRollback ? "explicit" : "disposed")}");
        }
    }

    private TResult Statement<TResult>(
        PostgreSqlTransaction? transaction,
        string operation,
        string table,
        bool write,
        Func<TableDefinition, (TResult Result, string Details)> body,
        Func<string?>? payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        lock (_gate)
        {
            EnsureUsable();
            var scope = transaction is null ? "autocommit" : $"tx={Format(transaction.Id)}";
            if (transaction is not null)
            {
                EnsureNotCompleted(transaction);
                if (transaction.State == PostgreSqlTransactionState.Failed)
                {
                    const string message = "The current transaction is aborted; statements are rejected until it is rolled back.";
                    Journal(operation, FaultPhases.Before, OperationOutcome.Failed, table, scope, message, payload);
                    throw new PostgreSqlStorageException(Name, PostgreSqlErrorCodes.InFailedTransaction, message, table, innerException: transaction.FailureCause);
                }
            }
            else if (write && _active is { } active)
            {
                Journal(operation, FaultPhases.Before, OperationOutcome.Rejected, table, $"{scope}; active tx={Format(active.Id)}", payload: payload);
                throw new UnsupportedCapabilityException(
                    PostgreSqlCapabilityIds.ConcurrentTransactions,
                    $"Autocommit {operation} on '{table}' was rejected because transaction {active.Id} is active in database '{Name}'; concurrent writers are not supported");
            }

            try
            {
                var definition = ResolveTable(table);
                var (result, details) = body(definition);
                Journal(operation, FaultPhases.After, OperationOutcome.Succeeded, table, $"{scope}; {details}", payload: payload);
                return result;
            }
            catch (PostgreSqlStorageException exception)
            {
                transaction?.Fail(exception);
                Journal(operation, FaultPhases.Before, OperationOutcome.Failed, table, scope, exception.Message, payload);
                throw;
            }
            catch (SimulatedFaultException exception)
            {
                // The injector already journaled the fault. An injected statement error aborts the transaction like any other error.
                transaction?.Fail(exception);
                throw;
            }
            catch (UnsupportedCapabilityException exception)
            {
                Journal(operation, FaultPhases.Before, OperationOutcome.Rejected, table, scope, exception.Message, payload);
                throw;
            }
        }
    }

    private TableState View(PostgreSqlTransaction? transaction, string table) =>
        transaction?.Candidate is { } candidate ? candidate.Read(_committed, table) : _committed[table];

    private void PublishIfAutocommit(PostgreSqlTransaction? transaction, Candidate candidate)
    {
        if (transaction is null)
        {
            Publish(candidate);
        }
    }

    /// <summary>Validates the candidate state of every changed table, then makes all of them visible in one assignment.</summary>
    private void Publish(Candidate candidate)
    {
        var next = _committed;
        foreach (var table in candidate.ChangedTables)
        {
            if (!ReferenceEquals(_committed[table], candidate.BaseOf(table)))
            {
                throw new SimForgeInternalException($"Committed state of table '{table}' changed while a write transaction was active.");
            }

            var state = candidate.StateOf(table);
            state.ValidateTouched(Name, candidate.TouchedKeysOf(table));
            next = next.SetItem(table, state);
        }

        _committed = next;
    }

    private void Finish(PostgreSqlTransaction transaction, PostgreSqlTransactionState state)
    {
        transaction.Complete(state);
        if (ReferenceEquals(_active, transaction))
        {
            _active = null;
        }
    }

    private void EnsureUsable()
    {
        _environment.ThrowIfNotReady();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_initialized)
        {
            throw new InvalidOperationException($"Database '{Name}' has not been initialized.");
        }
    }

    private static void EnsureNotCompleted(PostgreSqlTransaction transaction)
    {
        if (transaction.State is PostgreSqlTransactionState.Committed or PostgreSqlTransactionState.RolledBack)
        {
            throw new InvalidOperationException($"Transaction {transaction.Id} has completed ({transaction.State}) and can no longer be used.");
        }
    }

    private TableDefinition ResolveTable(string table) =>
        Schema.TryGetTable(table, out var definition)
            ? definition
            : throw new PostgreSqlStorageException(Name, PostgreSqlErrorCodes.UndefinedTable, $"Table '{table}' does not exist in database '{Name}'.", table);

    private ColumnDefinition ResolveColumn(TableDefinition table, string column) =>
        table.TryGetColumn(column, out var definition)
            ? definition
            : throw new PostgreSqlStorageException(Name, PostgreSqlErrorCodes.UndefinedColumn, $"Column '{table.Name}.{column}' does not exist.", table.Name, column);

    private Key NormalizeKey(TableDefinition table, object key) =>
        new(PostgreSqlValues.Normalize(Name, table, table.PrimaryKey, key)!);

    private Row BuildRow(TableDefinition table, IReadOnlyDictionary<string, object?> values)
    {
        var stored = new object?[table.Columns.Length];
        foreach (var (columnName, value) in values)
        {
            var column = ResolveColumn(table, columnName);
            stored[column.Ordinal] = PostgreSqlValues.Normalize(Name, table, column, value);
        }

        return new Row(table, stored);
    }

    private void Journal(string operation, string phase, OperationOutcome outcome, string? target = null, string? details = null, string? error = null, Func<string?>? payload = null) =>
        _environment.Journal.Record(ProviderName, Name, operation, phase, outcome, target, details, error, payload);

    private static string FormatValues(IReadOnlyDictionary<string, object?> values) =>
        string.Join(", ", values.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key}={PostgreSqlValues.Format(pair.Value)}"));

    private static string Format(long value) => value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>Uncommitted changes of one transaction (or one autocommit statement), expressed as candidate table states.</summary>
internal sealed class Candidate
{
    private readonly SortedDictionary<string, (TableState Base, TableState State, HashSet<Key> Touched)> _tables = new(StringComparer.Ordinal);

    public IEnumerable<string> ChangedTables => _tables.Keys;

    public int ChangeCount => _tables.Values.Sum(table => table.Touched.Count);

    public TableState Read(ImmutableDictionary<string, TableState> committed, string table) =>
        _tables.TryGetValue(table, out var entry) ? entry.State : committed[table];

    public void Write(ImmutableDictionary<string, TableState> committed, string table, TableState next, Key touchedKey)
    {
        if (!_tables.TryGetValue(table, out var entry))
        {
            entry = (committed[table], committed[table], []);
        }

        entry.Touched.Add(touchedKey);
        _tables[table] = (entry.Base, next, entry.Touched);
    }

    public TableState BaseOf(string table) => _tables[table].Base;

    public TableState StateOf(string table) => _tables[table].State;

    public IEnumerable<Key> TouchedKeysOf(string table) => _tables[table].Touched;
}
