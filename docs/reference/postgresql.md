# SimForge.PostgreSql

Namespace `SimForge.PostgreSql`. An in-memory, application-contract storage model with PostgreSQL-oriented semantics. It
exposes explicit schema, row, key, and transaction operations. It does not parse or execute SQL, is not an Npgsql or EF
Core provider, exposes no connection string, and does not implement the PostgreSQL wire protocol.

- [Registration and schema](#registration-and-schema)
- [SimulatedPostgreSqlDatabase](#simulatedpostgresqldatabase)
- [PostgreSqlTransaction](#postgresqltransaction)
- [Row](#row)
- [Fault points](#fault-points)
- [Capability table](#capability-table)
- [Value rules](#value-rules)
- [Transaction model](#transaction-model)
- [Error codes](#error-codes)

## Registration and schema

```csharp
SimulatedPostgreSqlDatabase AddPostgreSqlDatabase(this SimulationEnvironment environment, string name, Action<DatabaseSchemaBuilder> configureSchema)
```

`AddPostgreSqlDatabase` is a C# 14 extension member. It builds the schema, creates the database, and registers it with
the environment. The environment must be in state `Created`. The schema is fixed from then on.

| Builder member | Description |
|---|---|
| `DatabaseSchemaBuilder.Table(name, configure)` | Declares a table. At least one table is required, and table names are unique. |
| `TableBuilder.Column(name, type, primaryKey = false, notNull = false, unique = false)` | Declares a column. Exactly one column per table has `primaryKey: true`. A primary key is implicitly NOT NULL and unique. |

Table and column names match `[a-z_][a-z0-9_]*` and have at most 63 characters. Other names throw
`UnsupportedCapabilityException` (`postgresql.schema.quoted-identifiers`). A second primary-key column throws
`postgresql.constraints.composite-keys`, and a table without one throws `postgresql.schema.tables-without-primary-key`.
Duplicate table or column names throw `ArgumentException`.

| `ColumnType` | PostgreSQL type | CLR type |
|---|---|---|
| `Boolean` | `boolean` | `bool` |
| `Integer` | `integer` | `int` |
| `BigInt` | `bigint` | `long` |
| `Numeric` | `numeric` | `decimal` |
| `Text` | `text` | `string` |
| `Uuid` | `uuid` | `Guid` |
| `TimestampTz` | `timestamp with time zone` | `DateTimeOffset` |
| `Bytea` | `bytea` | `byte[]` |

## SimulatedPostgreSqlDatabase

Implements `ISimulationResource`. `ProviderName` is `postgresql`.

| Member | Description |
|---|---|
| `Name`, `Provider`, `Schema`, `FaultPoints` | Resource identity, schema, and declared fault points. |
| `Get(table, key)` | Committed row with the primary key, or null. |
| `Scan(table)` | All committed rows, in ascending primary-key order. |
| `Insert(table, values)` | Autocommit insert. Omitted columns are NULL. |
| `Update(table, key, changes)` | Autocommit update of the given columns. Returns false when no row has the key. `changes` must not be empty and must not contain the primary-key column. |
| `Delete(table, key)` | Autocommit delete. Returns false when no row has the key. |
| `BeginTransaction()` | Starts the database's single transaction. |
| `InjectFault(operation, phase = FaultPhases.Before, occurrence = 1, reason = null)` | Registers a fault rule for this database and returns its `ActiveFault`. |

`values` and `changes` are `IReadOnlyDictionary<string, object?>` keyed by column name. A `Row` can be passed as
`values`.

## PostgreSqlTransaction

Implements `IDisposable` and `IAsyncDisposable`.

| Member | Description |
|---|---|
| `Id` | Sequential number within the database. |
| `State` | `Active`, `Failed`, `Committed`, or `RolledBack`. |
| `Get`, `Scan`, `Insert`, `Update`, `Delete` | Same signatures as the database methods. They read the transaction's own writes and write to its candidate state. |
| `Commit()` | Validates the candidate state and publishes all changes at once. |
| `Rollback()` | Discards all changes. Throws `InvalidOperationException` when the transaction has completed. |
| `Dispose()`, `DisposeAsync()` | Roll back unless completed. Safe to call repeatedly. |

`Get`, `Scan`, `Insert`, `Update`, `Delete`, `Commit`, and `Rollback` throw `InvalidOperationException` once the
transaction is `Committed` or `RolledBack`. After the environment is disposed they throw `ObjectDisposedException`.

## Row

An immutable snapshot of one row. It implements `IReadOnlyDictionary<string, object?>` over every column in declaration
order.

| Member | Description |
|---|---|
| `Table`, `Definition` | The row's table. |
| `this[column]` | Column value, or null for SQL NULL. `byte[]` values are copies. An unknown column throws `KeyNotFoundException`. |
| `Get<T>(column)` | Typed value. NULL throws `InvalidOperationException` unless `T` is `Nullable<>`. A type mismatch throws `InvalidCastException`. |
| `IsNull(column)` | Whether the value is NULL. |

## Fault points

| Operation (`PostgreSqlOperations`) | Phases | Effect when a rule fires |
|---|---|---|
| `begin` | `before` | No transaction is started. |
| `get`, `scan` | `before` | The read fails. Inside a transaction, the transaction becomes `Failed`. |
| `insert`, `update`, `delete` | `before` | The statement is not applied. Inside a transaction, the transaction becomes `Failed`. |
| `commit` | `before` | Nothing is published; the transaction becomes `RolledBack`. |
| `commit` | `after` | All changes are published and the transaction becomes `Committed`, but `Commit()` throws `SimulatedFaultException` with `StateChanged == true`. |

Statement fault points are evaluated after argument validation. Commit fault points apply only to explicit
transactions.

## Capability table

The machine-readable form is [`postgresql.capabilities.json`](postgresql.capabilities.json), generated from
`PostgreSqlCapabilities.Manifest`. No capability is verified against a reference PostgreSQL version.

| Capability | Status | Level | What it covers |
|---|---|---|---|
| `postgresql.schema.tables` | SimulatedOnly | ApplicationContract | Named tables with declared columns, fixed when the database is added. Names are lowercase unquoted identifiers (≤ 63 characters), and every table has exactly one primary-key column. |
| `postgresql.types.scalar` | SimulatedOnly | ServiceSemantics | `boolean`/`bool`, `integer`/`int`, `bigint`/`long`, `numeric`/`decimal`, `text`/`string`, `uuid`/`Guid`, `timestamptz`/`DateTimeOffset` (UTC), `bytea`/`byte[]`. The CLR type must match exactly. |
| `postgresql.rows.crud-by-key` | SimulatedOnly | ApplicationContract | Insert (omitted columns are NULL), get by key, update by key, and delete by key. Update and delete report whether a row matched. |
| `postgresql.rows.scan` | SimulatedOnly | ApplicationContract | Deterministic full-table scan in ascending primary-key order. |
| `postgresql.constraints.primary-key` | SimulatedOnly | ServiceSemantics | Single-column primary keys: NOT NULL and unique (`<table>_pkey`). |
| `postgresql.constraints.not-null` | SimulatedOnly | ServiceSemantics | NOT NULL checked on insert and update. |
| `postgresql.constraints.unique` | SimulatedOnly | ServiceSemantics | Single-column UNIQUE (`<table>_<column>_key`) with NULLS DISTINCT: any number of NULLs is allowed. |
| `postgresql.transactions.single-writer` | SimulatedOnly | ServiceSemantics | Explicit transactions: read-your-own-writes, no visibility of uncommitted writes to other readers, atomic multi-table commit, and rollback. A statement error aborts the transaction. At most one active transaction per database. |
| `postgresql.faults` | SimulatedOnly | ApplicationContract | One-shot faults at `begin`, `get`, `scan`, `insert`, `update`, `delete`, and `commit` (`before`), plus `commit` (`after`), which is an ambiguous outcome: the change is visible, but the caller receives an error. |
| `postgresql.sql` | Unsupported | ServiceSemantics | SQL text, including parameterized SQL. |
| `postgresql.client-compatibility` | Unsupported | WireProtocol | Unchanged Npgsql, EF Core, or other drivers; connection strings; the wire protocol. |
| `postgresql.transactions.concurrent` | Unsupported | ServiceSemantics | Overlapping transactions, and autocommit writes while a transaction is active. |
| `postgresql.transactions.isolation-levels` | Unsupported | ServiceSemantics | READ COMMITTED, REPEATABLE READ, and SERIALIZABLE semantics. |
| `postgresql.transactions.savepoints` | Unsupported | ServiceSemantics | Savepoints. |
| `postgresql.constraints.composite-keys` | Unsupported | ServiceSemantics | Multi-column primary keys. |
| `postgresql.schema.tables-without-primary-key` | Unsupported | ServiceSemantics | Tables without a primary key. |
| `postgresql.rows.primary-key-update` | Unsupported | ServiceSemantics | Changing a primary-key value with an update. |
| `postgresql.schema.quoted-identifiers` | Unsupported | ServiceSemantics | Quoted, mixed-case, or over-length identifiers. |
| `postgresql.schema.column-defaults` | Unsupported | ServiceSemantics | DEFAULT expressions and generated columns. |
| `postgresql.schema.runtime-ddl` | Unsupported | ServiceSemantics | Creating or altering tables after setup; migrations. |
| `postgresql.sequences` | Unsupported | ServiceSemantics | Sequences, serial and identity columns. Identifiers are assigned by the application. |
| `postgresql.types.other` | Unsupported | ServiceSemantics | json/jsonb, arrays, enums, ranges, and other types. |
| `postgresql.queries` | Unsupported | ServiceSemantics | Joins, predicates, secondary indexes, ordering other than by primary key, aggregation, and LINQ translation. |
| `postgresql.constraints.other` | Unsupported | ServiceSemantics | Foreign keys, CHECK, multi-column UNIQUE, exclusion constraints, and NULLS NOT DISTINCT. |
| `postgresql.locks-mvcc` | Unsupported | ServiceSemantics | Locks, `SELECT ... FOR UPDATE`, MVCC snapshots, and deadlocks. |

Unsupported requests throw `UnsupportedCapabilityException` (carrying the capability ID) before any state changes, and
they do not abort an active transaction.

## Value rules

| Rule | Behavior |
|---|---|
| NULL | `null` is SQL NULL. `DBNull` is rejected. |
| Type matching | Exact CLR type per column; there are no implicit conversions. `int` for a `bigint` column fails with `datatype_mismatch`. |
| `numeric` equality | By value: `1.0` equals `1.00` for keys and uniqueness. The stored scale is preserved on read. |
| `timestamptz` | Normalized to UTC; values with different offsets for the same instant are equal. |
| `text` equality and order | Ordinal UTF-16. Text containing NUL or unpaired surrogates is rejected with `character_not_in_repertoire`. |
| `bytea` | Compared and ordered as unsigned bytes. It is copied on write and on every read. |
| `uuid` order | RFC 4122 byte order (the order of the canonical string form). |
| Copy boundary | Rows are immutable snapshots. Mutating an input dictionary, an input array, or a returned array never changes stored state. |

## Transaction model

The model is not a PostgreSQL isolation level.

1. `BeginTransaction()` claims the database's single writer slot. A second `BeginTransaction()`, or an autocommit write,
   throws `UnsupportedCapabilityException` (`postgresql.transactions.concurrent`) until the transaction completes.
   Autocommit reads are allowed and see committed state only.
2. Statements inside the transaction run against a candidate state. The transaction reads its own writes.
3. Any statement error (constraint violation, undefined table or column, datatype mismatch, or injected fault) moves
   the transaction to `Failed`. Every later statement fails with `in_failed_sql_transaction`.
4. `Commit()` on a failed transaction throws `in_failed_sql_transaction` and rolls back. Deviation: PostgreSQL reports
   `ROLLBACK` for `COMMIT` in an aborted block without raising an error.
5. Otherwise, `Commit()` re-validates the touched rows of the candidate state and makes every changed table visible in
   one step.
6. `Rollback()` or disposal discards the candidate state. Disposing the environment rolls back any active transaction.

## Error codes

SimForge error codes reuse PostgreSQL condition *names* for readability. No SQLSTATE parity is claimed.

| Code | Raised when |
|---|---|
| `unique_violation` | A duplicate primary key or unique value (`ConstraintName` is `<table>_pkey` or `<table>_<column>_key`). The conflicting value is not included in the message. |
| `not_null_violation` | NULL in a NOT NULL or primary-key column. |
| `undefined_table` / `undefined_column` | Unknown table or column. |
| `datatype_mismatch` | Wrong CLR type for a column or key. |
| `character_not_in_repertoire` | Text that cannot be stored as UTF-8. |
| `in_failed_sql_transaction` | A statement or commit on a failed transaction. |
| `injected_fault` | A fault rule fired (`SimulatedFaultException`; `StateChanged` distinguishes the phases). |
