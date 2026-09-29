using Xunit;
using static SimForge.PostgreSql.Tests.TestDatabase;

namespace SimForge.PostgreSql.Tests;

public sealed class SchemaAndValueTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("Orders")]
    [InlineData("order-items")]
    [InlineData("1orders")]
    [InlineData("public.orders")]
    public async Task Quoted_or_mixed_case_identifiers_are_unsupported(string name)
    {
        await using var environment = new SimulationEnvironment();

        var exception = Assert.Throws<UnsupportedCapabilityException>(
            () => environment.AddPostgreSqlDatabase("db", schema => schema.Table(name, table => table.Column("id", ColumnType.BigInt, primaryKey: true))));

        Assert.Equal(PostgreSqlCapabilityIds.QuotedIdentifiers, exception.CapabilityId);
    }

    [Fact]
    public async Task Over_length_identifiers_are_rejected_rather_than_truncated()
    {
        await using var environment = new SimulationEnvironment();
        var name = new string('a', 64);

        Assert.Throws<UnsupportedCapabilityException>(
            () => environment.AddPostgreSqlDatabase("db", schema => schema.Table("t", table => table.Column("id", ColumnType.BigInt, primaryKey: true).Column(name, ColumnType.Text))));
    }

    [Fact]
    public async Task Tables_need_exactly_one_primary_key_column()
    {
        await using var environment = new SimulationEnvironment();

        var missing = Assert.Throws<UnsupportedCapabilityException>(
            () => environment.AddPostgreSqlDatabase("a", schema => schema.Table("t", table => table.Column("value", ColumnType.Text))));
        var composite = Assert.Throws<UnsupportedCapabilityException>(
            () => environment.AddPostgreSqlDatabase("b", schema => schema.Table("t", table => table
                .Column("tenant", ColumnType.BigInt, primaryKey: true)
                .Column("id", ColumnType.BigInt, primaryKey: true))));

        Assert.Equal(PostgreSqlCapabilityIds.TablesWithoutPrimaryKey, missing.CapabilityId);
        Assert.Equal(PostgreSqlCapabilityIds.CompositeKeys, composite.CapabilityId);
        Assert.Empty(environment.Resources);
    }

    [Fact]
    public async Task Duplicate_declarations_and_empty_schemas_are_rejected()
    {
        await using var environment = new SimulationEnvironment();

        Assert.Throws<ArgumentException>(() => environment.AddPostgreSqlDatabase("a", schema => schema
            .Table("t", table => table.Column("id", ColumnType.BigInt, primaryKey: true))
            .Table("t", table => table.Column("id", ColumnType.BigInt, primaryKey: true))));
        Assert.Throws<ArgumentException>(() => environment.AddPostgreSqlDatabase("b", schema => schema
            .Table("t", table => table.Column("id", ColumnType.BigInt, primaryKey: true).Column("id", ColumnType.Text))));
        Assert.Throws<ArgumentException>(() => environment.AddPostgreSqlDatabase("c", _ => { }));
        Assert.Throws<ArgumentOutOfRangeException>(() => environment.AddPostgreSqlDatabase("d", schema => schema
            .Table("t", table => table.Column("id", (ColumnType)99, primaryKey: true))));
    }

    [Fact]
    public async Task Every_declared_type_round_trips_with_its_clr_representation()
    {
        await using var test = await CreateAsync(schema => schema.Table("all_types", table => table
            .Column("id", ColumnType.BigInt, primaryKey: true)
            .Column("flag", ColumnType.Boolean)
            .Column("small", ColumnType.Integer)
            .Column("amount", ColumnType.Numeric)
            .Column("label", ColumnType.Text)
            .Column("reference", ColumnType.Uuid)
            .Column("at", ColumnType.TimestampTz)
            .Column("blob", ColumnType.Bytea)), cancellationToken: Token);
        var reference = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");
        var at = new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.FromHours(3)).AddTicks(1234567);

        test.Database.Insert("all_types", new Dictionary<string, object?>
        {
            ["id"] = 1L,
            ["flag"] = true,
            ["small"] = -7,
            ["amount"] = 12.50m,
            ["label"] = "naïve 🚀",
            ["reference"] = reference,
            ["at"] = at,
            ["blob"] = new byte[] { 0, 255, 16 },
        });
        test.Database.Insert("all_types", new Dictionary<string, object?> { ["id"] = 2L });

        var row = test.Database.Get("all_types", 1L)!;
        Assert.True(row.Get<bool>("flag"));
        Assert.Equal(-7, row.Get<int>("small"));
        Assert.Equal("12.50", row.Get<decimal>("amount").ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal("naïve 🚀", row.Get<string>("label"));
        Assert.Equal(reference, row.Get<Guid>("reference"));
        Assert.Equal(at, row.Get<DateTimeOffset>("at"));
        Assert.Equal(TimeSpan.Zero, row.Get<DateTimeOffset>("at").Offset);
        Assert.Equal([0, 255, 16], row.Get<byte[]>("blob"));

        var empty = test.Database.Get("all_types", 2L)!;
        Assert.All(["flag", "small", "amount", "label", "reference", "at", "blob"], column => Assert.True(empty.IsNull(column)));
        Assert.Null(empty.Get<int?>("small"));
        Assert.Throws<InvalidOperationException>(() => empty.Get<int>("small"));
        Assert.Throws<InvalidCastException>(() => row.Get<long>("small"));
        Assert.Throws<KeyNotFoundException>(() => row["missing"]);
    }

    [Theory]
    [InlineData("total", 10)]
    [InlineData("total", 10.0)]
    [InlineData("customer", 'c')]
    public async Task Values_must_have_exactly_the_declared_clr_type(string column, object value)
    {
        await using var test = await CreateAsync(cancellationToken: Token);
        var values = Order(Guid.NewGuid());
        values[column] = value;

        var exception = Assert.Throws<PostgreSqlStorageException>(() => test.Database.Insert("orders", values));

        Assert.Equal(PostgreSqlErrorCodes.DatatypeMismatch, exception.ErrorCode);
        Assert.Equal(column, exception.Column);
        Assert.Empty(test.Database.Scan("orders"));
    }

    [Fact]
    public async Task DateTime_and_DBNull_are_rejected_with_a_hint()
    {
        await using var test = await CreateAsync(cancellationToken: Token);
        var withDateTime = Order(Guid.NewGuid());
        withDateTime["placed_at"] = DateTime.UtcNow;
        var withDbNull = Order(Guid.NewGuid());
        withDbNull["note"] = DBNull.Value;

        Assert.Contains("DateTimeOffset", Assert.Throws<PostgreSqlStorageException>(() => test.Database.Insert("orders", withDateTime)).Message, StringComparison.Ordinal);
        Assert.Contains("null for SQL NULL", Assert.Throws<PostgreSqlStorageException>(() => test.Database.Insert("orders", withDbNull)).Message, StringComparison.Ordinal);
    }

    // Built in code: attribute arguments are stored as UTF-8 metadata, which would replace unpaired surrogates with U+FFFD.
    public static TheoryData<string, string> UnstorableText => new()
    {
        { "nul", "nul\0inside" },
        { "lone-high-surrogate", "lone " + (char)0xD800 + " surrogate" },
        { "trailing-high-surrogate", "trailing " + (char)0xDBFF },
        { "reversed-pair", "reversed " + (char)0xDC00 + (char)0xD800 + " pair" },
    };

    [Theory]
    [MemberData(nameof(UnstorableText))]
    public async Task Text_that_cannot_be_stored_as_utf8_is_rejected(string name, string text)
    {
        Assert.NotEmpty(name);
        await using var test = await CreateAsync(cancellationToken: Token);

        var exception = Assert.Throws<PostgreSqlStorageException>(() => test.Database.Insert("orders", Order(Guid.NewGuid(), customer: text)));

        Assert.Equal(PostgreSqlErrorCodes.CharacterNotInRepertoire, exception.ErrorCode);
    }

    [Fact]
    public async Task Well_formed_surrogate_pairs_are_accepted()
    {
        await using var test = await CreateAsync(cancellationToken: Token);
        var id = Guid.NewGuid();

        test.Database.Insert("orders", Order(id, customer: "rocket " + char.ConvertFromUtf32(0x1F680)));

        Assert.Equal("rocket 🚀", test.Database.Get("orders", id)!.Get<string>("customer"));
    }

    [Fact]
    public async Task Unknown_tables_and_columns_are_service_errors()
    {
        await using var test = await CreateAsync(cancellationToken: Token);
        var values = Order(Guid.NewGuid());
        values["colour"] = "red";

        Assert.Equal(PostgreSqlErrorCodes.UndefinedTable, Assert.Throws<PostgreSqlStorageException>(() => test.Database.Scan("missing")).ErrorCode);
        Assert.Equal(PostgreSqlErrorCodes.UndefinedColumn, Assert.Throws<PostgreSqlStorageException>(() => test.Database.Insert("orders", values)).ErrorCode);
        Assert.Equal(PostgreSqlErrorCodes.DatatypeMismatch, Assert.Throws<PostgreSqlStorageException>(() => test.Database.Get("orders", "not-a-guid")).ErrorCode);
    }

    [Fact]
    public async Task Scans_are_ordered_by_primary_key_using_the_documented_comparisons()
    {
        await using var test = await CreateAsync(schema => schema
            .Table("texts", table => table.Column("id", ColumnType.Text, primaryKey: true))
            .Table("numbers", table => table.Column("id", ColumnType.Numeric, primaryKey: true))
            .Table("blobs", table => table.Column("id", ColumnType.Bytea, primaryKey: true)), cancellationToken: Token);
        foreach (var text in new[] { "b", "a", "B", "é", "z", "aa" })
        {
            test.Database.Insert("texts", new Dictionary<string, object?> { ["id"] = text });
        }

        foreach (var number in new[] { 10m, -1.5m, 2m, 0m })
        {
            test.Database.Insert("numbers", new Dictionary<string, object?> { ["id"] = number });
        }

        foreach (var blob in new[] { new byte[] { 2 }, [1, 255], [1], [] })
        {
            test.Database.Insert("blobs", new Dictionary<string, object?> { ["id"] = blob });
        }

        Assert.Equal(["B", "a", "aa", "b", "z", "é"], test.Database.Scan("texts").Select(row => row.Get<string>("id")));
        Assert.Equal([-1.5m, 0m, 2m, 10m], test.Database.Scan("numbers").Select(row => row.Get<decimal>("id")));
        Assert.Equal(["", "01", "01FF", "02"], test.Database.Scan("blobs").Select(row => Convert.ToHexString(row.Get<byte[]>("id"))));
    }

    [Fact]
    public async Task Numeric_equality_ignores_scale_but_reads_preserve_it()
    {
        await using var test = await CreateAsync(schema => schema.Table("prices", table => table
            .Column("id", ColumnType.BigInt, primaryKey: true)
            .Column("amount", ColumnType.Numeric, unique: true)), cancellationToken: Token);

        test.Database.Insert("prices", new Dictionary<string, object?> { ["id"] = 1L, ["amount"] = 1.0m });
        var duplicate = Assert.Throws<PostgreSqlStorageException>(
            () => test.Database.Insert("prices", new Dictionary<string, object?> { ["id"] = 2L, ["amount"] = 1.00m }));

        Assert.Equal(PostgreSqlErrorCodes.UniqueViolation, duplicate.ErrorCode);
        Assert.Equal("1.0", test.Database.Get("prices", 1L)!.Get<decimal>("amount").ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task Timestamps_with_different_offsets_are_the_same_instant()
    {
        await using var test = await CreateAsync(schema => schema.Table("events", table => table
            .Column("at", ColumnType.TimestampTz, primaryKey: true)), cancellationToken: Token);
        var utc = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

        test.Database.Insert("events", new Dictionary<string, object?> { ["at"] = utc });

        Assert.NotNull(test.Database.Get("events", utc.ToOffset(TimeSpan.FromHours(5))));
        Assert.Throws<PostgreSqlStorageException>(
            () => test.Database.Insert("events", new Dictionary<string, object?> { ["at"] = utc.ToOffset(TimeSpan.FromHours(-8)) }));
    }
}
