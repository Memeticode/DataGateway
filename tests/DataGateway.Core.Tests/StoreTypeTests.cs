using System.Text.Json;
using System.Text.RegularExpressions;
using DataGateway.Core.Catalog;
using DataGateway.Core.Queries;
using DataGateway.Data.SqlServer;
using Xunit;

namespace DataGateway.Core.Tests;

/// <summary>The catalog columns <c>store_type</c> and <c>store_type_override</c>: stored and
/// migrated, kept as written in the server-side contract for the source's own engine, parsed only by that engine, and
/// never in an answer.</summary>
public sealed class StoreTypeTests
{
    private const string Marker = "store-type-marker-9b1d";

    public static TheoryData<string, string, int?, bool, int?, int?, bool, bool> Readable() => new()
    {
        // text, name, length, max, precision, scale, unicode, fixed
        { "varchar(20)", "varchar", 20, false, null, null, false, false },
        { "VARCHAR(20)", "varchar", 20, false, null, null, false, false },
        { " varchar ( 20 ) ", "varchar", 20, false, null, null, false, false },
        { "varchar(8000)", "varchar", 8000, false, null, null, false, false },
        { "varchar(max)", "varchar", null, true, null, null, false, false },
        { "varchar(MAX)", "varchar", null, true, null, null, false, false },
        { "char(10)", "char", 10, false, null, null, false, true },
        { "nvarchar(4000)", "nvarchar", 4000, false, null, null, true, false },
        { "nvarchar(max)", "nvarchar", null, true, null, null, true, false },
        { "nchar(3)", "nchar", 3, false, null, null, true, true },
        { "decimal(19,4)", "decimal", null, false, 19, 4, false, false },
        { "decimal(19, 4)", "decimal", null, false, 19, 4, false, false },
        { "numeric(38,38)", "numeric", null, false, 38, 38, false, false },
        { "decimal(9)", "decimal", null, false, 9, null, false, false },
        { "decimal", "decimal", null, false, null, null, false, false },
        { "datetime", "datetime", null, false, null, null, false, false },
        { "smalldatetime", "smalldatetime", null, false, null, null, false, false },
        { "date", "date", null, false, null, null, false, false },
        { "datetime2(7)", "datetime2", null, false, 7, null, false, false },
        { "datetime2", "datetime2", null, false, null, null, false, false },
        { "int", "int", null, false, null, null, false, false },
        { "Int", "int", null, false, null, null, false, false },
        { "bigint", "bigint", null, false, null, null, false, false },
        { "smallint", "smallint", null, false, null, null, false, false },
        { "tinyint", "tinyint", null, false, null, null, false, false },
        { "bit", "bit", null, false, null, null, false, false },
        { "float(53)", "float", null, false, 53, null, false, false },
        { "money", "money", null, false, null, null, false, false },
        { "uniqueidentifier", "uniqueidentifier", null, false, null, null, false, false },
        { "ntext", "ntext", null, false, null, null, true, false },
        { "sql_variant", "sql_variant", null, false, null, null, false, false },
    };

    [Theory]
    [MemberData(nameof(Readable))]
    public void T1_readable_store_types_parse_into_their_parts(string text, string name, int? length, bool max,
        int? precision, int? scale, bool unicode, bool fixedLength)
    {
        Assert.True(StoreType.TryParse(text, out var parsed));
        Assert.Equal((name, length, max, precision, scale, unicode, fixedLength, false),
            (parsed.Name, parsed.Length, parsed.IsMax, parsed.Precision, parsed.Scale, parsed.IsUnicode, parsed.IsFixedLength,
                parsed.Utf8Collation));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("varchar")]
    [InlineData("char")]
    [InlineData("nvarchar")]
    [InlineData("varchar(0)")]
    [InlineData("varchar(8001)")]
    [InlineData("nvarchar(4001)")]
    [InlineData("nchar(4001)")]
    [InlineData("char(max)")]
    [InlineData("nchar(max)")]
    [InlineData("varchar(20,2)")]
    [InlineData("varchar(max,2)")]
    [InlineData("varchar(-1)")]
    [InlineData("varchar(20")]
    [InlineData("varchar20)")]
    [InlineData("varchar(20))")]
    [InlineData("varchar(2 0)")]
    [InlineData("varchar(\u0661\u0662)")]
    [InlineData("varchar(20)\n")]
    [InlineData("int(4)")]
    [InlineData("bit(1)")]
    [InlineData("datetime(3)")]
    [InlineData("datetime2(8)")]
    [InlineData("decimal(0)")]
    [InlineData("decimal(39)")]
    [InlineData("decimal(5,6)")]
    [InlineData("decimal(max)")]
    [InlineData("float(54)")]
    [InlineData("integer")]
    [InlineData("character varying(20)")]
    [InlineData("text(10)")]
    [InlineData("varchar(9999999999)")]
    [InlineData("dbo.varchar(20)")]
    [InlineData("varchar(20) COLLATE Latin1_General_100_CI_AS_SC_UTF8")]
    public void T2_unreadable_or_unknown_text_is_not_a_store_type(string? text)
    {
        Assert.False(StoreType.TryParse(text, out var parsed));
        Assert.Null(parsed);
    }

    [Fact]
    public void T3_store_types_are_values_and_only_the_utf8_flag_is_settable()
    {
        Assert.True(StoreType.TryParse("varchar(20)", out var first));
        Assert.True(StoreType.TryParse("VARCHAR ( 20 )", out var second));
        Assert.Equal(first, second);
        var utf8 = first with { Utf8Collation = true };
        Assert.True(utf8.Utf8Collation);
        Assert.False(first.Utf8Collation);
        Assert.NotEqual(first, utf8);
        Assert.Equal((first.Name, first.Length), (utf8.Name, utf8.Length));
        // Every parsed member is get-only, so a with copy cannot make a type the parser would refuse.
        Assert.All(typeof(StoreType).GetProperties().Where(property => property.Name != nameof(StoreType.Utf8Collation)),
            property => Assert.Null(property.SetMethod));
        Assert.Empty(typeof(StoreType).GetConstructors());
    }

    [Fact]
    public void T4_mapping_keeps_each_store_type_as_written_with_its_override_keyed_ignoring_case()
    {
        var contract = Map(Column("code", "text", 1) with { StoreType = "char(4)", StoreTypeOverride = true },
            Column("name", "text", 2) with { StoreType = "nvarchar(40)" },
            Column("qty", "number", 3) with { StoreType = " INT " },
            Column("note", "text", 4) with { StoreTypeOverride = true });
        Assert.Equal(new[] { "code", "name", "qty" }, contract.StoreTypes.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(new CatalogStoreType("char(4)", true), contract.StoreTypes["CODE"]);
        Assert.Equal(new CatalogStoreType("nvarchar(40)", false), contract.StoreTypes["Name"]);
        // Kept verbatim: only the engine of the source's own database reads it.
        Assert.Equal(new CatalogStoreType(" INT ", false), contract.StoreTypes["qty"]);
        Assert.False(contract.StoreTypes.ContainsKey("note"));
        Assert.Throws<NotSupportedException>(() =>
            ((IDictionary<string, CatalogStoreType>)contract.StoreTypes).Add("x", new("int", false)));
    }

    [Theory]
    [InlineData(Marker)]
    [InlineData("")]
    [InlineData("varchar")]
    [InlineData("char(max)")]
    [InlineData("character varying(20)")]
    [InlineData("integer")]
    public void T5_any_store_type_text_maps_without_parsing_or_failing(string text)
    {
        var mapped = CatalogMapping.Map(new(Source(), [Column("code", "text", 1) with { StoreType = text }]));
        Assert.True(mapped.IsSuccess, mapped.Failure?.Message);
        Assert.Equal(new CatalogStoreType(text, false), mapped.Value.StoreTypes["code"]);
        // The contract has nothing to warn with: a PostgreSQL or SQLite source keeps the text and never looks at it.
        Assert.DoesNotContain(typeof(SourceContract).GetProperties(), property => property.Name.Contains("Warning", StringComparison.Ordinal));
    }

    [Fact]
    public void T6_store_type_never_reaches_json_tostring_or_the_describe_answer()
    {
        var row = Column("code", "text", 1) with { StoreType = Marker, StoreTypeOverride = true };
        foreach (var json in new[] { JsonSerializer.Serialize(row), JsonSerializer.Serialize(row, JsonSerializerOptions.Web),
                     JsonSerializer.Serialize(new CatalogRows(Source(), [row]), JsonSerializerOptions.Web) })
        {
            Assert.DoesNotContain(Marker, json, StringComparison.Ordinal);
            Assert.DoesNotContain("tore", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("verride", json, StringComparison.OrdinalIgnoreCase);
        }
        Assert.DoesNotContain(Marker, row.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("StoreType", row.ToString(), StringComparison.Ordinal);

        // The describe answer for a column with a store type is byte-for-byte the answer for the same column without one.
        var limits = QueryLimits.ForRowCap(500);
        var plain = Map(Column("code", "text", 1), Column("qty", "number", 2));
        var typed = Map(Column("code", "text", 1) with { StoreType = "char(4)", StoreTypeOverride = true },
            Column("qty", "number", 2) with { StoreType = "int" });
        Assert.Equal(2, typed.StoreTypes.Count);
        foreach (var options in new[] { JsonSerializerOptions.Default, JsonSerializerOptions.Web })
        {
            var expected = JsonSerializer.SerializeToUtf8Bytes(SourceDescription.Map(plain, limits), options);
            Assert.Equal(expected, JsonSerializer.SerializeToUtf8Bytes(SourceDescription.Map(typed, limits), options));
            Assert.Equal(JsonSerializer.SerializeToUtf8Bytes(plain, options), JsonSerializer.SerializeToUtf8Bytes(typed, options));
            Assert.DoesNotContain("char(4)", System.Text.Encoding.UTF8.GetString(expected), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void T7_sql_server_compile_records_each_value_column_through_nested_groups_and_attaches_store_types_later()
    {
        const string query = """{"select":["code"],"filters":[{"or":[{"col":"code","op":"eq","value":"AB"},{"and":[{"col":"qty","op":"gte","value":3},{"col":"name","op":"contains","value":"x_"}]}]},{"col":"day","op":"between","value":["2024-01-01","2024-02-01"]},{"col":"note","op":"eq","value":"n"},{"col":"code","op":"like","value":"A%"}],"limit":2}""";
        var compiled = new SqlServerEngine().Compile(Validate(Typed(), query));
        Assert.Equal("SELECT TOP (@p0) [code] FROM [dbo].[synthetic_view] WHERE (([code] = @p1) OR (([qty] >= @p2) AND ([name] like @p3 ESCAPE '\\'))) " +
            "AND [day] BETWEEN @p4 AND @p5 AND [note] = @p6 AND [code] like @p7", compiled.Sql);
        Assert.Equal(new object[] { 3, "AB", 3m, "%x\\_%", new DateTime(2024, 1, 1), new DateTime(2024, 2, 1), "n", "A%" },
            compiled.Parameters.Values);
        Assert.Equal(new[] { "@p0", "@p1", "@p2", "@p3", "@p4", "@p5", "@p6", "@p7" }, compiled.Parameters.Keys);
        // @p0 is the row limit: no column.
        Assert.Equal(new (string, string)[] { ("@p1", "code"), ("@p2", "qty"), ("@p3", "name"), ("@p4", "day"), ("@p5", "day"), ("@p6", "note"), ("@p7", "code") },
            compiled.ParameterColumns.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => (pair.Key, pair.Value)));
        // Compiling never parses the catalog text: store types come only from the engine's check, attached by column.
        Assert.Empty(compiled.StoreTypes);
        Assert.True(StoreType.TryParse("char(4)", out var code));
        Assert.True(StoreType.TryParse("int", out var qty));
        var bound = compiled.WithStoreTypes(new Dictionary<string, StoreType> { ["CODE"] = code, ["qty"] = qty });
        Assert.Equal(new[] { "@p1", "@p2", "@p7" }, bound.StoreTypes.Keys.Order(StringComparer.Ordinal));
        Assert.Same(code, bound.StoreTypes["@p7"]);
        Assert.Same(qty, bound.StoreTypes["@p2"]);
        Assert.Equal((compiled.Sql, compiled.Keep, compiled.HasMatchedRowCount), (bound.Sql, bound.Keep, bound.HasMatchedRowCount));
        Assert.Equal(compiled.Parameters.ToArray(), bound.Parameters.ToArray());
        Assert.Equal(compiled.ParameterColumns.ToArray(), bound.ParameterColumns.ToArray());
        Assert.Equal(compiled.Columns, bound.Columns);
        Assert.Empty(compiled.StoreTypes);
        Assert.Empty(bound.WithStoreTypes(new Dictionary<string, StoreType>()).StoreTypes);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(5, 5)]
    [InlineData(6, 10)]
    [InlineData(10, 10)]
    [InlineData(11, 20)]
    [InlineData(100, 100)]
    public void T8_sql_server_pads_in_and_not_in_lists_to_ef_bucket_sizes_with_the_last_value(int count, int padded)
    {
        foreach (var op in new[] { "in", "not_in" })
        {
            var values = string.Join(",", Enumerable.Range(1, count));
            var compiled = new SqlServerEngine().Compile(Validate(Typed(),
                $$"""{"select":["code"],"filters":[{"col":"qty","op":"{{op}}","value":[{{values}}]}],"limit":2}"""));
            var placeholders = string.Join(", ", Enumerable.Range(1, padded).Select(i => "@p" + i));
            Assert.Equal($"SELECT TOP (@p0) [code] FROM [dbo].[synthetic_view] WHERE [qty] {(op == "in" ? "IN" : "NOT IN")} ({placeholders})",
                compiled.Sql);
            var expected = Enumerable.Range(1, count).Select(i => (object)(decimal)i)
                .Concat(Enumerable.Repeat((object)(decimal)count, padded - count)).Prepend(3);
            Assert.Equal(expected, compiled.Parameters.Values);
            Assert.Equal(padded, compiled.ParameterColumns.Count);
            Assert.All(compiled.ParameterColumns.Values, column => Assert.Equal("qty", column));
        }
    }

    [Fact]
    public void T8_padding_never_changes_the_validated_query()
    {
        var validated = Validate(Typed(), """{"select":["code"],"filters":[{"col":"qty","op":"in","value":[1,2,3,4,5,6]}],"limit":2}""");
        var first = new SqlServerEngine().Compile(validated);
        var second = new SqlServerEngine().Compile(validated);
        Assert.Equal(first.Sql, second.Sql);
        Assert.Equal(11, second.Parameters.Count);
        var membership = Assert.Single(validated.CloneQuery().GetComponents<SqlKata.AbstractCondition>("where")
            .OfType<SqlKata.InCondition<object>>());
        Assert.Equal(6, membership.Values.Count());
    }

    [Fact]
    public void T9_a_source_without_store_types_binds_no_store_type_but_still_pads_its_in_lists()
    {
        // Values and SQL are as before, except that an IN list is padded on every SQL Server source, store types or not:
        // the six values here become ten (the same rows; other SQL text and parameter count).
        const string query = """{"select":["code"],"filters":[{"col":"code","op":"eq","value":"AB"},{"col":"qty","op":"in","value":[1,2]},{"col":"qty","op":"in","value":[1,2,3,4,5,6]}],"limit":2}""";
        var compiled = new SqlServerEngine().Compile(Validate(Map(Column("code", "text", 1), Column("qty", "number", 2)), query));
        Assert.Equal("SELECT TOP (@p0) [code] FROM [dbo].[synthetic_view] WHERE [code] = @p1 AND [qty] IN (@p2, @p3) AND " +
            "[qty] IN (@p4, @p5, @p6, @p7, @p8, @p9, @p10, @p11, @p12, @p13)", compiled.Sql);
        Assert.Equal(new object[] { 3, "AB", 1m, 2m, 1m, 2m, 3m, 4m, 5m, 6m, 6m, 6m, 6m, 6m }, compiled.Parameters.Values);
        Assert.Empty(compiled.StoreTypes);
    }

    [Fact]
    public void T10_compiled_column_and_store_type_maps_must_name_parameters_and_are_snapshotted()
    {
        Assert.True(StoreType.TryParse("int", out var storeType));
        var parameters = new Dictionary<string, object> { ["@p1"] = 1m };
        var map = new Dictionary<string, StoreType> { ["@p1"] = storeType };
        var columns = new Dictionary<string, string> { ["@p1"] = "qty" };
        var compiled = new CompiledQuery("select @p1", parameters, 1, false, []) { StoreTypes = map, ParameterColumns = columns };
        map.Clear();
        columns.Clear();
        Assert.Same(storeType, Assert.Single(compiled.StoreTypes).Value);
        Assert.Equal("qty", Assert.Single(compiled.ParameterColumns).Value);
        var plain = new CompiledQuery("select @p1", parameters, 1, false, []);
        Assert.Empty(plain.StoreTypes);
        Assert.Empty(plain.ParameterColumns);
        Assert.Equal("StoreTypes", Assert.Throws<ArgumentException>(() =>
            new CompiledQuery("select @p1", parameters, 1, false, []) { StoreTypes = new Dictionary<string, StoreType> { ["@p2"] = storeType } }).ParamName);
        Assert.Equal("ParameterColumns", Assert.Throws<ArgumentException>(() =>
            new CompiledQuery("select @p1", parameters, 1, false, []) { ParameterColumns = new Dictionary<string, string> { ["@p2"] = "qty" } }).ParamName);
        Assert.Throws<ArgumentNullException>(() => new CompiledQuery("select @p1", parameters, 1, false, []) { StoreTypes = null! });
        Assert.Throws<ArgumentNullException>(() => new CompiledQuery("select @p1", parameters, 1, false, []) { ParameterColumns = null! });
        Assert.Throws<ArgumentNullException>(() => plain.WithStoreTypes(null!));
        Assert.Throws<NotSupportedException>(() => ((IDictionary<string, StoreType>)compiled.StoreTypes).Add("@p1", storeType));
        Assert.Throws<NotSupportedException>(() => ((IDictionary<string, string>)compiled.ParameterColumns).Add("@p1", "x"));
    }

    [Theory]
    [InlineData("varchar(20)", "varchar(20)")]
    [InlineData(" VARCHAR ( MAX ) ", "varchar(max)")]
    [InlineData("Decimal(19, 4)", "decimal(19,4)")]
    [InlineData("decimal(9)", "decimal(9)")]
    [InlineData("decimal", "decimal")]
    [InlineData("datetime2(3)", "datetime2(3)")]
    [InlineData("INT", "int")]
    public void T12_spelling_is_one_canonical_form(string text, string spelling)
    {
        Assert.True(StoreType.TryParse(text, out var parsed));
        Assert.Equal(spelling, parsed.Spelling);
        Assert.True(StoreType.TryParse(parsed.Spelling, out var again));
        Assert.Equal(parsed, again);
    }

    [Fact]
    public void T11_sql_server_ddl_views_and_both_migrations_add_store_type_and_its_override_last_and_audit_them()
    {
        var ddl = Text("db/catalog-ddl.sql");
        var views = Text("db/catalog-views.sql");
        var expression = Text("db/catalog-expression.sql");
        var storeType = Text("db/catalog-store-type.sql");
        // nvarchar(128) on both tables, with the reason stated once on the column table.
        Assert.Contains("\t[expression] [nvarchar](max) NULL,\n\t[store_type] [nvarchar](128) NULL,  -- nvarchar like expression, " +
            "the other catalog-owner text cell; the parser accepts ASCII only\n\t[store_type_override] [bit] NOT NULL,\n CONSTRAINT [PK_DataSourceColumn] ", ddl);
        Assert.Contains("\t[expression] [nvarchar](max) NULL,\n\t[store_type] [nvarchar](128) NULL,\n\t[store_type_override] [bit] NOT NULL,\n CONSTRAINT [PK_DataSourceColumnAudit] ", ddl);
        Assert.Contains("ALTER TABLE [dbo].[DataSourceColumn] ADD CONSTRAINT [DF_DataSourceColumn_store_type_override] DEFAULT ((0)) FOR [store_type_override]\nGO\n", ddl);
        Assert.Contains("ALTER TABLE [dbo].[DataSourceColumnAudit] ADD CONSTRAINT [DF_DataSourceColumnAudit_store_type_override] DEFAULT ((0)) FOR [store_type_override]\nGO\n", ddl);
        Assert.Equal(2, Regex.Matches(ddl, Regex.Escape("\t\t, [expression]\n\t\t, [store_type]\n\t\t, [store_type_override]\n\t)")).Count);
        Assert.Contains("\t\t, i.[expression]\n\t\t, i.[store_type]\n\t\t, i.[store_type_override]\n\tFROM inserted AS i", ddl);
        Assert.Contains("\t\t, d.[expression]\n\t\t, d.[store_type]\n\t\t, d.[store_type_override]\n\tFROM deleted AS d", ddl);
        const string view = "[can_aggregate], [expression], [store_type],\n       [store_type_override]\nFROM [dbo].[DataSourceColumn];";
        Assert.Contains(view, views);

        // Both migrations are one script under two names: either one, in either order or again, leaves the same catalog.
        Assert.Equal(Body(expression, ":On Error exit\n"), Body(storeType, ":On Error exit\n"));
        Assert.Contains("-v CatalogDatabase=Catalog", storeType);
        Assert.Contains(view, storeType);
        foreach (var cell in new[] { "expression", "store_type" })
        foreach (var table in new[] { "DataSourceColumn", "DataSourceColumnAudit" })
        {
            var type = cell == "expression" ? "[nvarchar](max)" : "[nvarchar](128)";
            Assert.Contains($"IF COL_LENGTH(N'dbo.{table}', N'{cell}') IS NULL\n        ALTER TABLE [dbo].[{table}] ADD [{cell}] {type} NULL;", storeType);
        }
        foreach (var table in new[] { "DataSourceColumn", "DataSourceColumnAudit" })
            Assert.Contains($"IF COL_LENGTH(N'dbo.{table}', N'store_type_override') IS NULL\n        ALTER TABLE [dbo].[{table}] ADD [store_type_override] [bit] NOT NULL\n" +
                $"            CONSTRAINT [DF_{table}_store_type_override] DEFAULT ((0));", storeType);
        var start = ddl.IndexOf("CREATE TRIGGER [dbo].[trDataSourceColumn_Audit]", StringComparison.Ordinal);
        var trigger = ddl[start..(ddl.IndexOf("END\nGO", start, StringComparison.Ordinal) + 3)];
        var migrated = "EXEC(N'" + trigger.Replace("CREATE TRIGGER", "CREATE OR ALTER TRIGGER").Replace("'", "''") + "');";
        Assert.Contains(migrated, storeType);
        // Every cell is added before the trigger that names them, inside the one bounded transaction.
        Assert.True(storeType.IndexOf("SET LOCK_TIMEOUT 10000;", StringComparison.Ordinal) < storeType.IndexOf("BEGIN TRAN;", StringComparison.Ordinal));
        Assert.True(storeType.LastIndexOf("ADD [store_type_override]", StringComparison.Ordinal) < storeType.IndexOf(migrated, StringComparison.Ordinal));
        Assert.DoesNotMatch(@"(?im)^\s*(DROP|GRANT|REVOKE|DENY)\b|EXEC\(N'\s*(DROP|GRANT|REVOKE|DENY)\b", storeType);
    }

    [Fact]
    public void T11_postgresql_ddl_views_and_both_migrations_add_store_type_and_its_override_last_and_audit_them()
    {
        var ddl = Text("db/postgresql/catalog-ddl.sql");
        var views = Text("db/postgresql/catalog-views.sql");
        var expression = Text("db/postgresql/catalog-expression.sql");
        var storeType = Text("db/postgresql/catalog-store-type.sql");
        var timeout = Text("db/postgresql/catalog-query-timeout.sql");
        Assert.Equal(2, Regex.Matches(ddl, Regex.Escape("    expression text,\n    store_type varchar(128),\n    store_type_override boolean NOT NULL DEFAULT false\n);")).Count);
        Assert.Contains("can_aggregate, expression, store_type,\n       store_type_override\nFROM datagateway.datasourcecolumn;", views);
        Assert.Equal(Body(expression, "-- It replaces only"), Body(storeType, "-- It replaces only"));

        var function = Slice(ddl, "CREATE OR REPLACE FUNCTION datagateway.audit_datasourcecolumn()", "$audit$;");
        Assert.Contains("can_aggregate, expression, store_type, store_type_override)\n    VALUES", function);
        Assert.Contains("image.expression, image.store_type, image.store_type_override);", function);
        var view = Slice(views, "CREATE OR REPLACE VIEW datagateway.vdatasourcecolumn", "FROM datagateway.datasourcecolumn;");
        string[] cells =
        [
            "ALTER TABLE datagateway.datasourcecolumnaudit ADD COLUMN IF NOT EXISTS expression text;",
            "ALTER TABLE datagateway.datasourcecolumn ADD COLUMN IF NOT EXISTS expression text;",
            "ALTER TABLE datagateway.datasourcecolumnaudit ADD COLUMN IF NOT EXISTS store_type varchar(128);",
            "ALTER TABLE datagateway.datasourcecolumn ADD COLUMN IF NOT EXISTS store_type varchar(128);",
            "ALTER TABLE datagateway.datasourcecolumnaudit ADD COLUMN IF NOT EXISTS store_type_override boolean NOT NULL DEFAULT false;",
            "ALTER TABLE datagateway.datasourcecolumn ADD COLUMN IF NOT EXISTS store_type_override boolean NOT NULL DEFAULT false;"
        ];
        InOrder(storeType, ["\\set ON_ERROR_STOP on", "BEGIN;", "SET LOCAL lock_timeout = '10s';", .. cells, function, view, "COMMIT;"]);
        // A rerun of catalog-ddl.sql, and the older migration that includes it, install the cells before the function.
        InOrder(ddl, [.. cells, "CREATE OR REPLACE FUNCTION datagateway.audit_datasourcecolumn()"]);
        InOrder(timeout, [.. cells, "\\ir catalog-ddl.sql"]);
        Assert.DoesNotMatch(@"(?i)\bREVOKE\b", storeType);
        Assert.DoesNotMatch(@"(?im)^\s*(DROP|GRANT)\b", storeType);
        Assert.DoesNotMatch(@"(?m)^\\i", storeType);
    }

    private static void InOrder(string text, string[] steps)
    {
        var at = -1;
        foreach (var step in steps)
        {
            var next = text.IndexOf(step, at + 1, StringComparison.Ordinal);
            Assert.True(next > at, "Out of order or missing: " + step);
            at = next;
        }
    }

    private static string Body(string text, string from) => text[text.IndexOf(from, StringComparison.Ordinal)..];

    private static string Slice(string text, string from, string through)
    {
        var start = text.IndexOf(from, StringComparison.Ordinal);
        Assert.True(start >= 0, "Missing: " + from);
        return text[start..(text.IndexOf(through, start, StringComparison.Ordinal) + through.Length)];
    }

    private static string Text(string path) => File.ReadAllText(TestFiles.InRepo(path)).Replace("\r\n", "\n");

    private static SourceContract Typed() => Map(
        Column("code", "text", 1) with { StoreType = "char(4)" },
        Column("name", "text", 2) with { StoreType = "nvarchar(40)" },
        Column("qty", "number", 3) with { StoreType = "int" },
        Column("day", "date", 4) with { StoreType = "date" },
        Column("note", "text", 5));

    private static ValidatedQuery Validate(SourceContract contract, string json)
    {
        var validated = QueryBuilding.Validate(contract, json, QueryLimits.ForRowCap(500));
        Assert.True(validated.IsSuccess, validated.Failure?.Message);
        return validated.Value;
    }

    private static SourceContract Map(params DataSourceColumnRow[] columns)
    {
        var mapped = CatalogMapping.Map(new(Source(), columns));
        Assert.True(mapped.IsSuccess, mapped.Failure?.Message);
        return mapped.Value;
    }

    private static DataSourceColumnRow Column(string name, string type, int ordinal) =>
        new(name, ordinal, name, "Invented.", type, null, null, null, true, true, true, true, true);

    private static DataSourceRow Source() => new("dbo.synthetic_view", "fixture-connection-removed")
    {
        Id = 7319, Name = "synthetic", Title = "Synthetic", Description = "Synthetic", Grain = "One row",
        IsActive = true, RowCap = 500, QueryTimeoutSeconds = 30
    };
}
