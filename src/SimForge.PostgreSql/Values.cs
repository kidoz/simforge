using System.Globalization;

namespace SimForge.PostgreSql;

/// <summary>SimForge error codes for the PostgreSQL-oriented model. The names follow PostgreSQL condition names, but no SQLSTATE parity is claimed.</summary>
public static class PostgreSqlErrorCodes
{
    public const string UniqueViolation = "unique_violation";
    public const string NotNullViolation = "not_null_violation";
    public const string UndefinedTable = "undefined_table";
    public const string UndefinedColumn = "undefined_column";
    public const string DatatypeMismatch = "datatype_mismatch";
    public const string CharacterNotInRepertoire = "character_not_in_repertoire";
    public const string InFailedTransaction = "in_failed_sql_transaction";
}

/// <summary>An error reported by the simulated database. Inside a transaction it aborts the transaction.</summary>
public sealed class PostgreSqlStorageException : SimulatedServiceException
{
    public PostgreSqlStorageException(string resource, string errorCode, string message, string? table = null, string? column = null, string? constraintName = null, Exception? innerException = null)
        : base(SimulatedPostgreSqlDatabase.ProviderName, resource, errorCode, message, innerException)
    {
        Table = table;
        Column = column;
        ConstraintName = constraintName;
    }

    public string? Table { get; }

    public string? Column { get; }

    /// <summary>For unique violations, <c>{table}_pkey</c> or <c>{table}_{column}_key</c>.</summary>
    public string? ConstraintName { get; }
}

/// <summary>Normalized, non-null stored value with the equality and ordering rules defined for its column type.</summary>
internal readonly struct Key : IEquatable<Key>, IComparable<Key>
{
    public Key(object value)
    {
        Value = value;
    }

    public object Value { get; }

    public bool Equals(Key other) => Value switch
    {
        byte[] bytes => other.Value is byte[] otherBytes && bytes.AsSpan().SequenceEqual(otherBytes),
        string text => other.Value is string otherText && string.Equals(text, otherText, StringComparison.Ordinal),
        _ => Value.Equals(other.Value),
    };

    public override bool Equals(object? obj) => obj is Key other && Equals(other);

    public override int GetHashCode()
    {
        if (Value is byte[] bytes)
        {
            var hash = new HashCode();
            hash.AddBytes(bytes);
            return hash.ToHashCode();
        }

        return Value is string text ? StringComparer.Ordinal.GetHashCode(text) : Value.GetHashCode();
    }

    public int CompareTo(Key other) => Value switch
    {
        byte[] bytes => bytes.AsSpan().SequenceCompareTo((byte[])other.Value),
        string text => string.CompareOrdinal(text, (string)other.Value),
        IComparable comparable => comparable.CompareTo(other.Value),
        _ => throw new SimForgeInternalException($"Values of type {Value.GetType().Name} are not orderable."),
    };
}

internal static class PostgreSqlValues
{
    public static Type ClrType(ColumnType type) => type switch
    {
        ColumnType.Boolean => typeof(bool),
        ColumnType.Integer => typeof(int),
        ColumnType.BigInt => typeof(long),
        ColumnType.Numeric => typeof(decimal),
        ColumnType.Text => typeof(string),
        ColumnType.Uuid => typeof(Guid),
        ColumnType.TimestampTz => typeof(DateTimeOffset),
        ColumnType.Bytea => typeof(byte[]),
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
    };

    /// <summary>Validates <paramref name="value"/> for <paramref name="column"/> and returns the stored representation, never sharing mutable input.</summary>
    public static object? Normalize(string resource, TableDefinition table, ColumnDefinition column, object? value)
    {
        switch (value)
        {
            case null:
                return null;
            case bool when column.Type == ColumnType.Boolean:
            case int when column.Type == ColumnType.Integer:
            case long when column.Type == ColumnType.BigInt:
            case decimal when column.Type == ColumnType.Numeric:
            case Guid when column.Type == ColumnType.Uuid:
                return value;
            case string text when column.Type == ColumnType.Text:
                ValidateText(resource, table, column, text);
                return text;
            case DateTimeOffset instant when column.Type == ColumnType.TimestampTz:
                return instant.ToUniversalTime();
            case byte[] bytes when column.Type == ColumnType.Bytea:
                return bytes.ToArray();
            default:
                var hint = value is DBNull ? " Use null for SQL NULL." : value is DateTime ? " Use DateTimeOffset; DateTime kinds are ambiguous." : string.Empty;
                throw new PostgreSqlStorageException(
                    resource,
                    PostgreSqlErrorCodes.DatatypeMismatch,
                    $"Column '{table.Name}.{column.Name}' is {column.Type} ({ClrType(column.Type).Name}) but the value is {value.GetType().Name}.{hint}",
                    table.Name,
                    column.Name);
        }
    }

    /// <summary>Returns a value safe to hand to callers: byte arrays are copied, and all other stored values are immutable.</summary>
    public static object? CopyOut(object? stored) => stored is byte[] bytes ? bytes.ToArray() : stored;

    public static string Format(object? value) => value switch
    {
        null => "NULL",
        string text => $"'{text}'",
        byte[] bytes => $"'\\x{Convert.ToHexStringLower(bytes)}'",
        bool boolean => boolean ? "true" : "false",
        DateTimeOffset instant => instant.ToString("O", CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    private static void ValidateText(string resource, TableDefinition table, ColumnDefinition column, string text)
    {
        if (text.Contains('\0', StringComparison.Ordinal))
        {
            throw new PostgreSqlStorageException(
                resource,
                PostgreSqlErrorCodes.CharacterNotInRepertoire,
                $"Column '{table.Name}.{column.Name}': text values cannot contain NUL (U+0000) characters.",
                table.Name,
                column.Name);
        }

        if (!IsWellFormed(text))
        {
            throw new PostgreSqlStorageException(
                resource,
                PostgreSqlErrorCodes.CharacterNotInRepertoire,
                $"Column '{table.Name}.{column.Name}': text values must be well-formed UTF-16 (unpaired surrogates cannot be encoded as UTF-8).",
                table.Name,
                column.Name);
        }
    }

    private static bool IsWellFormed(string text)
    {
        for (var index = 0; index < text.Length; index++)
        {
            if (char.IsHighSurrogate(text[index]))
            {
                if (index + 1 >= text.Length || !char.IsLowSurrogate(text[index + 1]))
                {
                    return false;
                }

                index++;
            }
            else if (char.IsLowSurrogate(text[index]))
            {
                return false;
            }
        }

        return true;
    }
}
