using System.Collections.Immutable;

namespace SimForge.PostgreSql;

/// <summary>
/// Declared scalar column types, with their CLR representations. The mapping is intentionally narrow and does not
/// cover every PostgreSQL value (see the capability manifest for deviations).
/// </summary>
public enum ColumnType
{
    /// <summary><c>boolean</c> ↔ <see cref="bool"/>.</summary>
    Boolean,

    /// <summary><c>integer</c> ↔ <see cref="int"/>.</summary>
    Integer,

    /// <summary><c>bigint</c> ↔ <see cref="long"/>.</summary>
    BigInt,

    /// <summary><c>numeric</c> ↔ <see cref="decimal"/> (limited to the System.Decimal range and scale).</summary>
    Numeric,

    /// <summary><c>text</c> ↔ <see cref="string"/> (compared ordinally; NUL characters and unpaired surrogates are rejected).</summary>
    Text,

    /// <summary><c>uuid</c> ↔ <see cref="Guid"/>.</summary>
    Uuid,

    /// <summary><c>timestamp with time zone</c> ↔ <see cref="DateTimeOffset"/>, stored and returned in UTC.</summary>
    TimestampTz,

    /// <summary><c>bytea</c> ↔ <see cref="byte"/>[] (copied on write and on read).</summary>
    Bytea,
}

/// <summary>A declared column.</summary>
public sealed class ColumnDefinition
{
    internal ColumnDefinition(string name, ColumnType type, bool primaryKey, bool notNull, bool unique, int ordinal)
    {
        Name = name;
        Type = type;
        IsPrimaryKey = primaryKey;
        IsNotNull = notNull || primaryKey;
        IsUnique = unique || primaryKey;
        Ordinal = ordinal;
    }

    public string Name { get; }

    public ColumnType Type { get; }

    public bool IsPrimaryKey { get; }

    /// <summary>True for NOT NULL columns, including the primary key.</summary>
    public bool IsNotNull { get; }

    /// <summary>True for UNIQUE columns, including the primary key. NULL values never conflict with each other.</summary>
    public bool IsUnique { get; }

    public int Ordinal { get; }

    public override string ToString() => $"{Name} {Type}{(IsPrimaryKey ? " PRIMARY KEY" : string.Empty)}{(IsNotNull && !IsPrimaryKey ? " NOT NULL" : string.Empty)}{(IsUnique && !IsPrimaryKey ? " UNIQUE" : string.Empty)}";
}

/// <summary>A declared table with exactly one primary-key column.</summary>
public sealed class TableDefinition
{
    private readonly Dictionary<string, ColumnDefinition> _byName;

    internal TableDefinition(string name, ImmutableArray<ColumnDefinition> columns)
    {
        Name = name;
        Columns = columns;
        _byName = columns.ToDictionary(column => column.Name, StringComparer.Ordinal);
        PrimaryKey = columns.Single(column => column.IsPrimaryKey);
    }

    public string Name { get; }

    /// <summary>Columns in declaration order.</summary>
    public ImmutableArray<ColumnDefinition> Columns { get; }

    public ColumnDefinition PrimaryKey { get; }

    public bool TryGetColumn(string name, out ColumnDefinition column) => _byName.TryGetValue(name, out column!);
}

/// <summary>An immutable set of declared tables.</summary>
public sealed class DatabaseSchema
{
    private readonly Dictionary<string, TableDefinition> _byName;

    internal DatabaseSchema(ImmutableArray<TableDefinition> tables)
    {
        Tables = tables;
        _byName = tables.ToDictionary(table => table.Name, StringComparer.Ordinal);
    }

    public ImmutableArray<TableDefinition> Tables { get; }

    public bool TryGetTable(string name, out TableDefinition table) => _byName.TryGetValue(name, out table!);
}

/// <summary>Declares the tables of a simulated database. The schema is fixed once the database is added; runtime DDL is not supported.</summary>
public sealed class DatabaseSchemaBuilder
{
    private readonly List<TableDefinition> _tables = [];

    public DatabaseSchemaBuilder Table(string name, Action<TableBuilder> configure)
    {
        Identifiers.Validate(name, "table");
        ArgumentNullException.ThrowIfNull(configure);
        if (_tables.Exists(table => table.Name == name))
        {
            throw new ArgumentException($"Table '{name}' is declared twice.", nameof(name));
        }

        var builder = new TableBuilder(name);
        configure(builder);
        _tables.Add(builder.Build());
        return this;
    }

    internal DatabaseSchema Build()
    {
        if (_tables.Count == 0)
        {
            throw new ArgumentException("A simulated database needs at least one table.");
        }

        return new DatabaseSchema([.. _tables]);
    }
}

/// <summary>Declares the columns of one table.</summary>
public sealed class TableBuilder
{
    private readonly string _table;
    private readonly List<ColumnDefinition> _columns = [];

    internal TableBuilder(string table)
    {
        _table = table;
    }

    /// <summary>
    /// Declares a column. Exactly one column per table must be the primary key; composite keys, defaults, foreign keys,
    /// CHECK constraints, and multi-column UNIQUE constraints are not supported.
    /// </summary>
    public TableBuilder Column(string name, ColumnType type, bool primaryKey = false, bool notNull = false, bool unique = false)
    {
        Identifiers.Validate(name, "column");
        if (!Enum.IsDefined(type))
        {
            throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown column type.");
        }

        if (_columns.Exists(column => column.Name == name))
        {
            throw new ArgumentException($"Column '{_table}.{name}' is declared twice.", nameof(name));
        }

        if (primaryKey && _columns.Exists(column => column.IsPrimaryKey))
        {
            throw new UnsupportedCapabilityException(
                PostgreSqlCapabilityIds.CompositeKeys,
                $"Table '{_table}' already has a primary-key column; composite primary keys are not supported");
        }

        _columns.Add(new ColumnDefinition(name, type, primaryKey, notNull, unique, _columns.Count));
        return this;
    }

    internal TableDefinition Build()
    {
        if (!_columns.Exists(column => column.IsPrimaryKey))
        {
            throw new UnsupportedCapabilityException(
                PostgreSqlCapabilityIds.TablesWithoutPrimaryKey,
                $"Table '{_table}' declares no primary-key column; every simulated table is key-addressed");
        }

        return new TableDefinition(_table, [.. _columns]);
    }
}

internal static class Identifiers
{
    // PostgreSQL's default identifier length limit is NAMEDATALEN - 1 = 63 bytes.
    private const int MaxLength = 63;

    public static void Validate(string name, string kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var lowercase = (name[0] is >= 'a' and <= 'z' or '_') &&
                        name.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '_');
        if (!lowercase)
        {
            throw new UnsupportedCapabilityException(
                PostgreSqlCapabilityIds.QuotedIdentifiers,
                $"The {kind} name '{name}' is not a lowercase unquoted identifier ([a-z_][a-z0-9_]*); quoted and mixed-case identifiers are not supported");
        }

        if (name.Length > MaxLength)
        {
            throw new UnsupportedCapabilityException(
                PostgreSqlCapabilityIds.QuotedIdentifiers,
                $"The {kind} name '{name}' exceeds {MaxLength} characters; PostgreSQL would truncate it, SimForge rejects it");
        }
    }
}
