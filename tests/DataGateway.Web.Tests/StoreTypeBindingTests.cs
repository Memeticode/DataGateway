using System.Data;
using System.Data.Common;
using System.Net;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using Dapper.Data;
using DataGateway.Core.Catalog;
using DataGateway.Core.Queries;
using DataGateway.Data;
using DataGateway.Data.PostgreSql;
using DataGateway.Data.Queries;
using DataGateway.Data.Sqlite;
using DataGateway.Data.SqlServer;
using DataGateway.Web.Catalog;
using Microsoft.Data.SqlClient;
using Xunit;
using static DataGateway.Web.Tests.CatalogEngineTests;

namespace DataGateway.Web.Tests;

/// <summary>SQL Server binds each filter value as its column's catalog store type, PostgreSQL and SQLite
/// do not, and the example readers carry the cell.</summary>
public sealed class StoreTypeBindingTests
{
    private static readonly string X4000 = new('x', 4000), X4001 = new('x', 4001), X5000 = new('x', 5000),
        X8000 = new('x', 8000), X8001 = new('x', 8001);

    // What preview.6 sends for a value, written out: the driver's own nvarchar for text (no size set, so the driver
    // reports the value's length), datetime2 for a date, and the driver's inference for everything else.
    private static (SqlDbType, int, object) Preview6(object? value) => value switch
    {
        null => (SqlDbType.NVarChar, 0, DBNull.Value),
        string text => (SqlDbType.NVarChar, text.Length, text),
        DateTime date => (SqlDbType.DateTime2, 0, date),
        decimal number => (SqlDbType.Decimal, 0, number),
        bool flag => (SqlDbType.Bit, 0, flag),
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };

    public static TheoryData<string, object?, SqlDbType, int, object> Rule() => new()
    {
        // varchar / char + ASCII: varchar sized n when it fits, else 8000, else max; char only at exactly n. Over 4000
        // characters always max, even when the column is longer: varchar(8000) against a column that is really nvarchar
        // would be cut to nvarchar(4000).
        { "varchar(20)", "ABCD", SqlDbType.VarChar, 20, "ABCD" },
        { "varchar(20)", "", SqlDbType.VarChar, 20, "" },
        { "varchar(20)", new string('a', 20), SqlDbType.VarChar, 20, new string('a', 20) },
        { "varchar(20)", new string('a', 21), SqlDbType.VarChar, 8000, new string('a', 21) },
        { "varchar(20)", X4000, SqlDbType.VarChar, 8000, X4000 },
        { "varchar(20)", X4001, SqlDbType.VarChar, -1, X4001 },
        { "varchar(20)", X8000, SqlDbType.VarChar, -1, X8000 },
        { "varchar(20)", X8001, SqlDbType.VarChar, -1, X8001 },
        { "varchar(4000)", X4000, SqlDbType.VarChar, 4000, X4000 },
        { "varchar(5000)", X4001, SqlDbType.VarChar, -1, X4001 },
        { "varchar(8000)", X8000, SqlDbType.VarChar, -1, X8000 },
        { "char(5000)", X5000, SqlDbType.VarChar, -1, X5000 },
        { "varchar(max)", "ABCD", SqlDbType.VarChar, 8000, "ABCD" },
        { "varchar(max)", X8001, SqlDbType.VarChar, -1, X8001 },
        { "char(10)", "ABCDEFGHIJ", SqlDbType.Char, 10, "ABCDEFGHIJ" },
        { "char(10)", "AB", SqlDbType.VarChar, 10, "AB" },
        { "char(10)", "AB%", SqlDbType.VarChar, 10, "AB%" },
        { "char(10)", "ABCDEFGHIJK", SqlDbType.VarChar, 8000, "ABCDEFGHIJK" },
        { "CHAR ( 10 )", "ABCDEFGHIJ", SqlDbType.Char, 10, "ABCDEFGHIJ" },
        // nvarchar / nchar: nvarchar sized n when it fits, else 4000, else max; nchar only at exactly n.
        { "nvarchar(40)", "abc", SqlDbType.NVarChar, 40, "abc" },
        { "nvarchar(40)", "Z\u00fcrich", SqlDbType.NVarChar, 40, "Z\u00fcrich" },
        { "nvarchar(40)", new string('a', 41), SqlDbType.NVarChar, 4000, new string('a', 41) },
        { "nvarchar(40)", X4000, SqlDbType.NVarChar, 4000, X4000 },
        { "nvarchar(40)", X4001, SqlDbType.NVarChar, -1, X4001 },
        { "nvarchar(4000)", X4000, SqlDbType.NVarChar, 4000, X4000 },
        { "nvarchar(4000)", X5000, SqlDbType.NVarChar, -1, X5000 },
        { "nvarchar(max)", "abc", SqlDbType.NVarChar, 4000, "abc" },
        { "nvarchar(max)", X5000, SqlDbType.NVarChar, -1, X5000 },
        { "nchar(3)", "abc", SqlDbType.NChar, 3, "abc" },
        { "nchar(3)", "ab", SqlDbType.NVarChar, 3, "ab" },
        { "nchar(3)", "\u03bb\u03bb\u03bb", SqlDbType.NChar, 3, "\u03bb\u03bb\u03bb" },
        { "nchar(3)", X5000, SqlDbType.NVarChar, -1, X5000 },
        // Whole numbers in range bind as the integer type.
        { "int", 5m, SqlDbType.Int, 0, 5 },
        { "int", -2147483648m, SqlDbType.Int, 0, int.MinValue },
        { "int", 2147483647.000m, SqlDbType.Int, 0, int.MaxValue },
        { "bigint", 3000000000m, SqlDbType.BigInt, 0, 3000000000L },
        { "smallint", 32767m, SqlDbType.SmallInt, 0, (short)32767 },
        { "tinyint", 255m, SqlDbType.TinyInt, 0, (byte)255 },
        { "tinyint", 0m, SqlDbType.TinyInt, 0, (byte)0 },
        // Dates bind as the column's date type.
        { "datetime", new DateTime(2024, 1, 2), SqlDbType.DateTime, 0, new DateTime(2024, 1, 2) },
        { "datetime", new DateTime(1753, 1, 1), SqlDbType.DateTime, 0, new DateTime(1753, 1, 1) },
        { "smalldatetime", new DateTime(2024, 1, 2), SqlDbType.SmallDateTime, 0, new DateTime(2024, 1, 2) },
        { "smalldatetime", new DateTime(2079, 6, 6), SqlDbType.SmallDateTime, 0, new DateTime(2079, 6, 6) },
        { "date", new DateTime(1, 1, 1), SqlDbType.Date, 0, new DateTime(1, 1, 1) },
        // A time part the type holds exactly: datetime keeps 1/300 s, so whole hundredths; smalldatetime whole minutes.
        { "datetime", new DateTime(2024, 1, 2, 13, 45, 30, 120), SqlDbType.DateTime, 0, new DateTime(2024, 1, 2, 13, 45, 30, 120) },
        { "datetime", new DateTime(9999, 12, 31, 23, 59, 59, 990), SqlDbType.DateTime, 0, new DateTime(9999, 12, 31, 23, 59, 59, 990) },
        { "smalldatetime", new DateTime(2024, 1, 2, 10, 30, 0), SqlDbType.SmallDateTime, 0, new DateTime(2024, 1, 2, 10, 30, 0) },
        { "smalldatetime", new DateTime(2079, 6, 6, 23, 59, 0), SqlDbType.SmallDateTime, 0, new DateTime(2079, 6, 6, 23, 59, 0) },
        { "datetime2(7)", new DateTime(2024, 1, 2).AddTicks(1), SqlDbType.DateTime2, 0, new DateTime(2024, 1, 2).AddTicks(1) },
        { "datetime2(7)", new DateTime(2024, 1, 2), SqlDbType.DateTime2, 0, new DateTime(2024, 1, 2) },
        // bit + bool.
        { "bit", true, SqlDbType.Bit, 0, true },
        { "bit", false, SqlDbType.Bit, 0, false },
    };

    [Theory]
    [MemberData(nameof(Rule))]
    public void T1_a_value_binds_as_its_column_store_type(string storeType, object? value, SqlDbType type, int size, object bound)
    {
        var parameter = Typed(storeType, value);
        // Only text gets a size; for other types the driver infers one from the type.
        Assert.Equal((type, size, bound), (parameter.SqlDbType, value is string ? parameter.Size : 0, parameter.Value));
        Assert.Equal(bound.GetType(), parameter.Value.GetType());
        Assert.Equal(type switch
        {
            SqlDbType.VarChar => DbType.AnsiString, SqlDbType.Char => DbType.AnsiStringFixedLength,
            SqlDbType.NVarChar => DbType.String, SqlDbType.NChar => DbType.StringFixedLength,
            SqlDbType.Int => DbType.Int32, SqlDbType.BigInt => DbType.Int64, SqlDbType.SmallInt => DbType.Int16,
            SqlDbType.TinyInt => DbType.Byte, SqlDbType.DateTime => DbType.DateTime, SqlDbType.Date => DbType.Date,
            SqlDbType.DateTime2 => DbType.DateTime2, SqlDbType.Bit => DbType.Boolean,
            SqlDbType.SmallDateTime => DbType.DateTime, _ => throw new ArgumentOutOfRangeException(nameof(type))
        }, parameter.DbType);
    }

    public static TheoryData<string, object?> AsPreview6() => new()
    {
        // Non-ASCII text against varchar or char keeps nvarchar, so no character turns into '?'.
        { "varchar(20)", "Z\u00fcrich" }, { "char(10)", "\u03bb" }, { "varchar(max)", "\U0001F600" }, { "char(1)", "\u00df" },
        // Out-of-range or fractional numbers stay decimals.
        { "int", 2147483648m }, { "int", 1.5m }, { "smallint", 32768m }, { "tinyint", -1m }, { "tinyint", 256m },
        { "bigint", 9223372036854775808m },
        // decimal and numeric keep the value's own precision and scale.
        { "decimal(19,4)", 1.23456m }, { "numeric(10,2)", 12.5m }, { "decimal", 7m },
        // Dates a datetime or smalldatetime cannot hold.
        { "datetime", new DateTime(1752, 12, 31) }, { "smalldatetime", new DateTime(1899, 12, 31) },
        { "smalldatetime", new DateTime(2079, 6, 7) },
        // A time part the type would round or drop: the value stays datetime2, so no fraction, second or hour is lost.
        { "datetime", new DateTime(2024, 1, 2, 0, 0, 0, 1) }, { "datetime", new DateTime(2024, 1, 2, 0, 0, 0, 3) },
        { "datetime", new DateTime(2024, 1, 2).AddTicks(1) }, { "datetime", new DateTime(9999, 12, 31, 23, 59, 59, 999) },
        { "smalldatetime", new DateTime(2024, 1, 2, 10, 30, 15) }, { "smalldatetime", new DateTime(2024, 1, 2, 10, 30, 0, 500) },
        { "smalldatetime", new DateTime(2079, 6, 7, 0, 1, 0) },
        { "date", new DateTime(2024, 1, 2, 10, 0, 0) }, { "date", new DateTime(2024, 1, 2).AddTicks(1) },
        // A value of another kind than its column (catalog drift), and types the rule does not name.
        { "int", "5" }, { "varchar(20)", 5m }, { "date", "2024-01-01" }, { "bit", 1m }, { "nvarchar(10)", true },
        { "varchar(20)", new DateTime(2024, 1, 2) }, { "money", 2m }, { "float", 2m }, { "time", new DateTime(2024, 1, 2) },
        { "text", "abc" }, { "ntext", "abc" }, { "uniqueidentifier", "abc" },
        // No value.
        { "varchar(20)", null }, { "int", null },
    };

    [Theory]
    [MemberData(nameof(AsPreview6))]
    public void T2_a_value_the_rule_does_not_cover_binds_exactly_as_preview6(string storeType, object? value)
    {
        var parameter = Typed(storeType, value);
        Assert.Equal(Preview6(value), (parameter.SqlDbType, value is string ? parameter.Size : 0, parameter.Value));
        Assert.Equal(0, parameter.Precision);
        Assert.Equal(0, parameter.Scale);
    }

    [Fact]
    public void T2_a_utf8_varchar_column_binds_text_as_preview6()
    {
        Assert.True(StoreType.TryParse("varchar(20)", out var storeType));
        var parameter = new SqlParameter { ParameterName = "@p1" };
        Hook("PrepareParameter", parameter, "ABCD", storeType with { Utf8Collation = true });
        Assert.Equal(Preview6("ABCD"), (parameter.SqlDbType, parameter.Size, parameter.Value));
    }

    [Fact]
    public void T3_long_unicode_text_goes_as_max_and_a_char_pattern_goes_as_varchar()
    {
        // 5000 x's against nvarchar(4000): longer than the column's largest fixed size, so it goes at max and keeps every character.
        var unicode = Typed("nvarchar(4000)", X5000);
        Assert.Equal((SqlDbType.NVarChar, -1), (unicode.SqlDbType, unicode.Size));
        Assert.Same(X5000, unicode.Value);
        // LIKE against char(10). A fixed-length char(10) pattern would be blank-padded to 'AB%       '; varchar is not.
        var compiled = new SqlServerEngine().Compile(Validate(
            """{"select":["code"],"filters":[{"col":"code","op":"like","value":"AB%"}],"limit":1}"""));
        Assert.Equal("SELECT TOP (@p0) [code] FROM [dbo].[synthetic] WHERE [code] like @p1", compiled.Sql);
        var pattern = Bind(compiled, "@p1");
        Assert.Equal((SqlDbType.VarChar, 10, (object)"AB%"), (pattern.SqlDbType, pattern.Size, pattern.Value));
    }

    [Theory]
    [InlineData("code", "like", "A[_]", "A[[][_]]", SqlDbType.VarChar, 10)]
    [InlineData("code", "contains", "ABCDEFGH", "%ABCDEFGH%", SqlDbType.Char, 10)]
    [InlineData("code", "contains", "ABCDEFGHI", "%ABCDEFGHI%", SqlDbType.VarChar, 8000)]
    [InlineData("tag", "contains", "a", "%a%", SqlDbType.VarChar, 3)]
    [InlineData("tag", "contains", "a_", "%a\\_%", SqlDbType.VarChar, 8000)]
    [InlineData("label", "contains", "x", "%x%", SqlDbType.NVarChar, 40)]
    public void T4_like_and_contains_patterns_are_sized_by_the_pattern_not_the_value(string column, string op, string value,
        string bound, SqlDbType type, int size)
    {
        var compiled = new SqlServerEngine().Compile(Validate(
            $$"""{"select":["code"],"filters":[{"col":"{{column}}","op":"{{op}}","value":"{{value.Replace("\\", "\\\\")}}"}],"limit":1}"""));
        var parameter = Bind(compiled, "@p1");
        Assert.Equal((type, size, (object)bound), (parameter.SqlDbType, parameter.Size, parameter.Value));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(5, 5)]
    [InlineData(6, 10)]
    [InlineData(10, 10)]
    [InlineData(11, 20)]
    [InlineData(100, 100)]
    [InlineData(150, 150)]
    [InlineData(151, 200)]
    [InlineData(750, 750)]
    [InlineData(751, 800)]
    public void T5_in_list_buckets_follow_ef_core_10(int count, int padded) =>
        Assert.Equal(padded, SqlServerColumnCompiler.PaddedCount(count));

    [Fact]
    public void T5_every_padded_value_binds_as_its_column_type()
    {
        var compiled = new SqlServerEngine().Compile(Validate(
            """{"select":["code"],"filters":[{"col":"tag","op":"in","value":["a","b","c","d","e","f"]}],"limit":1}"""));
        Assert.Equal("SELECT TOP (@p0) [code] FROM [dbo].[synthetic] WHERE [tag] IN (@p1, @p2, @p3, @p4, @p5, @p6, @p7, @p8, @p9, @p10)",
            compiled.Sql);
        var bound = Enumerable.Range(1, 10).Select(i => Bind(compiled, "@p" + i)).ToArray();
        Assert.Equal(new object[] { "a", "b", "c", "d", "e", "f", "f", "f", "f", "f" }, bound.Select(p => p.Value));
        Assert.All(bound, p => Assert.Equal((SqlDbType.VarChar, 3), (p.SqlDbType, p.Size)));
    }

    [Fact]
    public void T5_not_in_is_padded_to_the_same_bucket_and_every_value_typed()
    {
        var compiled = new SqlServerEngine().Compile(Validate(
            """{"select":["code"],"filters":[{"col":"label","op":"not_in","value":["a","b","c","d","e","f","g"]}],"limit":1}"""));
        Assert.Equal("SELECT TOP (@p0) [code] FROM [dbo].[synthetic] WHERE [label] NOT IN (@p1, @p2, @p3, @p4, @p5, @p6, @p7, @p8, @p9, @p10)",
            compiled.Sql);
        var bound = Enumerable.Range(1, 10).Select(i => Bind(compiled, "@p" + i)).ToArray();
        Assert.Equal(new object[] { "a", "b", "c", "d", "e", "f", "g", "g", "g", "g" }, bound.Select(p => p.Value));
        Assert.All(bound, p => Assert.Equal((SqlDbType.NVarChar, 40), (p.SqlDbType, p.Size)));
    }

    // SQL Server refuses a request with more than 2100 parameters, so padding stops where it would take the query, TOP
    // included, above 2098; lists pad in order, each while the total still allows it.
    [Theory]
    [InlineData(new[] { 2050 }, new[] { 2050 })]          // 2051 unpadded; 2100 values would make 2101
    [InlineData(new[] { 2047 }, new[] { 2047 })]          // 2100 values would make 2101
    [InlineData(new[] { 1997 }, new[] { 2000 })]          // 2001 padded: within the limit
    [InlineData(new[] { 1901, 97 }, new[] { 2000, 97 })]  // the first pads to exactly 2098; the second would pass it
    [InlineData(new[] { 701, 1301 }, new[] { 750, 1301 })]
    [InlineData(new[] { 2001, 6 }, new[] { 2001, 10 })]   // the first cannot pad, the second still can
    [InlineData(new[] { 6, 11 }, new[] { 10, 20 })]       // small lists pad as before
    public void T5_padding_never_takes_a_query_past_2098_parameters(int[] counts, int[] padded)
    {
        var filters = counts.Select((count, list) =>
            $$"""{"col":"qty","op":"in","value":[{{string.Join(",", Enumerable.Range(list * 10000 + 1, count))}}]}""");
        var json = $$"""{"select":["code"],"filters":[{{string.Join(",", filters)}}],"limit":1}""";
        var limits = QueryLimits.Create(rowCap: 500, maxJsonLength: int.MaxValue, maxFilterDepth: DefaultFilterDepth,
            maxFilterConditions: 10, maxInValues: 2100);
        var validated = QueryBuilding.Validate(Contract(typed: true), json, limits);
        Assert.True(validated.IsSuccess, validated.Failure?.Message);

        var compiled = new SqlServerEngine().Compile(validated.Value);

        var next = 1;
        var lists = new List<string>();
        foreach (var size in padded)
        {
            lists.Add("[qty] IN (" + string.Join(", ", Enumerable.Range(next, size).Select(index => "@p" + index)) + ")");
            next += size;
        }
        Assert.Equal("SELECT TOP (@p0) [code] FROM [dbo].[synthetic] WHERE " + string.Join(" AND ", lists), compiled.Sql);
        Assert.Equal(1 + padded.Sum(), compiled.Parameters.Count);
        Assert.InRange(compiled.Parameters.Count, 1, 2100);
        // Each list keeps its own values in order, then repeats its last one.
        next = 1;
        for (var list = 0; list < counts.Length; list++)
        {
            var values = Enumerable.Range(next, padded[list]).Select(index => Convert.ToInt32(compiled.Parameters["@p" + index])).ToArray();
            var own = Enumerable.Range(list * 10000 + 1, counts[list]).ToArray();
            Assert.Equal(own.Concat(Enumerable.Repeat(own[^1], padded[list] - counts[list])), values);
            next += padded[list];
        }
    }

    private static readonly int DefaultFilterDepth = QueryLimits.ForRowCap(500).MaxFilterDepth;

    [Fact]
    public void T5_the_same_column_filtered_twice_types_both_values_each_sized_by_its_own_length()
    {
        var compiled = new SqlServerEngine().Compile(Validate(
            """{"select":["code"],"filters":[{"col":"code","op":"eq","value":"ABCDEFGHIJ"},{"col":"code","op":"like","value":"AB%"},{"col":"code","op":"neq","value":"ABCDEFGHIJKLMNOP"}],"limit":1}"""));
        Assert.Equal("SELECT TOP (@p0) [code] FROM [dbo].[synthetic] WHERE [code] = @p1 AND [code] like @p2 AND [code] != @p3", compiled.Sql);
        Assert.Equal(new[] { ("@p1", "code"), ("@p2", "code"), ("@p3", "code") },
            compiled.ParameterColumns.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => (pair.Key, pair.Value)));
        var exact = Bind(compiled, "@p1");
        var pattern = Bind(compiled, "@p2");
        var longer = Bind(compiled, "@p3");
        Assert.Equal((SqlDbType.Char, 10, (object)"ABCDEFGHIJ"), (exact.SqlDbType, exact.Size, exact.Value));
        Assert.Equal((SqlDbType.VarChar, 10, (object)"AB%"), (pattern.SqlDbType, pattern.Size, pattern.Value));
        Assert.Equal((SqlDbType.VarChar, 8000, (object)"ABCDEFGHIJKLMNOP"), (longer.SqlDbType, longer.Size, longer.Value));
    }

    [Fact]
    public async Task T6_the_engine_passes_each_parameter_its_own_store_type_and_skips_the_row_limit()
    {
        using var probe = new SourceProbe();
        var engine = new RecordingEngine();
        var compiled = engine.Compile(Validate(
            """{"select":["code"],"filters":[{"or":[{"col":"code","op":"eq","value":"AB"},{"col":"note","op":"eq","value":"n"}]},{"col":"qty","op":"between","value":[1,2]}],"limit":1}"""))
            .WithStoreTypes(Agreed);
        Assert.Equal(new[] { "@p1", "@p3", "@p4" }, compiled.StoreTypes.Keys.Order(StringComparer.Ordinal));
        await engine.ReadSourceAsync("synthetic", compiled, 7, () => { }, rows => SqliteEngineTests.Collect(rows), default);
        Assert.Equal(new[] { "typed @p1 AB char", "typed @p3 1 int", "typed @p4 2 int", "untyped @p2 n" },
            engine.Calls.Order(StringComparer.Ordinal));
        var parameters = Assert.Single(probe.Commands).Parameters.Cast<DbParameter>().ToArray();
        Assert.Equal(2, parameters[0].Value);
        Assert.Equal(new[] { "@p0", "@p1", "@p2", "@p3", "@p4" }, parameters.Select(p => p.ParameterName));
    }

    [Fact]
    public async Task T7_postgresql_and_sqlite_compile_byte_for_byte_as_before_and_never_read_store_types()
    {
        const string query = """{"select":["code"],"filters":[{"or":[{"col":"code","op":"eq","value":"AB"},{"col":"qty","op":"in","value":[1,2,3,4,5,6]}]},{"col":"label","op":"contains","value":"x"}],"limit":1}""";
        var typed = Validate(query);
        var plain = Validate(query, typed: false);
        // Text these engines' own databases would never accept, so any attempt to read it would show.
        var contract = Contract(typed: true, unreadable: true);
        Assert.NotEmpty(contract.StoreTypes);
        foreach (IDatabaseEngine engine in new IDatabaseEngine[] { new PostgreSqlEngine(), new SqliteEngine() })
        {
            Assert.False(engine is IStoreTypeEngine);
            var withTypes = engine.Compile(typed);
            var without = engine.Compile(plain);
            Assert.Equal(without.Sql, withTypes.Sql);
            Assert.Equal(without.Parameters.ToArray(), withTypes.Parameters.ToArray());
            Assert.Empty(withTypes.StoreTypes);
            Assert.Empty(withTypes.ParameterColumns);
            var findings = new List<StoreTypeFinding>();
            Assert.Same(withTypes, await new QueryRunner(engine).BindStoreTypesAsync(contract, withTypes, findings.Add, default));
            Assert.Empty(findings);
            // No IN padding outside SQL Server: six values stay six.
            Assert.Equal(1 + 1 + 6 + 1, withTypes.Parameters.Count);
        }
        Assert.Equal("SELECT \"code\" FROM \"dbo\".\"synthetic\" WHERE ((\"code\" = @p0) OR (\"qty\" IN (@p1, @p2, @p3, @p4, @p5, @p6))) AND \"label\" like @p7 ESCAPE '\\' LIMIT @p8",
            new PostgreSqlEngine().Compile(typed).Sql);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task T8_example_readers_project_store_type_by_name_and_read_it_verbatim(bool pg)
    {
        var readSql = pg ? PostgreSqlCatalog.ReadSql : SqlServerCatalog.ReadSql;
        Assert.Contains(pg ? "c.expression, c.store_type, c.store_type_override\n" : "c.[expression], c.[store_type], c.[store_type_override]\r\n", readSql);
        using var probe = new SourceProbe { AllowNextResult = true };
        var row = SourceRow();
        row["store_type"] = "varchar(20)";
        row["store_type_override"] = pg ? true : 1;
        Load(probe, row);
        var detail = await CatalogTableReader.DetailAsync(new DbContext<ProbeConnection>("synthetic"), readSql,
            pg ? PostgreSqlCatalog.AvailableSql : SqlServerCatalog.AvailableSql, "synthetic", "sqlserver", !pg, 15, default);
        var column = Assert.Single(detail!.Rows.Columns);
        Assert.Equal(("varchar(20)", true), (column.StoreType, column.StoreTypeOverride));
        var contract = CatalogMapping.Map(detail.Rows).Value;
        Assert.Equal(new CatalogStoreType("varchar(20)", true), contract.StoreTypes["measure"]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void T8_reader_treats_a_missing_or_non_text_store_type_cell_as_a_bad_cell_and_null_as_none(bool integral)
    {
        foreach (var value in new object?[] { null, DBNull.Value })
        {
            var row = SourceRow();
            row["store_type"] = value;
            Assert.Null(Assert.Single(CatalogTableReader.MapDetail([row], "sqlserver", integral).Rows.Columns).StoreType);
        }
        var missing = SourceRow();
        missing.Remove("store_type");
        Assert.Equal(CatalogTableReader.InvalidCell,
            Assert.Throws<InvalidCastException>(() => CatalogTableReader.MapDetail([missing], "sqlserver", integral)).Message);
        foreach (var value in new object[] { 42, true, new object() })
        {
            var row = SourceRow();
            row["store_type"] = value;
            Assert.Equal(CatalogTableReader.InvalidCell,
                Assert.Throws<InvalidCastException>(() => CatalogTableReader.MapDetail([row], "sqlserver", integral)).Message);
        }
        // store_type_override is a flag like can_select: required, never null, an integer 0/1 only where integral flags are.
        foreach (var value in new object?[] { null, DBNull.Value, "1", 2 })
        {
            var row = SourceRow();
            row["store_type_override"] = value;
            Assert.Equal(CatalogTableReader.InvalidCell,
                Assert.Throws<InvalidCastException>(() => CatalogTableReader.MapDetail([row], "sqlserver", integral)).Message);
        }
        var flagged = SourceRow();
        flagged["store_type_override"] = integral ? 1 : true;
        Assert.True(Assert.Single(CatalogTableReader.MapDetail([flagged], "sqlserver", integral).Rows.Columns).StoreTypeOverride);
        var absent = SourceRow();
        absent.Remove("store_type_override");
        Assert.Throws<InvalidCastException>(() => CatalogTableReader.MapDetail([absent], "sqlserver", integral));
    }

    [Fact]
    public async Task T9_sqlite_catalog_reads_the_stored_cell_and_describe_stays_byte_for_byte_the_same()
    {
        using var fixture = new SqliteFixture();
        var before = await fixture.Catalog().QueryCatalogDetailAsync("synthetic_items", default);
        Assert.All(before!.Rows.Columns, column => Assert.Null(column.StoreType));
        using var settings = fixture.Settings();
        await using var host = fixture.Host(new SqliteEngine());
        await host.Start();
        const string body = """{"source":"synthetic_items"}""";
        var plainHttp = await Describe(host, body);
        await using var mcp = await McpWire.Connect(host.Client);
        var plainMcp = McpWire.Text(await McpWire.Call(mcp, "describe_source", body));

        fixture.Change("UPDATE gateway_column SET store_type=@v0 WHERE datasource_fk=101 AND column_name='label'", "nvarchar(40)");
        fixture.Change("UPDATE gateway_column SET store_type=@v0 WHERE datasource_fk=101 AND column_name='amount'", "store-type-marker-9b1d");
        var after = await fixture.Catalog().QueryCatalogDetailAsync("synthetic_items", default);
        Assert.Equal("nvarchar(40)", Assert.Single(after!.Rows.Columns, column => column.ColumnName == "label").StoreType);
        var contract = CatalogMapping.Map(after.Rows).Value;
        // A SQLite source keeps both cells as written and never parses them.
        Assert.Equal(new[] { "amount", "label" }, contract.StoreTypes.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(new CatalogStoreType("store-type-marker-9b1d", false), contract.StoreTypes["amount"]);
        using (var query = await host.Client.PostAsync("/query", new StringContent(
            """{"source":"synthetic_items","query":{"select":["id"],"filters":[{"col":"label","op":"eq","value":"plain"}],"limit":1}}""",
            Encoding.UTF8, "application/json")))
            Assert.Equal(HttpStatusCode.OK, query.StatusCode);

        Assert.Equal(plainHttp, await Describe(host, body));
        Assert.Equal(plainMcp, McpWire.Text(await McpWire.Call(mcp, "describe_source", body)));
        Assert.DoesNotContain("nvarchar", plainHttp, StringComparison.Ordinal);
        Assert.DoesNotContain("store-type-marker-9b1d", string.Join("\n", host.AllLines().Select(line => line.GetRawText())), StringComparison.Ordinal);
    }

    private static async Task<string> Describe(LoggedHost host, string body)
    {
        using var response = await host.Client.PostAsync("/sources/describe", new StringContent(body, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    private static SqlParameter Typed(string storeType, object? value)
    {
        Assert.True(StoreType.TryParse(storeType, out var parsed), storeType);
        var parameter = new SqlParameter { ParameterName = "@p1" };
        Hook("PrepareParameter", parameter, value, parsed);
        Assert.Equal("@p1", parameter.ParameterName);
        return parameter;
    }

    // The test source's catalog types, as a check that found the view agreeing would return them.
    private static readonly IReadOnlyDictionary<string, StoreType> Agreed = new[] { ("code", "char(10)"), ("tag", "varchar(3)"),
        ("label", "nvarchar(40)"), ("qty", "int") }.ToDictionary(pair => pair.Item1, pair => StoreType.TryParse(pair.Item2, out var type) ? type : throw new InvalidOperationException());

    // Binds one compiled parameter the way the engine does once the check agreed: typed when its column has a store type.
    private static SqlParameter Bind(CompiledQuery compiled, string name)
    {
        var parameter = new SqlParameter { ParameterName = name };
        if (compiled.WithStoreTypes(Agreed).StoreTypes.TryGetValue(name, out var storeType))
            Hook("PrepareParameter", parameter, compiled.Parameters[name], storeType);
        else Hook("PrepareParameter", parameter, compiled.Parameters[name]);
        return parameter;
    }

    private static object? Hook(string name, params object?[] arguments)
    {
        var method = typeof(SqlServerEngine).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(candidate => candidate.Name == name && candidate.GetParameters().Length == arguments.Length);
        try { return method.Invoke(new SqlServerEngine(), arguments); }
        catch (TargetInvocationException error) { ExceptionDispatchInfo.Capture(error.InnerException!).Throw(); throw; }
    }

    private static SourceContract Contract(bool typed, bool unreadable = false)
    {
        DataSourceColumnRow Column(string name, string type, int ordinal, string storeType) =>
            new(name, ordinal, name, "Invented.", type, null, null, null, true, true, true, true, true)
            { StoreType = !typed ? null : unreadable ? "not a type (" + storeType : storeType };
        var mapped = CatalogMapping.Map(new(new DataSourceRow("dbo.synthetic", "fixture-connection-removed")
        {
            Id = 7319, Name = "synthetic", Title = "Synthetic", Description = "Synthetic", Grain = "One row",
            IsActive = true, RowCap = 500, QueryTimeoutSeconds = 30
        }, [Column("code", "text", 1, "char(10)"), Column("tag", "text", 2, "varchar(3)"), Column("label", "text", 3, "nvarchar(40)"),
            Column("qty", "number", 4, "int"), new("note", 5, "note", "Invented.", "text", null, null, null, true, true, true, true, true)]));
        Assert.True(mapped.IsSuccess, mapped.Failure?.Message);
        return mapped.Value;
    }

    private static ValidatedQuery Validate(string json, bool typed = true)
    {
        var validated = QueryBuilding.Validate(Contract(typed), json, QueryLimits.ForRowCap(500));
        Assert.True(validated.IsSuccess, validated.Failure?.Message);
        return validated.Value;
    }

    // The real base engine and the SQL Server compiler over a probe connection, recording which hook each value reached.
    private sealed class RecordingEngine() : DatabaseEngine<ProbeConnection>(text => new ProbeConnection { ConnectionString = text })
    {
        internal readonly List<string> Calls = [];
        protected override SqlKata.Compilers.Compiler CreateCompiler() => new SqlServerColumnCompiler();
        protected override string MatchedRowCountExpression => "COUNT_BIG(*) AS [__dg_matched_rows]";
        protected override (string Pattern, string EscapeCharacter) PrepareLikePattern(string pattern) => (pattern, "");
        protected override void PrepareParameter(DbParameter parameter, object? value)
        {
            Calls.Add($"untyped {parameter.ParameterName} {value}");
            parameter.Value = value;
        }
        protected override void PrepareParameter(DbParameter parameter, object? value, StoreType storeType)
        {
            Calls.Add($"typed {parameter.ParameterName} {value} {storeType.Name}");
            parameter.Value = value;
        }
        protected override object ConvertCell(object value, OutputColumn? column) => value;
        public override void ValidateSourceConnection(string? connection) { }
        public override ProviderFailure? RecognizeFailure(Exception error) => null;
    }
}
