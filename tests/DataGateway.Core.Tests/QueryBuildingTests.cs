using DataGateway.Data.SqlServer;
using System.Text.Json;
using System.Text.RegularExpressions;
using DataGateway.Core.Catalog;
using DataGateway.Core.Queries;
using SqlKata;
using SqlKata.JsonQuery;
using Xunit;

namespace DataGateway.Core.Tests;

/// <summary>Pins <see cref="QueryBuilding.Validate"/> and the SQL Server engine's compile step: a caller's JSON query
/// becomes exactly this SQL, these bindings and these output columns, or is refused with the library's code and
/// path.</summary>
/// <remarks>
/// <para>Every identifier in the SQL comes from the catalog, never from the caller, and every caller value is a
/// binding. <see cref="AssertCatalogOnly"/> checks that on every compiled query here, so a change that lets caller
/// text into the SQL fails many tests at once.</para>
/// <para>The row plan fetches one row more than it keeps; the first binding is always that fetch count.</para>
/// </remarks>
public sealed class QueryBuildingTests
{
    /// <summary>A grouped query using every aggregate function, sorted by an aggregate output and a group column.</summary>
    private const string Grouped = """{"group_by":["region"],"aggregates":[{"fn":"sum","col":"line_total"},{"fn":"avg","col":"quantity"},{"fn":"min","col":"order_date"},{"fn":"max","col":"region"},{"fn":"count","col":"is_return"}],"order_by":[{"col":"sum_line_total","dir":"desc"},{"col":"region","dir":"asc"}],"limit":5}""";

    /// <summary>A synthetic source whose columns cover each data type and a spread of flags, including a filter-only
    /// column and a column with every flag off.</summary>
    /// <param name="objectName">The catalog view name, for the view-name tests.</param>
    /// <param name="reserved">Use two columns named after SQL keywords instead.</param>
    private static SourceContract Contract(string objectName = "sales.order_lines", bool reserved = false)
    {
        var source = new DataSourceRow(objectName, "fixture-connection-removed")
        { QueryTimeoutSeconds = 30, Id = 7319,
            Name = "synthetic_sales", Title = "Synthetic sales", Description = "Synthetic sales by line.",
            Grain = "one line", RowCap = 500, IsActive = true
        };
        DataSourceColumnRow Col(string name, int ordinal, string type, bool select, bool filter, bool sort, bool group, bool aggregate) =>
            new(name, ordinal, name, "Synthetic column.", type, null, null, null, select, filter, sort, group, aggregate);
        DataSourceColumnRow[] columns = reserved
            ? [Col("group", 1, "text", true, true, true, true, true), Col("select", 2, "number", true, true, true, false, true)]
            : [Col("region", 1, "text", true, true, true, true, true),
               Col("customer_code", 2, "text", true, true, false, false, false),
               Col("order_date", 3, "date", true, true, true, true, true),
               Col("line_total", 4, "number", true, true, true, false, true),
               Col("quantity", 5, "number", true, false, true, false, true),
               Col("is_return", 6, "bool", true, true, true, true, true),
               Col("sku", 7, "text", false, true, false, false, false),
               Col("internal_note", 8, "text", false, false, false, false, false)];
        var mapped = CatalogMapping.Map(new(source, columns));
        Assert.True(mapped.IsSuccess, mapped.Failure?.Message);
        return mapped.Value;
    }

    /// <summary>Golden SQL for select with sort and limit, and for an empty query that selects every selectable column.</summary>
    [Fact]
    public void T2_golden_sql_parameters_and_output_columns()
    {
        var result0 = Check(Contract(), """{"select":["REGION","line_total"],"order_by":[{"col":"Line_Total","dir":"desc"}],"limit":3}""",
            """SELECT TOP (@p0) [region], [line_total] FROM [sales].[order_lines] ORDER BY [line_total] DESC""",
            4, 3, "region:text, line_total:number", 4);
        var result1 = Check(Contract(), """{}""",
            """SELECT TOP (@p0) [region], [customer_code], [order_date], [line_total], [quantity], [is_return] FROM [sales].[order_lines]""",
            501, 500, "region:text, customer_code:text, order_date:date, line_total:number, quantity:number, is_return:bool", 501);
    }

    /// <summary>Golden SQL for grouping with aggregates, grouping alone, and an ungrouped aggregate.</summary>
    /// <remarks>The ungrouped aggregate adds a matched-row count column, so the answer can tell that nothing matched
    /// from an aggregate over zero rows; it is not an output column.</remarks>
    [Fact]
    public void T3_golden_sql_parameters_and_output_columns()
    {
        var result0 = Check(Contract(), """{"group_by":["region"],"aggregates":[{"fn":"sum","col":"line_total"},{"fn":"avg","col":"quantity"},{"fn":"min","col":"order_date"},{"fn":"max","col":"region"},{"fn":"count","col":"is_return"}],"order_by":[{"col":"sum_line_total","dir":"desc"},{"col":"region","dir":"asc"}],"limit":5}""",
            """SELECT TOP (@p0) [region], SUM([line_total]) AS [sum_line_total], AVG([quantity]) AS [avg_quantity], MIN([order_date]) AS [min_order_date], MAX([region]) AS [max_region], COUNT([is_return]) AS [count_is_return] FROM [sales].[order_lines] GROUP BY [region] ORDER BY [sum_line_total] DESC, [region]""",
            6, 5, "region:text, sum_line_total:number, avg_quantity:number, min_order_date:date, max_region:text, count_is_return:number", 6);
        var result1 = Check(Contract(), """{"group_by":["is_return","order_date"]}""",
            """SELECT TOP (@p0) [is_return], [order_date] FROM [sales].[order_lines] GROUP BY [is_return], [order_date]""",
            501, 500, "is_return:bool, order_date:date", 501);
        var result2 = Check(Contract(), """{"aggregates":[{"fn":"count","col":"region"}]}""",
            """SELECT TOP (@p0) COUNT([region]) AS [count_region], COUNT_BIG(*) AS [__dg_matched_rows] FROM [sales].[order_lines]""",
            501, 500, "count_region:number", 501);
    }

    /// <summary>Golden SQL for every filter operator, nested and/or groups, and booleans written as SQL literals rather
    /// than bindings.</summary>
    [Fact]
    public void T4_golden_sql_parameters_and_output_columns()
    {
        var result0 = Check(Contract(), """{"select":["region"],"filters":[{"col":"region","op":"eq","value":"North"},{"col":"customer_code","op":"neq","value":"C-1"},{"col":"is_return","op":"eq","value":false},{"col":"is_return","op":"neq","value":true},{"col":"line_total","op":"gt","value":10.5},{"col":"line_total","op":"lte","value":1000},{"col":"line_total","op":"in","value":[1,2,2,3]},{"col":"region","op":"not_in","value":["East","West"]},{"col":"order_date","op":"between","value":["2024-01-01","2024-12-31"]},{"col":"sku","op":"is_not_null"},{"or":[{"col":"region","op":"is_null"},{"and":[{"col":"customer_code","op":"contains","value":"A_1"},{"or":[{"col":"sku","op":"like","value":"B%"},{"col":"line_total","op":"gte","value":5},{"col":"line_total","op":"lt","value":0}]}]}]}]}""",
            """SELECT TOP (@p0) [region] FROM [sales].[order_lines] WHERE [region] = @p1 AND [customer_code] != @p2 AND [is_return] = cast(0 as bit) AND [is_return] != cast(1 as bit) AND [line_total] > @p3 AND [line_total] <= @p4 AND [line_total] IN (@p5, @p6, @p7) AND [region] NOT IN (@p8, @p9) AND [order_date] BETWEEN @p10 AND @p11 AND [sku] IS NOT NULL AND (([region] IS NULL) OR (([customer_code] like @p12 ESCAPE '\') AND (([sku] like @p13) OR ([line_total] >= @p14) OR ([line_total] < @p15))))""",
            501, 500, "region:text", 501, "North", "C-1", 10.5m, 1000m, 1m, 2m, 3m, "East", "West", new DateTime(2024, 1, 1), new DateTime(2024, 12, 31), @"%A\_1%", "B%", 5m, 0m);
        Assert.Equal(2, Regex.Matches(result0.Compiled.Sql, "cast\\(").Count);
        Assert.DoesNotContain(result0.Compiled.Parameters.Values, value => value is bool);
    }

    /// <summary>Golden SQL for hostile filter values: each one arrives as a binding, and the SQL text never changes.</summary>
    [Fact]
    public void T5_golden_sql_parameters_and_output_columns()
    {
        var result0 = Check(Contract(), """{"select":["customer_code"],"filters":[{"col":"customer_code","op":"eq","value":"x'; DROP TABLE sales.order_lines; --"},{"col":"region","op":"in","value":["a]b","[c]","/* d */"]},{"col":"customer_code","op":"contains","value":"50%_off\\[x]"},{"col":"sku","op":"like","value":"%'--"}],"limit":2}""",
            """SELECT TOP (@p0) [customer_code] FROM [sales].[order_lines] WHERE [customer_code] = @p1 AND [region] IN (@p2, @p3, @p4) AND [customer_code] like @p5 ESCAPE '\' AND [sku] like @p6""",
            3, 2, "customer_code:text", 3, "x'; DROP TABLE sales.order_lines; --", "a]b", "[c]", "/* d */", @"%50\%\_off\\\[x]%", "%'--");
    }

    /// <summary>A column name or sort direction carrying SQL is refused, never quoted into the SQL.</summary>
    [Theory]
    [InlineData("""{"select":["customer_code]; DROP TABLE x; --"]}""", "UnknownColumn", "select[0]")]
    [InlineData("""{"select":["[region]"]}""", "UnknownColumn", "select[0]")]
    [InlineData("""{"filters":[{"col":"region; --","op":"eq","value":"x"}]}""", "UnknownColumn", "filters[0].col")]
    [InlineData("""{"order_by":[{"col":"region desc; --"}]}""", "UnknownColumn", "order_by[0].col")]
    [InlineData("""{"order_by":[{"col":"region","dir":"desc; DROP"}]}""", "InvalidSortDirection", "order_by[0].dir")]
    public void T5_caller_identifiers_are_refused(string json, string code, string path) => Refusal(json, code, path);

    /// <summary>The fetch count reaches the SQL as the one top binding, the keep count reaches the compiled query, and
    /// the shared limits are not changed by planning.</summary>
    [Fact]
    public void T6_row_plan_is_applied_to_sql_without_changing_limits()
    {
        var contract = Contract();
        var limits = QueryLimits.ForRowCap(500);
        foreach (var (suffix, fetch, keep) in new[] { ("", 501, 500), (",\"limit\":null", 501, 500),
                     (",\"limit\":7", 8, 7), (",\"limit\":500", 501, 500) })
        {
            var result = QueryBuilding.Validate(contract, "{\"select\":[\"region\"]" + suffix + "}", limits);
            Assert.True(result.IsSuccess, result.Failure?.Message);
            var compiled = new SqlServerEngine().Compile(result.Value);
            Assert.Equal(fetch, result.Value.RowLimit.Fetch);
            Assert.Equal(keep, result.Value.RowLimit.Keep);
            Assert.Equal(keep, compiled.Keep);
            AssertParameters(compiled, fetch);
            Assert.Single(Regex.Matches(compiled.Sql, @"TOP \(").Cast<Match>());
            AssertCatalogOnly(contract, result.Value, compiled);
        }
        Refusal("""{"limit":501}""", "StructuralLimitExceeded", "limit");
        Refusal("""{"limit":0}""", "InvalidLimit", "limit");
        Assert.Equal(QueryLimits.ForRowCap(500), limits);
        Assert.Equal(500, limits.MaxLimit);
        var maximum = QueryBuilding.Validate(contract, "{}", QueryLimits.ForRowCap(QueryLimits.MaxRowCap)).Value;
        var maxCompiled = new SqlServerEngine().Compile(maximum);
        AssertParameters(maxCompiled, int.MaxValue);
        Assert.Equal(QueryLimits.MaxRowCap, maxCompiled.Keep);
        AssertCatalogOnly(contract, maximum, maxCompiled);
    }

    /// <summary>A repeated, string or fractional limit is refused; compiling twice gives the same SQL and bindings; and
    /// the builder leaves JSON parsing to the query library.</summary>
    [Fact]
    public void T7_one_limit_read_and_repeat_compilation_changes_nothing()
    {
        Refusal("""{"select":["region"],"limit":2,"limit":400}""", "InvalidJson", "limit");
        Refusal("""{"limit":"5"}""", "InvalidJson", "$");
        Refusal("""{"limit":5.5}""", "InvalidJson", "$");
        var contract = Contract();
        var validated = QueryBuilding.Validate(contract, """{"select":["region"],"limit":7}""", QueryLimits.ForRowCap(500)).Value;
        var rowLimit = validated.RowLimit;
        var columns = validated.Columns;
        var first = new SqlServerEngine().Compile(validated);
        var second = new SqlServerEngine().Compile(validated);
        Assert.Equal(first.Sql, second.Sql);
        Assert.Equal(first.Parameters, second.Parameters);
        Assert.Same(rowLimit, validated.RowLimit);
        Assert.Same(columns, validated.Columns);
        Assert.Equal(8, rowLimit.Fetch);
        AssertColumns(validated, "region:text");
        AssertCatalogOnly(contract, validated, first);
        AssertCatalogOnly(contract, validated, second);
        var source = File.ReadAllText(TestFiles.InRepo("DataGateway.Core/Queries/QueryBuilding.cs"));
        foreach (var forbidden in new[] { "JsonDocument", "JsonElement", "JsonNode", "Utf8JsonReader", "JsonSerializer" })
            Assert.DoesNotContain(forbidden, source, StringComparison.Ordinal);
    }

    /// <summary>In a like pattern only <c>%</c> is a wildcard: SQL Server's <c>_</c> and <c>[</c> wildcards are escaped in
    /// the binding, while the validated query keeps the caller's pattern unchanged.</summary>
    /// <remarks>The same pattern then means the same thing on every engine.</remarks>
    [Theory]
    [InlineData("ab_c", "ab[_]c")]
    [InlineData("[xy]z", "[[]xy]z")]
    [InlineData("a_[b", "a[_][[]b")]
    [InlineData("%_%", "%[_]%")]
    [InlineData("[[", "[[][[]")]
    [InlineData("Can%", "Can%")]
    [InlineData("a]b", "a]b")]
    [InlineData(@"a\b", @"a\b")]
    [InlineData("a^b", "a^b")]
    [InlineData("*x", "*x")]
    public void T7_like_has_only_percent_wildcard(string pattern, string bound)
    {
        var json = JsonSerializer.Serialize(new { select = new[] { "region" }, filters = new[] { new { col = "sku", op = "like", value = pattern } } });
        var pair = Check(Contract(), json,
            "SELECT TOP (@p0) [region] FROM [sales].[order_lines] WHERE [sku] like @p1",
            501, 500, "region:text", 501, bound);
        var callerQuery = pair.Validated.CloneQuery();
        Assert.Equal(pattern, callerQuery.GetOneComponent<BasicStringCondition>("where").Value);
        var again = new SqlServerEngine().Compile(pair.Validated);
        Assert.Equal(pair.Compiled.Sql, again.Sql);
        Assert.Equal(pair.Compiled.Parameters, again.Parameters);
        Assert.Equal(pattern, pair.Validated.CloneQuery().GetOneComponent<BasicStringCondition>("where").Value);
    }

    /// <summary>Like patterns are escaped inside nested and/or groups too.</summary>
    [Fact]
    public void T8_like_walks_root_or_and_inside_or_without_changing_sql()
    {
        Check(Contract(), """{"select":["region"],"filters":[{"col":"sku","op":"like","value":"a_b"},{"or":[{"col":"sku","op":"like","value":"[xy]"},{"and":[{"col":"sku","op":"like","value":"a_[b"}]}]}]}""",
            "SELECT TOP (@p0) [region] FROM [sales].[order_lines] WHERE [sku] like @p1 AND (([sku] like @p2) OR (([sku] like @p3)))",
            501, 500, "region:text", 501, "a[_]b", "[[]xy]", "a[_][[]b");
    }

    /// <summary>Like and contains on the same text bind differently: like escapes SQL Server wildcards, contains wraps
    /// the text in <c>%</c> with an escape clause.</summary>
    [Fact]
    public void T9_like_and_contains_keep_distinct_bindings_and_sql()
    {
        Check(Contract(), """{"select":["region"],"filters":[{"col":"sku","op":"like","value":"A_1"},{"col":"sku","op":"contains","value":"A_1"}]}""",
            """SELECT TOP (@p0) [region] FROM [sales].[order_lines] WHERE [sku] like @p1 AND [sku] like @p2 ESCAPE '\'""",
            501, 500, "region:text", 501, "A[_]1", @"%A\_1%");
    }

    /// <summary>One query for each refusal code the library raises, including each structural limit.</summary>
    public static IEnumerable<object[]> RequestRefusals()
    {
        yield return ["not json", "InvalidJson"];
        yield return ["", "InvalidJson"];
        yield return ["[]", "InvalidJson"];
        yield return ["""{"select":"region"}""", "InvalidJson"];
        yield return ["""{"select":null}""", "InvalidStructure"];
        yield return ["""{"selct":["region"]}""", "InvalidJson"];
        yield return ["""{"select":["nope"]}""", "UnknownColumn"];
        yield return ["""{"group_by":["line_total"]}""", "ColumnOperationNotAllowed"];
        yield return ["""{"filters":[{"col":"is_return","op":"gt","value":true}]}""", "InvalidOperator"];
        yield return ["""{"aggregates":[{"fn":"sum","col":"region"}]}""", "InvalidAggregateFunction"];
        yield return ["""{"select":["region"],"order_by":[{"col":"region","dir":"up"}]}""", "InvalidSortDirection"];
        yield return ["""{"select":["region"],"filters":[{"col":"order_date","op":"eq","value":"31/12/2024"}]}""", "InvalidValue"];
        yield return ["""{"select":["region"],"group_by":["region"]}""", "InvalidClauseCombination"];
        yield return ["""{"group_by":["region"],"order_by":[{"col":"line_total","dir":"asc"}]}""", "InvalidOrderTarget"];
        yield return ["""{"select":["region","REGION"]}""", "DuplicateReference"];
        yield return ["""{"limit":0}""", "InvalidLimit"];
        yield return ["""{"limit":501}""", "StructuralLimitExceeded"];
        var nested = """{"col":"region","op":"eq","value":"x"}""";
        for (var i = 0; i < 9; i++) nested = "{\"and\":[" + nested + "]}";
        yield return ["{\"filters\":[" + nested + "]}", "StructuralLimitExceeded"];
        yield return [JsonSerializer.Serialize(new { filters = new[] { new { col = "region", op = "in", value = Enumerable.Range(0, 101).Select(i => i.ToString()).ToArray() } } }), "StructuralLimitExceeded"];
        yield return [JsonSerializer.Serialize(new { filters = Enumerable.Range(0, 51).Select(_ => new { col = "region", op = "eq", value = "x" }).ToArray() }), "StructuralLimitExceeded"];
        yield return ["{}".PadRight(4097), "StructuralLimitExceeded"];
        yield return ["""{"x'; --":1,"x'; --":2}""", "InvalidJson"];
    }

    /// <summary>The gateway keeps the library's code and path, replaces its message with the gateway's own fix-it text,
    /// and logs it at step <c>query</c>.</summary>
    [Theory]
    [MemberData(nameof(RequestRefusals))]
    public void T8_library_refusal_triple_is_unchanged(string json, string code)
    {
        var contract = Contract();
        var limits = QueryLimits.Create(rowCap: 500, maxJsonLength: 4096, maxFilterDepth: 8, maxFilterConditions: 50, maxInValues: 100);
        var expected = Assert.Throws<JsonQueryException>(() => new global::SqlKata.Query("sales.order_lines")
            .WithJsonQuery(json, contract.QueryColumns, limits));
        Assert.Equal(code, expected.Code.ToString());
        var result = QueryBuilding.Validate(contract, json, limits);
        Assert.False(result.IsSuccess);
        Assert.Throws<InvalidOperationException>(() => result.Value);
        var failure = Assert.IsType<CatalogFailure>(result.Failure);
        Assert.Equal((expected.Code.ToString(), expected.Path, RefusalMessage.For(expected.Code, expected.Path, expected.Detail)), (failure.Code, failure.Path, failure.Message));
        Assert.NotEqual(expected.Message, failure.Message);
        Assert.Equal("query", failure.Step);
        Assert.Null(failure.ExceptionType);
        Assert.Null(failure.SqlNumber);
        Assert.DoesNotContain("fixture-connection-removed", JsonSerializer.Serialize(failure), StringComparison.Ordinal);
        if (json.Length == 4097) Assert.Equal("$", failure.Path);
        if (json.StartsWith("{\"x'; --\"", StringComparison.Ordinal)) Assert.Equal("x'; --", failure.Path);
    }

    [Theory]
    [InlineData("not json", "InvalidJson", "$", "The query must be one JSON object whose only members are select, filters, group_by, aggregates, order_by and limit, each used once and with the type querySchema shows. Correct it against querySchema from describe source.")]
    [InlineData("""{"select":["nope"]}""", "UnknownColumn", "select[0]", "The column at path is not a queryable column of this source. Use a column from describe source whose flag for this clause is true.")]
    [InlineData("""{"limit":501}""", "StructuralLimitExceeded", "limit", "The query is over a configured limit: limit 501 exceeds the maximum of 500. Ask for no more rows than the maximum, or leave limit out to get the first rows, with more set to true when rows were left out.")]
    public void T8_literal_failure_triples(string json, string code, string path, string message)
    {
        var failure = Refusal(json, code, path);
        Assert.Equal(message, failure.Message);
    }

    [Theory]
    [InlineData("""{"select":["sku"]}""", "select[0]")]
    [InlineData("""{"filters":[{"col":"quantity","op":"eq","value":1}]}""", "filters[0].col")]
    [InlineData("""{"filters":[{"or":[{"col":"region","op":"eq","value":"x"},{"col":"quantity","op":"eq","value":1}]}]}""", "filters[0].or[1].col")]
    [InlineData("""{"order_by":[{"col":"customer_code"}]}""", "order_by[0].col")]
    [InlineData("""{"group_by":["line_total"]}""", "group_by[0]")]
    [InlineData("""{"aggregates":[{"fn":"count","col":"customer_code"}]}""", "aggregates[0].col")]
    public void T9_each_missing_capability_is_refused(string json, string path) => Refusal(json, "ColumnOperationNotAllowed", path);

    /// <summary>A column with every flag off is unknown to every clause, exactly like a column that does not exist.</summary>
    [Theory]
    [InlineData("internal_note")]
    [InlineData("secret_col")]
    public void T9_documented_only_and_unknown_columns_in_all_clauses(string column)
    {
        Refusal(JsonSerializer.Serialize(new { select = new[] { column } }), "UnknownColumn", "select[0]");
        Refusal(JsonSerializer.Serialize(new { filters = new[] { new { col = column, op = "eq", value = "x" } } }), "UnknownColumn", "filters[0].col");
        Refusal(JsonSerializer.Serialize(new { order_by = new[] { new { col = column } } }), "UnknownColumn", "order_by[0].col");
        Refusal(JsonSerializer.Serialize(new { group_by = new[] { column } }), "UnknownColumn", "group_by[0]");
        Refusal(JsonSerializer.Serialize(new { aggregates = new[] { new { fn = "count", col = column } } }), "UnknownColumn", "aggregates[0].col");
    }

    /// <summary>A catalog view name of one or two bare identifiers is bracket-quoted part by part.</summary>
    [Theory]
    [InlineData("dbo.vSynthetic", "[dbo].[vSynthetic]")]
    [InlineData("synthetic", "[synthetic]")]
    [InlineData("_s._v1", "[_s].[_v1]")]
    [InlineData("S1.V_2", "[S1].[V_2]")]
    public void T10_bare_view_names_are_wrapped(string name, string wrapped)
    {
        Check(Contract(name), """{"select":["region"]}""", "SELECT TOP (@p0) [region] FROM " + wrapped,
            501, 500, "region:text", 501);
    }

    /// <summary>View and column names that are SQL keywords work, because they are always quoted.</summary>
    [Fact]
    public void T10_reserved_names_remain_catalog_identifiers()
    {
        Check(Contract("dbo.Order", reserved: true),
            """{"group_by":["group"],"aggregates":[{"fn":"sum","col":"select"}],"order_by":[{"col":"group","dir":"asc"}]}""",
            "SELECT TOP (@p0) [group], SUM([select]) AS [sum_select] FROM [dbo].[Order] GROUP BY [group] ORDER BY [group]",
            501, 500, "group:text, sum_select:number", 501);
    }

    /// <summary>A view name the library would refuse is an invalid catalog with a fixed message that does not repeat the
    /// name; a caller's own bad query is still reported as the caller's error.</summary>
    [Theory]
    [InlineData("dbo.[v]")]
    [InlineData("dbo.my view")]
    [InlineData("db.dbo.v")]
    [InlineData("dbo.v;DROP")]
    [InlineData("dbo.")]
    [InlineData(".v")]
    [InlineData("")]
    [InlineData(" dbo.v")]
    [InlineData("dbo.v-x")]
    [InlineData("dbo..v")]
    [InlineData("1dbo.v")]
    [InlineData("dbo.v\u00e9")]
    public void T10_invalid_view_names_agree_with_library_and_do_not_leak(string name)
    {
        var contract = Contract(name);
        var limits = QueryLimits.ForRowCap(500);
        var exception = Assert.Throws<ArgumentException>(() => new global::SqlKata.Query(name).WithJsonQuery("{}", contract.QueryColumns));
        Assert.Equal("query", exception.ParamName);
        var result = QueryBuilding.Validate(contract, "{}", limits);
        Assert.False(result.IsSuccess);
        Assert.Throws<InvalidOperationException>(() => result.Value);
        var failure = Assert.IsType<CatalogFailure>(result.Failure);
        Assert.Equal(("InvalidCatalog", "$.source", "Correct the catalog object_name: name the view as schema.view or view, with bare ASCII SQL identifiers."),
            (failure.Code, failure.Path, failure.Message));
        Assert.Equal("seed", failure.Step);
        // Exact constant above proves no name interpolation, including empty/substring names.
        if (name.Length > 2) Assert.DoesNotContain(name, failure.Message, StringComparison.Ordinal);
        var requestFailure = QueryBuilding.Validate(contract, "not json", limits).Failure!;
        Assert.Equal(("InvalidJson", "$", "The query must be one JSON object whose only members are select, filters, group_by, aggregates, order_by and limit, each used once and with the type querySchema shows. Correct it against querySchema from describe source."),
            (requestFailure.Code, requestFailure.Path, requestFailure.Message));
        Assert.Equal("query", requestFailure.Step);
    }

    /// <summary>A validated query serializes as its row plan and output columns only, never the view or connection, and
    /// its columns and the compiled bindings are read-only.</summary>
    [Fact]
    public void T11_output_json_hides_query_and_parameters_are_read_only()
    {
        var contract = Contract();
        var validated = QueryBuilding.Validate(contract, Grouped, QueryLimits.ForRowCap(500)).Value;
        Assert.Equal("""[{"name":"region","dataType":"text"},{"name":"sum_line_total","dataType":"number"},{"name":"avg_quantity","dataType":"number"},{"name":"min_order_date","dataType":"date"},{"name":"max_region","dataType":"text"},{"name":"count_is_return","dataType":"number"}]""",
            JsonSerializer.Serialize(validated.Columns, JsonSerializerOptions.Web));
        var json = JsonSerializer.Serialize(validated, JsonSerializerOptions.Web);
        using var parsed = JsonDocument.Parse(json);
        Assert.Equal(new[] { "rowLimit", "columns" }, parsed.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.DoesNotContain("order_lines", json, StringComparison.Ordinal);
        Assert.DoesNotContain("fixture-connection-removed", json, StringComparison.Ordinal);
        Assert.Throws<NotSupportedException>(() => ((IList<OutputColumn>)validated.Columns).Add(new("x", "text")));
        var compiled = new SqlServerEngine().Compile(validated);
        Assert.Throws<NotSupportedException>(() => ((IDictionary<string, object>)compiled.Parameters).Add("@injected", 1));
        AssertCatalogOnly(contract, validated, compiled);
    }

    /// <summary>Missing arguments and limits not built by <see cref="QueryLimits"/> throw instead of becoming a refusal.</summary>
    [Fact]
    public void T12_programming_errors_escape()
    {
        var contract = Contract();
        var limits = QueryLimits.ForRowCap(500);
        Assert.Equal("contract", Assert.Throws<ArgumentNullException>(() => QueryBuilding.Validate(null!, "{}", limits)).ParamName);
        Assert.Equal("query", Assert.Throws<ArgumentNullException>(() => QueryBuilding.Validate(contract, null!, limits)).ParamName);
        Assert.Equal("limits", Assert.Throws<ArgumentNullException>(() => QueryBuilding.Validate(contract, "{}", null!)).ParamName);
        Assert.Equal("query", Assert.Throws<ArgumentNullException>(() => new SqlServerEngine().Compile(null!)).ParamName);
        Assert.Equal("limits", Assert.Throws<ArgumentException>(() => QueryBuilding.Validate(contract, """{"select":["region"]}""", JsonQueryLimits.Default)).ParamName);
    }

    /// <summary>Validates <paramref name="json"/> against <see cref="Contract"/> and asserts it is refused with this code
    /// and path at step <c>query</c>.</summary>
    /// <returns>The refusal, for further checks.</returns>
    private static CatalogFailure Refusal(string json, string code, string path)
    {
        var result = QueryBuilding.Validate(Contract(), json, QueryLimits.ForRowCap(500));
        Assert.False(result.IsSuccess);
        Assert.Throws<InvalidOperationException>(() => result.Value);
        var failure = Assert.IsType<CatalogFailure>(result.Failure);
        Assert.Equal((code, path, "query"), (failure.Code, failure.Path, failure.Step));
        return failure;
    }

    // Keep the prescribed exact SQL projection-cardinality assertion verbatim.
#pragma warning disable xUnit2013
    /// <summary>Asserts compiled SQL reads one catalog view, quotes only catalog and output names, numbers its bindings
    /// <c>@p0</c> upward with the fetch count first, and uses no word outside a small SQL vocabulary.</summary>
    /// <remarks>This is the guard against caller text in the SQL: any token it does not recognize fails the test.</remarks>
    private static void AssertCatalogOnly(SourceContract contract, ValidatedQuery validated, CompiledQuery compiled)
    {
        var sql = compiled.Sql;
        const string metadata = ", COUNT_BIG(*) AS [__dg_matched_rows]";
        if (compiled.HasMatchedRowCount)
        {
            Assert.Equal(1, Regex.Matches(sql, Regex.Escape(metadata)).Count);
            sql = sql.Replace(metadata, "", StringComparison.Ordinal);
        }
        else Assert.DoesNotContain(metadata, sql, StringComparison.Ordinal);
        var view = string.Join(".", contract.ObjectName.Split('.').Select(part => $"[{part}]"));
        Assert.Single(Regex.Matches(sql, @"\bFROM ", RegexOptions.IgnoreCase).Cast<Match>());
        Assert.Contains("FROM " + view, sql, StringComparison.Ordinal);
        foreach (var forbidden in new[] { "JOIN", "(SELECT", ";", "--", "/*" })
            Assert.DoesNotContain(forbidden, sql, StringComparison.OrdinalIgnoreCase);
        var names = contract.ObjectName.Split('.').Concat(contract.Columns.Select(c => c.Name))
            .Concat(validated.Columns.Select(c => c.Name)).ToHashSet(StringComparer.Ordinal);
        foreach (Match token in Regex.Matches(sql, @"\[([^\]]+)\]"))
            Assert.Contains(token.Groups[1].Value, names);
        var parameters = Regex.Matches(sql, @"(?<![\w@])@p\d+\b").Select(m => m.Value).ToArray();
        Assert.Equal(compiled.Parameters.Count, parameters.Length);
        Assert.Equal(Enumerable.Range(0, parameters.Length).Select(i => $"@p{i}"), parameters);
        foreach (var key in compiled.Parameters.Keys)
            Assert.Single(parameters, p => p == key);
        Assert.Equal(validated.RowLimit.Fetch, Assert.IsType<int>(compiled.Parameters["@p0"]));
        var remainder = Regex.Replace(sql.Replace(view, "", StringComparison.Ordinal), @"\[[^\]]+\]|(?<![\w@])@p\d+\b", " ")
            .Replace(" ESCAPE '\\'", "", StringComparison.Ordinal)
            .Replace("cast(1 as bit)", "", StringComparison.Ordinal)
            .Replace("cast(0 as bit)", "", StringComparison.Ordinal);
        var vocabulary = "SELECT TOP AS FROM WHERE AND OR NOT IN BETWEEN IS NULL like GROUP ORDER BY DESC COUNT SUM AVG MIN MAX = != < <= > >="
            .Split(' ').ToHashSet(StringComparer.Ordinal);
        foreach (var word in Regex.Split(remainder, @"[\s(),]+").Where(word => word.Length > 0))
            Assert.Contains(word, vocabulary);
        Assert.DoesNotContain("fixture-connection-removed", sql, StringComparison.Ordinal);
    }
#pragma warning restore xUnit2013

    /// <summary>Asserts the bindings are <c>@p0</c>, <c>@p1</c> and so on with exactly these values and types; dates
    /// carry no time zone kind.</summary>
    private static void AssertParameters(CompiledQuery compiled, params object[] values)
    {
        var expected = values.Select((value, i) => new KeyValuePair<string, object>($"@p{i}", value))
            .ToDictionary(pair => pair.Key, pair => pair.Value);
        Assert.Equal(expected.Count, compiled.Parameters.Count);
        Assert.Equal(expected.Keys, compiled.Parameters.Keys);
        foreach (var (key, value) in expected)
        {
            Assert.True(compiled.Parameters.ContainsKey(key));
            Assert.Equal(value.GetType(), compiled.Parameters[key].GetType());
            Assert.Equal(value, compiled.Parameters[key]);
            if (value is DateTime) Assert.Equal(DateTimeKind.Unspecified, ((DateTime)compiled.Parameters[key]).Kind);
        }
    }

    /// <summary>Asserts the output columns as a comma-separated list of <c>name:type</c>.</summary>
    private static void AssertColumns(ValidatedQuery validated, string expected) =>
        Assert.Equal(expected.Split(", "), validated.Columns.Select(c => $"{c.Name}:{c.DataType}"));

    /// <summary>Validates and compiles a query and asserts its exact SQL, row plan, output columns and bindings.</summary>
    /// <param name="contract">The source to query.</param>
    /// <param name="json">The caller's query.</param>
    /// <param name="sql">The exact SQL expected.</param>
    /// <param name="fetch">Rows the plan fetches.</param>
    /// <param name="keep">Rows the plan keeps.</param>
    /// <param name="columns">The output columns, as for <see cref="AssertColumns"/>.</param>
    /// <param name="parameters">The binding values in order, starting with the fetch count.</param>
    private static (ValidatedQuery Validated, CompiledQuery Compiled) Check(SourceContract contract, string json,
        string sql, int fetch, int keep, string columns, params object[] parameters)
    {
        var result = QueryBuilding.Validate(contract, json, QueryLimits.ForRowCap(500));
        Assert.True(result.IsSuccess, result.Failure?.Message);
        var validated = result.Value;
        var compiled = new SqlServerEngine().Compile(validated);
        Assert.Equal(sql, compiled.Sql);
        Assert.Equal(sql.Contains(", COUNT_BIG(*) AS [__dg_matched_rows]", StringComparison.Ordinal), compiled.HasMatchedRowCount);
        Assert.Equal(validated.Columns, compiled.Columns);
        Assert.Equal(fetch, validated.RowLimit.Fetch);
        Assert.Equal(keep, validated.RowLimit.Keep);
        Assert.Equal(keep, compiled.Keep);
        AssertColumns(validated, columns);
        AssertParameters(compiled, parameters);
        AssertCatalogOnly(contract, validated, compiled);
        return (validated, compiled);
    }
}
