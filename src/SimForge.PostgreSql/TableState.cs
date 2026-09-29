using System.Collections.Immutable;

namespace SimForge.PostgreSql;

/// <summary>
/// Immutable state of one table: rows ordered by primary key, plus a value-to-key index for each unique non-key column.
/// Every change produces a new instance, so a transaction's candidate state never affects committed state until it is
/// published.
/// </summary>
internal sealed class TableState
{
    private TableState(TableDefinition definition, ImmutableSortedDictionary<Key, Row> rows, ImmutableArray<ImmutableDictionary<Key, Key>?> uniqueIndexes)
    {
        Definition = definition;
        Rows = rows;
        UniqueIndexes = uniqueIndexes;
    }

    public TableDefinition Definition { get; }

    public ImmutableSortedDictionary<Key, Row> Rows { get; }

    /// <summary>Aligned with column ordinals; null for the primary key and for non-unique columns.</summary>
    public ImmutableArray<ImmutableDictionary<Key, Key>?> UniqueIndexes { get; }

    public static TableState Empty(TableDefinition definition) => new(
        definition,
        ImmutableSortedDictionary<Key, Row>.Empty,
        [.. definition.Columns.Select(column => column.IsUnique && !column.IsPrimaryKey ? ImmutableDictionary<Key, Key>.Empty : null)]);

    public Row? Find(Key key) => Rows.TryGetValue(key, out var row) ? row : null;

    public TableState Insert(string resource, Row row)
    {
        CheckNotNull(resource, row);
        var key = row.PrimaryKey;
        if (Rows.ContainsKey(key))
        {
            throw UniqueViolation(resource, Definition.PrimaryKey, $"{Definition.Name}_pkey");
        }

        var indexes = UniqueIndexes.ToBuilder();
        foreach (var column in Definition.Columns)
        {
            if (indexes[column.Ordinal] is not { } index || row.GetStored(column.Ordinal) is not { } value)
            {
                continue;
            }

            var valueKey = new Key(value);
            if (index.ContainsKey(valueKey))
            {
                throw UniqueViolation(resource, column, $"{Definition.Name}_{column.Name}_key");
            }

            indexes[column.Ordinal] = index.Add(valueKey, key);
        }

        return new TableState(Definition, Rows.Add(key, row), indexes.ToImmutable());
    }

    public TableState Update(string resource, Row current, Row updated)
    {
        CheckNotNull(resource, updated);
        var key = current.PrimaryKey;
        var indexes = UniqueIndexes.ToBuilder();
        foreach (var column in Definition.Columns)
        {
            if (indexes[column.Ordinal] is not { } index)
            {
                continue;
            }

            var before = current.GetStored(column.Ordinal);
            var after = updated.GetStored(column.Ordinal);
            if (before is not null && after is not null && new Key(before).Equals(new Key(after)))
            {
                continue;
            }

            if (before is not null)
            {
                index = index.Remove(new Key(before));
            }

            if (after is not null)
            {
                var valueKey = new Key(after);
                if (index.ContainsKey(valueKey))
                {
                    throw UniqueViolation(resource, column, $"{Definition.Name}_{column.Name}_key");
                }

                index = index.Add(valueKey, key);
            }

            indexes[column.Ordinal] = index;
        }

        return new TableState(Definition, Rows.SetItem(key, updated), indexes.ToImmutable());
    }

    public TableState Delete(Row current)
    {
        var indexes = UniqueIndexes.ToBuilder();
        foreach (var column in Definition.Columns)
        {
            if (indexes[column.Ordinal] is { } index && current.GetStored(column.Ordinal) is { } value)
            {
                indexes[column.Ordinal] = index.Remove(new Key(value));
            }
        }

        return new TableState(Definition, Rows.Remove(current.PrimaryKey), indexes.ToImmutable());
    }

    /// <summary>
    /// Re-checks the constraints of the rows a transaction touched against this candidate state. The check runs before
    /// the candidate is made visible, independently of the checks each statement already made.
    /// </summary>
    public void ValidateTouched(string resource, IEnumerable<Key> touchedKeys)
    {
        foreach (var key in touchedKeys)
        {
            if (Find(key) is not { } row)
            {
                continue;
            }

            CheckNotNull(resource, row);
            foreach (var column in Definition.Columns)
            {
                if (UniqueIndexes[column.Ordinal] is not { } index || row.GetStored(column.Ordinal) is not { } value)
                {
                    continue;
                }

                if (!index.TryGetValue(new Key(value), out var owner) || !owner.Equals(key))
                {
                    throw new SimForgeInternalException(
                        $"Candidate state of table '{Definition.Name}' has an inconsistent unique index for column '{column.Name}'.");
                }
            }
        }
    }

    private void CheckNotNull(string resource, Row row)
    {
        foreach (var column in Definition.Columns)
        {
            if (column.IsNotNull && row.GetStored(column.Ordinal) is null)
            {
                throw new PostgreSqlStorageException(
                    resource,
                    PostgreSqlErrorCodes.NotNullViolation,
                    $"Column '{Definition.Name}.{column.Name}' is NOT NULL but the row has NULL.",
                    Definition.Name,
                    column.Name);
            }
        }
    }

    // The conflicting value is deliberately left out of the message so that test data is not copied into logs.
    private PostgreSqlStorageException UniqueViolation(string resource, ColumnDefinition column, string constraint) =>
        new(
            resource,
            PostgreSqlErrorCodes.UniqueViolation,
            $"Duplicate value for unique constraint '{constraint}' on '{Definition.Name}.{column.Name}' (a {column.Type} value).",
            Definition.Name,
            column.Name,
            constraint);
}
