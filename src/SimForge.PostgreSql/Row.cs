using System.Collections;
using System.Diagnostics.CodeAnalysis;

namespace SimForge.PostgreSql;

/// <summary>
/// Immutable row snapshot. Every column of the table is present, and omitted columns are NULL. Reads return copies of
/// mutable values (<see cref="ColumnType.Bytea"/>), so mutating a returned value never changes stored state.
/// </summary>
public sealed class Row : IReadOnlyDictionary<string, object?>
{
    private readonly object?[] _values;

    internal Row(TableDefinition table, object?[] values)
    {
        Definition = table;
        _values = values;
    }

    public string Table => Definition.Name;

    public TableDefinition Definition { get; }

    public int Count => _values.Length;

    public IEnumerable<string> Keys => Definition.Columns.Select(column => column.Name);

    public IEnumerable<object?> Values => _values.Select(PostgreSqlValues.CopyOut);

    internal Key PrimaryKey => new(_values[Definition.PrimaryKey.Ordinal]!);

    /// <summary>Returns the column value (null for SQL NULL). Byte arrays are copies.</summary>
    public object? this[string key] => PostgreSqlValues.CopyOut(_values[Ordinal(key)]);

    /// <summary>
    /// Returns the value as <typeparamref name="T"/>. NULL is returned as null only for <see cref="Nullable{T}"/> targets
    /// such as <c>Get&lt;long?&gt;</c>; any other target throws on NULL. For nullable reference columns, use
    /// <see cref="IsNull"/> or the indexer.
    /// </summary>
    public T Get<T>(string column)
    {
        var value = this[column];
        if (value is null)
        {
            return Nullable.GetUnderlyingType(typeof(T)) is not null
                ? default!
                : throw new InvalidOperationException($"Column '{Table}.{column}' is NULL. Use IsNull, the indexer, or a Nullable<T> target.");
        }

        return value is T typed
            ? typed
            : throw new InvalidCastException($"Column '{Table}.{column}' holds {value.GetType().Name}, not {typeof(T).Name}.");
    }

    public bool IsNull(string column) => _values[Ordinal(column)] is null;

    public bool ContainsKey(string key) => Definition.TryGetColumn(key, out _);

    public bool TryGetValue(string key, [MaybeNullWhen(false)] out object? value)
    {
        if (Definition.TryGetColumn(key, out var column))
        {
            value = PostgreSqlValues.CopyOut(_values[column.Ordinal]);
            return true;
        }

        value = null;
        return false;
    }

    public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() =>
        Definition.Columns.Select(column => KeyValuePair.Create(column.Name, PostgreSqlValues.CopyOut(_values[column.Ordinal]))).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public override string ToString() =>
        $"{Table}({string.Join(", ", Definition.Columns.Select(column => $"{column.Name}={PostgreSqlValues.Format(_values[column.Ordinal])}"))})";

    internal object? GetStored(int ordinal) => _values[ordinal];

    internal Row With(IReadOnlyList<(int Ordinal, object? Value)> changes)
    {
        var values = (object?[])_values.Clone();
        foreach (var (ordinal, value) in changes)
        {
            values[ordinal] = value;
        }

        return new Row(Definition, values);
    }

    private int Ordinal(string column)
    {
        ArgumentNullException.ThrowIfNull(column);
        return Definition.TryGetColumn(column, out var definition)
            ? definition.Ordinal
            : throw new KeyNotFoundException($"Table '{Table}' has no column '{column}'.");
    }
}
