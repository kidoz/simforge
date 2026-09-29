namespace SimForge.PostgreSql;

/// <summary>Capability identifiers of the PostgreSQL-oriented provider.</summary>
public static class PostgreSqlCapabilityIds
{
    public const string SchemaTables = "postgresql.schema.tables";
    public const string ScalarTypes = "postgresql.types.scalar";
    public const string CrudByKey = "postgresql.rows.crud-by-key";
    public const string Scan = "postgresql.rows.scan";
    public const string PrimaryKey = "postgresql.constraints.primary-key";
    public const string NotNull = "postgresql.constraints.not-null";
    public const string Unique = "postgresql.constraints.unique";
    public const string Transactions = "postgresql.transactions.single-writer";
    public const string Faults = "postgresql.faults";

    public const string Sql = "postgresql.sql";
    public const string ClientCompatibility = "postgresql.client-compatibility";
    public const string ConcurrentTransactions = "postgresql.transactions.concurrent";
    public const string IsolationLevels = "postgresql.transactions.isolation-levels";
    public const string Savepoints = "postgresql.transactions.savepoints";
    public const string CompositeKeys = "postgresql.constraints.composite-keys";
    public const string TablesWithoutPrimaryKey = "postgresql.schema.tables-without-primary-key";
    public const string PrimaryKeyUpdate = "postgresql.rows.primary-key-update";
    public const string QuotedIdentifiers = "postgresql.schema.quoted-identifiers";
    public const string ColumnDefaults = "postgresql.schema.column-defaults";
    public const string RuntimeDdl = "postgresql.schema.runtime-ddl";
    public const string Sequences = "postgresql.sequences";
    public const string OtherTypes = "postgresql.types.other";
    public const string Queries = "postgresql.queries";
    public const string OtherConstraints = "postgresql.constraints.other";
    public const string LocksAndMvcc = "postgresql.locks-mvcc";
}

/// <summary>Machine-readable capability manifest of <see cref="SimulatedPostgreSqlDatabase"/>.</summary>
public static class PostgreSqlCapabilities
{
    public static CapabilityManifest Manifest { get; } = new(
        SimulatedPostgreSqlDatabase.ProviderName,
        "PostgreSQL-oriented application-contract storage simulation",
        [
            new Capability
            {
                Id = PostgreSqlCapabilityIds.SchemaTables,
                Status = CapabilityStatus.SimulatedOnly,
                Level = CompatibilityLevel.ApplicationContract,
                Summary = "Named tables with declared columns, fixed when the database is added to an environment.",
                Operations = ["AddPostgreSqlDatabase", "DatabaseSchemaBuilder.Table", "TableBuilder.Column"],
                Boundaries =
                [
                    "Table and column names are lowercase unquoted identifiers matching [a-z_][a-z0-9_]* with at most 63 characters.",
                    "Every table has exactly one primary-key column.",
                    "There are no PostgreSQL schemas (namespaces); table names are unqualified.",
                ],
                Deviations = ["Identifiers longer than 63 characters are rejected; PostgreSQL truncates them with a notice."],
            },
            new Capability
            {
                Id = PostgreSqlCapabilityIds.ScalarTypes,
                Status = CapabilityStatus.SimulatedOnly,
                Level = CompatibilityLevel.ServiceSemantics,
                Summary = "A small declared scalar type set with exact CLR type matching.",
                DataTypes =
                [
                    "boolean <-> bool",
                    "integer <-> int",
                    "bigint <-> long",
                    "numeric <-> decimal",
                    "text <-> string",
                    "uuid <-> Guid",
                    "timestamptz <-> DateTimeOffset (stored and returned in UTC)",
                    "bytea <-> byte[] (copied on write and on read)",
                ],
                Boundaries =
                [
                    "Values must have exactly the declared CLR type; no implicit conversions (for example int to bigint) are applied.",
                    "SQL NULL is represented by null; DBNull and DateTime values are rejected with datatype_mismatch.",
                    "Equality: numeric compares by value (1.0 equals 1.00, scale is preserved); timestamptz compares instants; text and bytea compare exactly.",
                ],
                Deviations =
                [
                    "numeric is limited to the System.Decimal range and 28-29 significant digits; NaN and infinities are not representable.",
                    "timestamptz keeps 100 ns precision; PostgreSQL rounds to microseconds.",
                    "The timestamptz range is limited to DateTimeOffset (years 1-9999).",
                    "text equality and ordering are ordinal UTF-16; PostgreSQL ordering depends on the collation (no collation emulation).",
                    "text containing NUL or unpaired surrogates is rejected with character_not_in_repertoire.",
                ],
            },
            new Capability
            {
                Id = PostgreSqlCapabilityIds.CrudByKey,
                Status = CapabilityStatus.SimulatedOnly,
                Level = CompatibilityLevel.ApplicationContract,
                Summary = "Insert, read by key, update by key, and delete by key, in autocommit mode or inside a transaction.",
                Operations = ["Insert", "Get", "Update", "Delete"],
                Boundaries =
                [
                    "Insert: omitted columns are NULL; identifiers are assigned by the application.",
                    "Get: returns null when the key is absent.",
                    "Update and Delete return false when no row has the key (zero rows affected, no error).",
                    "An update must change at least one column.",
                    "Rows are immutable snapshots; mutating inputs or returned byte arrays never changes stored state.",
                ],
            },
            new Capability
            {
                Id = PostgreSqlCapabilityIds.Scan,
                Status = CapabilityStatus.SimulatedOnly,
                Level = CompatibilityLevel.ApplicationContract,
                Summary = "Deterministic full-table scan ordered by primary key.",
                Operations = ["Scan"],
                Assumptions =
                [
                    "The order is ascending primary key: numeric order for numbers, ordinal UTF-16 for text, unsigned byte order for bytea, RFC 4122 byte order for uuid, false before true for boolean.",
                    "Filtering in caller code is plain LINQ-to-Objects and is not a simulation of SQL predicates.",
                ],
            },
            new Capability
            {
                Id = PostgreSqlCapabilityIds.PrimaryKey,
                Status = CapabilityStatus.SimulatedOnly,
                Level = CompatibilityLevel.ServiceSemantics,
                Summary = "Single-column primary keys are NOT NULL and unique.",
                Boundaries = ["A duplicate key fails with unique_violation (constraint '<table>_pkey'); a NULL key fails with not_null_violation."],
            },
            new Capability
            {
                Id = PostgreSqlCapabilityIds.NotNull,
                Status = CapabilityStatus.SimulatedOnly,
                Level = CompatibilityLevel.ServiceSemantics,
                Summary = "NOT NULL columns reject NULL on insert and update.",
                Boundaries = ["Violations fail with not_null_violation before any state changes."],
            },
            new Capability
            {
                Id = PostgreSqlCapabilityIds.Unique,
                Status = CapabilityStatus.SimulatedOnly,
                Level = CompatibilityLevel.ServiceSemantics,
                Summary = "Single-column UNIQUE constraints with PostgreSQL's default NULLS DISTINCT behavior.",
                Boundaries =
                [
                    "Any number of rows may hold NULL in a unique column.",
                    "A duplicate non-NULL value fails with unique_violation (constraint '<table>_<column>_key').",
                    "Constraints are checked when each statement runs; there are no deferred constraints.",
                ],
                Deviations = ["Error messages omit the conflicting value so that test data is not copied into logs."],
            },
            new Capability
            {
                Id = PostgreSqlCapabilityIds.Transactions,
                Status = CapabilityStatus.SimulatedOnly,
                Level = CompatibilityLevel.ServiceSemantics,
                Summary = "Restricted single-writer transactions with atomic multi-table commit and rollback. This is not a PostgreSQL isolation level.",
                Operations = ["BeginTransaction", "Commit", "Rollback", "Dispose"],
                Boundaries =
                [
                    "At most one transaction is active per database; BeginTransaction and autocommit writes are rejected while one is active.",
                    "A transaction reads its own writes; uncommitted writes are never visible to reads outside it.",
                    "Commit re-validates the touched rows of the candidate state, then makes all changed tables visible in one step.",
                    "A statement error aborts the transaction: later statements and Commit fail with in_failed_sql_transaction until rollback or disposal.",
                    "Rollback and disposal discard all uncommitted changes.",
                ],
                Deviations =
                [
                    "Commit of an aborted transaction throws in_failed_sql_transaction (and rolls back); PostgreSQL reports ROLLBACK without an error.",
                    "Overlapping transactions are rejected instead of running under MVCC isolation.",
                ],
                Assumptions = ["No other writer can commit while a transaction is active, so a transaction observes the state committed at its start plus its own writes."],
            },
            new Capability
            {
                Id = PostgreSqlCapabilityIds.Faults,
                Status = CapabilityStatus.SimulatedOnly,
                Level = CompatibilityLevel.ApplicationContract,
                Summary = "Declared fault points for one-shot injected failures.",
                Operations =
                [
                    "begin:before",
                    "get:before",
                    "scan:before",
                    "insert:before",
                    "update:before",
                    "delete:before",
                    "commit:before (nothing becomes visible; the transaction is rolled back)",
                    "commit:after (changes are visible, but the caller receives an error: an ambiguous outcome)",
                ],
                Boundaries =
                [
                    "Statement faults are evaluated after argument validation and abort an active transaction.",
                    "Commit faults apply to explicit transactions only; autocommit statements evaluate only their statement fault point.",
                ],
            },
            Unsupported(PostgreSqlCapabilityIds.Sql, "SQL text parsing and execution, including parameterized SQL."),
            Unsupported(PostgreSqlCapabilityIds.ClientCompatibility, "Unchanged Npgsql, EF Core, or other drivers; connection strings; the PostgreSQL wire protocol.", CompatibilityLevel.WireProtocol),
            Unsupported(PostgreSqlCapabilityIds.ConcurrentTransactions, "More than one active transaction per database, or autocommit writes during a transaction."),
            Unsupported(PostgreSqlCapabilityIds.IsolationLevels, "READ COMMITTED, REPEATABLE READ, and SERIALIZABLE semantics."),
            Unsupported(PostgreSqlCapabilityIds.Savepoints, "SAVEPOINT, RELEASE, and ROLLBACK TO SAVEPOINT."),
            Unsupported(PostgreSqlCapabilityIds.CompositeKeys, "Primary keys spanning more than one column."),
            Unsupported(PostgreSqlCapabilityIds.TablesWithoutPrimaryKey, "Tables without a primary key."),
            Unsupported(PostgreSqlCapabilityIds.PrimaryKeyUpdate, "Changing a row's primary-key value with an update."),
            Unsupported(PostgreSqlCapabilityIds.QuotedIdentifiers, "Quoted, mixed-case, or over-length identifiers."),
            Unsupported(PostgreSqlCapabilityIds.ColumnDefaults, "Column DEFAULT expressions and generated columns."),
            Unsupported(PostgreSqlCapabilityIds.RuntimeDdl, "Creating, altering, or dropping tables after the database is added; migrations."),
            Unsupported(PostgreSqlCapabilityIds.Sequences, "Sequences, serial and identity columns, and implicit key generation."),
            Unsupported(PostgreSqlCapabilityIds.OtherTypes, "json/jsonb, arrays, enums, ranges, date/time types other than timestamptz, and other PostgreSQL types."),
            Unsupported(PostgreSqlCapabilityIds.Queries, "Joins, predicates, secondary indexes, ORDER BY other than the primary key, aggregation, and LINQ translation."),
            Unsupported(PostgreSqlCapabilityIds.OtherConstraints, "Foreign keys, CHECK, multi-column UNIQUE, exclusion constraints, and NULLS NOT DISTINCT."),
            Unsupported(PostgreSqlCapabilityIds.LocksAndMvcc, "Row and table locks, SELECT FOR UPDATE, MVCC snapshots, and deadlock detection."),
        ]);

    private static Capability Unsupported(string id, string summary, CompatibilityLevel level = CompatibilityLevel.ServiceSemantics) => new()
    {
        Id = id,
        Status = CapabilityStatus.Unsupported,
        Level = level,
        Summary = summary,
    };
}

/// <summary>Registers PostgreSQL-oriented storage simulations with an environment.</summary>
public static class PostgreSqlEnvironmentExtensions
{
    extension(SimulationEnvironment environment)
    {
        /// <summary>
        /// Adds an in-memory, PostgreSQL-oriented application-contract storage simulation with a fixed schema. It is not a
        /// SQL engine and cannot be used with Npgsql or EF Core.
        /// </summary>
        public SimulatedPostgreSqlDatabase AddPostgreSqlDatabase(string name, Action<DatabaseSchemaBuilder> configureSchema)
        {
            ArgumentNullException.ThrowIfNull(environment);
            ArgumentNullException.ThrowIfNull(configureSchema);
            var builder = new DatabaseSchemaBuilder();
            configureSchema(builder);
            return environment.AddResource(new SimulatedPostgreSqlDatabase(environment, name, builder.Build()));
        }
    }
}
