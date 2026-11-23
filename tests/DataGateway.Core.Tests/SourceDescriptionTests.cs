using System.Text;
using System.Text.Json;
using DataGateway.Core.Catalog;
using DataGateway.Core.Queries;
using SqlKata;
using SqlKata.JsonQuery;
using Xunit;

namespace DataGateway.Core.Tests;

/// <summary>Pins <see cref="SourceDescription"/>: the describe-source answer (the source's metadata and its query
/// schema) and its request check.</summary>
/// <remarks>
/// <para>The query schema is what a caller or model reads to write a query, so it must agree with the validator
/// exactly: every column, function and limit the schema offers is accepted, and everything it leaves out is
/// refused. The tests probe the query library directly against the schema to prove that agreement.</para>
/// <para>The answer never carries the connection string, object name or other internal contract fields.</para>
/// </remarks>
public sealed class SourceDescriptionTests
{
    /// <summary>The answer has exactly its documented members, columns in ordinal order, null metadata written as null,
    /// and a documentation-only column listed with every flag false.</summary>
    [Fact]
    public void T2_exact_shape_order_nulls_and_documented_only_column()
    {
        DataSourceColumnRow[] columns =
        [
            Column("alpha", 4), Column("last", 5), Column("Zeta", 4),
            Column("documented", 2, 0), Column("first", 1)
        ];
        var contract = Contract(columns);
        var body = Serialize(SourceDescription.Map(contract, QueryLimits.ForRowCap(500)));
        AssertShape(body);
        Assert.Equal(new[] { "first", "documented", "Zeta", "alpha", "last" },
            body.GetProperty("querySchema").GetProperty("columns").EnumerateArray().Select(c => c.GetProperty("name").GetString()));
        AssertMetadata(body, contract.Source, columns);
        var off = body.GetProperty("querySchema").GetProperty("columns")[1];
        foreach (var flag in new[] { "canSelect", "canFilter", "canSort", "canGroup", "canAggregate" })
            Assert.False(off.GetProperty(flag).GetBoolean());
        foreach (var c in body.GetProperty("querySchema").GetProperty("columns").EnumerateArray())
            foreach (var name in new[] { "unit", "magnitude", "displayFormat" })
                Assert.Equal(JsonValueKind.Null, c.GetProperty(name).ValueKind);
        Assert.Equal(JsonValueKind.Object, body.GetProperty("querySchema").ValueKind);
    }

    /// <summary>For every type and flag combination, each schema clause lists exactly the columns whose flag allows it,
    /// with type-appropriate aggregate functions and the configured row cap as the limit maximum.</summary>
    [Fact]
    public void T3_all_128_flag_combinations_match_every_schema_clause_and_configured_cap()
    {
        var contract = Matrix();
        var limits = QueryLimits.ForRowCap(37);
        var schema = JsonQuerySchema.Create(contract.QueryColumns, limits);
        AssertSchema(schema, contract.Columns, 37);
        foreach (var c in contract.Columns.Where(c => !c.CanSelect && !c.CanFilter && !c.CanSort && !c.CanGroup && !c.CanAggregate))
            Assert.DoesNotContain(JsonSerializer.Serialize(c.Name), schema.GetRawText());
    }

    /// <summary>Every column, aggregate function and limit bound the schema offers is accepted by the validator, and
    /// everything else is refused with the matching code.</summary>
    [Fact]
    public void T4_schema_and_validator_agree_for_all_columns_functions_and_limit_bounds()
    {
        var contract = Matrix();
        var limits = QueryLimits.ForRowCap(37);
        AssertAgreement(contract, JsonQuerySchema.Create(contract.QueryColumns, limits), limits, 37);
        var unbounded = JsonQuerySchema.Create(contract.QueryColumns).GetProperty("properties").GetProperty("limit").GetProperty("maximum").GetInt32();
        Assert.Equal(int.MaxValue, unbounded);
        AssertLimit(contract, limits, unbounded, JsonQueryErrorCode.StructuralLimitExceeded);
    }

    /// <summary>No part of the connection string, object name or internal contract field names appears in the answer.</summary>
    [Fact]
    public void T5_internal_object_connection_and_contract_fields_never_escape()
    {
        var rows = new CatalogRows(new DataSourceRow("hidden_schema.fake_object_x",
            "Server=fake-describe-host;User ID=fake-describe-user;Password=fake-describe-password")
        { Id = 7319,
            Name = "synthetic", Title = "Synthetic", Description = "Measurements", Grain = "One row", QueryTimeoutSeconds = 30, RowCap = 500, IsActive = true
        }, [Column("measure", 1)]);
        var result = CatalogMapping.Map(rows);
        Assert.True(result.IsSuccess, result.Failure?.Message);
        var body = Serialize(SourceDescription.Map(result.Value, QueryLimits.ForRowCap(500)));
        AssertShape(body);
        foreach (var fragment in new[] { "hidden_schema", "fake_object_x", "fake-describe-host", "fake-describe-user",
                     "fake-describe-password", "objectName", "connectionString", "isActive", "queryColumns", "ordinal", "audit" })
            Assert.DoesNotContain(fragment, body.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The requested source name is passed on exactly as sent, case and spaces included; matching it is the
    /// catalog's job.</summary>
    [Theory]
    [InlineData("{\"source\":\"synthetic_sales\"}", "synthetic_sales")]
    [InlineData("{\"source\":\"SYNTHETIC_SALES\"}", "SYNTHETIC_SALES")]
    [InlineData("{\"source\":\"\"}", "")]
    [InlineData("{\"source\":\" synthetic_sales \"}", " synthetic_sales ")]
    public void T6_names_pass_unchanged(string json, string name)
    {
        using var body = JsonDocument.Parse(json);
        var parsed = SourceDescription.ParseRequest(body.RootElement);
        Assert.True(parsed.IsSuccess);
        Assert.Equal(name, parsed.Value);
    }

    /// <summary>A body that is not exactly one string member <c>source</c> is refused with a fixed message that echoes
    /// nothing from the body.</summary>
    [Theory]
    [InlineData("[]", "$")]
    [InlineData("\"x\"", "$")]
    [InlineData("null", "$")]
    [InlineData("1", "$")]
    [InlineData("{}", "$")]
    [InlineData("{\"name\":\"x\"}", "$")]
    [InlineData("{\"Source\":\"x\"}", "$")]
    [InlineData("{\"source\":\"x\",\"extra\":\"fake-input-secret\"}", "$")]
    [InlineData("{\"source\":\"a\",\"source\":\"b\"}", "$")]
    [InlineData("{\"source\":1}", "$.source")]
    [InlineData("{\"source\":null}", "$.source")]
    [InlineData("{\"source\":[\"x\"]}", "$.source")]
    [InlineData("{\"source\":{\"fake-input-secret\":1}}", "$.source")]
    public void T6_structure_rejections_have_constant_safe_messages(string json, string path)
    {
        using var body = JsonDocument.Parse(json);
        AssertRequestFailure(SourceDescription.ParseRequest(body.RootElement), "InvalidStructure", path);
    }

    /// <summary>Invalid UTF-8 in the source value is invalid JSON; in a member name it is an invalid structure; neither
    /// echoes the input.</summary>
    [Fact]
    public void T6_invalid_utf8_value_and_property_name_are_handled_without_echoing_input()
    {
        byte[] value = [.. Encoding.UTF8.GetBytes("{\"source\":\""), 255, 254, .. Encoding.UTF8.GetBytes("\"}")];
        using var body = JsonDocument.Parse(value);
        AssertRequestFailure(SourceDescription.ParseRequest(body.RootElement), "InvalidJson", "$");
        AssertRequestFailure(CatalogResult<string>.Reject(SourceDescription.InvalidJson), "InvalidJson", "$");
        byte[] name = [.. Encoding.UTF8.GetBytes("{\""), 255, 254, .. Encoding.UTF8.GetBytes("\":\"fake-input-secret\"}")];
        using var named = JsonDocument.Parse(name);
        AssertRequestFailure(SourceDescription.ParseRequest(named.RootElement), "InvalidStructure", "$");
    }

    /// <summary>Configured structural limits appear in the schema, and the validator accepts each limit exactly and
    /// refuses one past it.</summary>
    [Fact]
    public void T11_configured_schema_and_validator_agree_on_every_structural_boundary()
    {
        var contract = Matrix();
        var limits = QueryLimits.Create(rowCap: 9, maxJsonLength: 4096, maxFilterDepth: 2, maxFilterConditions: 4, maxInValues: 3);
        AssertStructuralBoundaries(contract, limits);
        Assert.Equal(10, RowCap.Plan(null, limits).Fetch);
        Assert.Equal(9, RowCap.Plan(null, limits).Keep);
    }

    /// <summary>With a filter depth of 0, the schema offers no group depth or group rule and the validator refuses any
    /// nested group.</summary>
    [Fact]
    public void T11_depth_zero_schema_and_validator_allow_only_flat_filters()
    {
        var contract = Matrix();
        var limits = QueryLimits.Create(rowCap: 9, maxJsonLength: JsonQueryLimits.Default.MaxJsonLength,
            maxFilterDepth: 0, maxFilterConditions: JsonQueryLimits.Default.MaxFilterConditions,
            maxInValues: JsonQueryLimits.Default.MaxInValues);
        AssertFlatOnly(contract, limits);
    }

    /// <summary>Limits that overflow, are fractional or strings, are below 1, or exceed the cap are refused by the
    /// library before any row plan is made.</summary>
    [Theory]
    [InlineData("2147483648", JsonQueryErrorCode.InvalidJson, "$")]
    [InlineData("1.5", JsonQueryErrorCode.InvalidJson, "$")]
    [InlineData("\"5\"", JsonQueryErrorCode.InvalidJson, "$")]
    [InlineData("0", JsonQueryErrorCode.InvalidLimit, "limit")]
    [InlineData("-1", JsonQueryErrorCode.InvalidLimit, "limit")]
    [InlineData("10", JsonQueryErrorCode.StructuralLimitExceeded, "limit")]
    public void T11_library_refuses_invalid_limits_before_plan(string literal, JsonQueryErrorCode code, string path)
    {
        var contract = Matrix();
        var selected = JsonSerializer.Serialize(contract.Columns.First(c => c.CanSelect).Name);
        var failure = Probe(contract, QueryLimits.ForRowCap(9), "{\"select\":[" + selected + "],\"limit\":" + literal + "}");
        Assert.Equal(code, failure?.Code);
        Assert.Equal(path, failure?.Path);
    }

    /// <summary>A null limit is accepted as no limit: the query has no limit clause and the plan uses the cap.</summary>
    [Fact]
    public void T11_null_limit_is_accepted_and_has_no_limit_clause()
    {
        var contract = Matrix();
        var limits = QueryLimits.ForRowCap(9);
        var json = JsonSerializer.Serialize(new { select = new[] { contract.Columns.First(c => c.CanSelect).Name }, limit = (int?)null });
        Assert.Null(Probe(contract, limits, json));
        var query = new Query(contract.ObjectName).WithJsonQuery(json, contract.QueryColumns, limits);
        Assert.Null(query.GetOneComponent<LimitClause>("limit"));
        var plan = RowCap.Plan(null, limits);
        Assert.Equal(10, plan.Fetch);
        Assert.Equal(9, plan.Keep);
    }

    /// <summary>The answer's own query schema (the wire form, not the library's JSON Schema) agrees with the flags, the
    /// types and the validator for every combination.</summary>
    [Fact]
    public void T16_wire_matrix_matches_flags_types_library_and_validator()
    {
        var contract = Matrix();
        var limits = QueryLimits.ForRowCap(37);
        var schema = SourceDescription.Map(contract, limits).QuerySchema;
        Assert.Same(contract.Columns, schema.Columns);
        var oracle = JsonQuerySchema.Create(contract.QueryColumns, limits);
        var ops = schema.Filters!.Operators;
        Assert.Equal(new[] { "eq", "neq", "in", "not_in", "is_null", "is_not_null", "lt", "lte", "gt", "gte", "between", "contains", "like" }, ops.Select(o => o.Op));
        Assert.Equal(new[] { "count", "sum", "avg", "min", "max" }, schema.Aggregates!.Functions.Select(f => f.Fn));
        var selected = contract.Columns.First(c => c.CanSelect).Name;
        foreach (var column in contract.Columns)
        {
            foreach (var op in ops)
            {
                var node = new Dictionary<string, object?> { ["col"] = column.Name, ["op"] = op.Op };
                object value = column.DataType switch { "text" => "A", "date" => "2024-01-01", "bool" => true, _ => 1 };
                if (op.Op is not ("is_null" or "is_not_null"))
                    node["value"] = op.Op is "in" or "not_in" ? new[] { value } : op.Op == "between" ? new[] { value, value } : value;
                var failure = Probe(contract, limits, JsonSerializer.Serialize(new { select = new[] { selected }, filters = new[] { node } }));
                Assert.Equal(column.CanFilter && op.Types.Contains(column.DataType), failure is null);
                if (column.CanFilter)
                {
                    var branch = oracle.GetProperty("$defs").GetProperty("filter").GetProperty("oneOf").EnumerateArray()
                        .Single(b => Strings(b.GetProperty("properties").GetProperty("col").GetProperty("enum")).Contains(column.Name));
                    Assert.Equal(op.Types.Contains(column.DataType), Strings(branch.GetProperty("properties").GetProperty("op").GetProperty("enum")).Contains(op.Op));
                }
            }
            foreach (var fn in schema.Aggregates.Functions)
            {
                var failure = Probe(contract, limits, JsonSerializer.Serialize(new { aggregates = new[] { new { fn = fn.Fn, col = column.Name } } }));
                Assert.Equal(column.CanAggregate && fn.Types.Contains(column.DataType), failure is null);
                if (column.CanAggregate) Assert.Equal(fn.Types.Contains(column.DataType), Functions(oracle)[column.Name].Contains(fn.Fn));
            }
        }
        var flags = new[] { 1, 2, 4, 8, 16 };
        foreach (var flag in flags)
        foreach (var type in new[] { "text", "number", "date", "bool" })
        {
            var one = Contract([Column("distinctive", 1, flag, type)]);
            var wire = SourceDescription.Map(one, limits).QuerySchema;
            Assert.Equal(flag == 1, wire.Select is not null);
            Assert.Equal(flag == 2, wire.Filters is not null);
            Assert.Equal(flag == 4, wire.OrderBy is not null);
            Assert.Equal(flag == 8, wire.GroupBy is not null);
            Assert.Equal(flag == 16, wire.Aggregates is not null);
            if (wire.Filters is { } filters)
            {
                var branch = JsonQuerySchema.Create(one.QueryColumns, limits).GetProperty("$defs").GetProperty("filter").GetProperty("oneOf")[0];
                Assert.Equal(Strings(branch.GetProperty("properties").GetProperty("op").GetProperty("enum")), filters.Operators.Select(o => o.Op));
                foreach (var op in filters.Operators) Assert.Equal(new[] { type }, op.Types);
            }
            if (wire.Aggregates is { } aggregates)
            {
                Assert.Equal(TypeFunctions(type), aggregates.Functions.Select(f => f.Fn));
                foreach (var fn in aggregates.Functions) Assert.Equal(new[] { type }, fn.Types);
            }
        }
        foreach (var op in ops)
            Assert.Equal(new[] { "text", "number", "date", "bool" }.Where(t =>
                t is "text" ? op.Op is not ("lt" or "lte" or "gt" or "gte" or "between") :
                t is "bool" ? op.Op is "eq" or "neq" or "in" or "not_in" or "is_null" or "is_not_null" :
                op.Op is not ("contains" or "like")), op.Types);
        foreach (var fn in schema.Aggregates.Functions)
            Assert.Equal(new[] { "text", "number", "date", "bool" }.Where(t => TypeFunctions(t).Contains(fn.Fn)), fn.Types);
    }

    /// <summary>A source mixing types and flags gets a wire schema whose clauses match what the validator accepts.</summary>
    [Fact]
    public void T16_mixed_source_types_follow_clause_flags_and_validator()
    {
        var contract = Contract([
            Column("amount", 1, 1 | 4, "number"),
            Column("label", 2, 31, "text"),
            Column("day", 3, 1 | 2 | 4, "date")
        ]);
        var limits = QueryLimits.ForRowCap(37);
        var schema = SourceDescription.Map(contract, limits).QuerySchema;
        (string Op, string[] Types)[] expected =
        [
            ("eq", ["text", "date"]),
            ("neq", ["text", "date"]),
            ("in", ["text", "date"]),
            ("not_in", ["text", "date"]),
            ("is_null", ["text", "date"]),
            ("is_not_null", ["text", "date"]),
            ("lt", ["date"]),
            ("lte", ["date"]),
            ("gt", ["date"]),
            ("gte", ["date"]),
            ("between", ["date"]),
            ("contains", ["text"]),
            ("like", ["text"])
        ];
        var operators = schema.Filters!.Operators;
        Assert.Equal(expected.Select(o => o.Op), operators.Select(o => o.Op));
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i].Types, operators[i].Types);
            foreach (var type in expected[i].Types)
            {
                var column = contract.Columns.Single(c => c.DataType == type && c.CanFilter);
                var op = expected[i].Op;
                var node = new Dictionary<string, object?> { ["col"] = column.Name, ["op"] = op };
                object value = type == "date" ? "2024-01-01" : "A";
                if (op is not ("is_null" or "is_not_null"))
                    node["value"] = op is "in" or "not_in" ? new[] { value } : op == "between" ? new[] { value, value } : value;
                Assert.Null(Probe(contract, limits, JsonSerializer.Serialize(new { select = new[] { "label" }, filters = new[] { node } })));
            }
        }
        var functions = schema.Aggregates!.Functions;
        Assert.Equal(new[] { "count", "min", "max" }, functions.Select(f => f.Fn));
        foreach (var fn in functions)
        {
            Assert.Equal(new[] { "text" }, fn.Types);
            Assert.Null(Probe(contract, limits, JsonSerializer.Serialize(new { aggregates = new[] { new { fn = fn.Fn, col = "label" } } })));
        }
        foreach (var fn in new[] { "sum", "avg" })
            Assert.Equal(JsonQueryErrorCode.ColumnOperationNotAllowed,
                Probe(contract, limits, JsonSerializer.Serialize(new { aggregates = new[] { new { fn, col = "amount" } } }))?.Code);
    }

    /// <summary>Duplicates within a clause and across group and aggregate output names, empty groups, null and
    /// fractional limits, and select with grouping are refused or accepted as the schema's rule text says.</summary>
    [Fact]
    public void T16_cross_clause_duplicates_empty_groups_and_limits_agree()
    {
        var contract = Contract([Column("token", 1, 31, "text"), Column("count_token", 2, 31, "number")]);
        var limits = QueryLimits.ForRowCap(37);
        foreach (var json in new[]
        {
            "{\"select\":[\"token\",\"TOKEN\"]}",
            "{\"group_by\":[\"token\",\"TOKEN\"]}",
            "{\"order_by\":[{\"col\":\"token\"},{\"col\":\"TOKEN\"}]}",
            "{\"aggregates\":[{\"fn\":\"count\",\"col\":\"token\"},{\"fn\":\"count\",\"col\":\"TOKEN\"}]}",
            "{\"group_by\":[\"count_token\"],\"aggregates\":[{\"fn\":\"count\",\"col\":\"token\"}]}"
        }) Assert.Equal(JsonQueryErrorCode.DuplicateReference, Probe(contract, limits, json)?.Code);
        foreach (var group in new[] { "and", "or" })
            Assert.Equal(JsonQueryErrorCode.InvalidStructure, Probe(contract, limits, "{\"filters\":[{\"" + group + "\":[]}]} ")?.Code);
        Assert.Null(Probe(contract, limits, "{\"limit\":null}"));
        Assert.Equal(JsonQueryErrorCode.InvalidJson, Probe(contract, limits, "{\"limit\":1.5}")?.Code);
        Assert.Equal(JsonQueryErrorCode.InvalidClauseCombination, Probe(contract, limits, "{\"select\":[\"token\"],\"group_by\":[\"token\"]}")?.Code);
        var wire = SourceDescription.Map(contract, limits).QuerySchema;
        Assert.Contains("Cannot be combined", wire.Select!.Rule);
        Assert.Contains("each column at most once", wire.OrderBy!.Rule);
        Assert.Contains("never equal to a group_by column", wire.Aggregates!.Rule);
    }

    /// <summary>The flag and clause names that refusal messages refer to still exist in the answer, so a message never
    /// points at something the caller cannot find.</summary>
    [Fact]
    public void T18_frozen_message_schema_and_flag_referents_remain()
    {
        var body = Serialize(SourceDescription.Map(Matrix(), QueryLimits.ForRowCap(37)));
        Assert.True(body.TryGetProperty("querySchema", out var schema));
        foreach (var flag in new[] { "canSelect", "canFilter", "canSort", "canGroup", "canAggregate" })
        {
            Assert.All(schema.GetProperty("columns").EnumerateArray(), column => Assert.True(column.TryGetProperty(flag, out _)));
            Assert.Contains(flag, schema.GetRawText());
        }
        foreach (var clause in new[] { "select", "filters", "group_by", "aggregates", "order_by", "limit" })
            Assert.True(schema.TryGetProperty(clause, out _));
    }

    /// <summary>Asserts the schema shows <paramref name="limits"/>, and that conditions, in values, group depth, limit
    /// and payload length are each accepted at the limit and refused one past it.</summary>
    /// <remarks>The edge values assume the limits <see cref="T11_configured_schema_and_validator_agree_on_every_structural_boundary"/>
    /// configures.</remarks>
    private static void AssertStructuralBoundaries(SourceContract contract, JsonQueryLimits limits)
    {
        var schema = SourceDescription.Map(contract, limits).QuerySchema;
        Assert.Equal(limits.MaxLimit, schema.Limit.Maximum);
        Assert.Equal(limits.MaxFilterConditions, schema.Filters!.MaxConditions);
        Assert.Equal(limits.MaxFilterDepth, schema.Filters.MaxGroupDepth);
        Assert.Equal(limits.MaxInValues, schema.Filters.MaxInValues);
        var selected = contract.Columns.First(c => c.CanSelect).Name;
        var filterable = contract.Columns.First(c => c.CanFilter).Name;
        var number = contract.Columns.First(c => c.CanFilter && c.DataType == "number").Name;
        string Conditions(int count) => JsonSerializer.Serialize(new { select = new[] { selected },
            filters = Enumerable.Range(0, count).Select(_ => new { col = filterable, op = "is_not_null" }) });
        string InValues(int count) => JsonSerializer.Serialize(new { select = new[] { selected },
            filters = new[] { new { col = number, op = "in", value = Enumerable.Range(1, count).ToArray() } } });
        string Groups(int depth)
        {
            object node = new { col = filterable, op = "is_not_null" };
            for (var i = 0; i < depth; i++) node = new { and = new[] { node } };
            return JsonSerializer.Serialize(new { select = new[] { selected }, filters = new[] { node } });
        }
        Assert.Null(Probe(contract, limits, Conditions(4)));
        StructuralFailure(contract, limits, Conditions(5), "filters");
        Assert.Null(Probe(contract, limits, InValues(3)));
        StructuralFailure(contract, limits, InValues(4), "filters");
        Assert.Null(Probe(contract, limits, Groups(2)));
        StructuralFailure(contract, limits, Groups(3), "filters");
        AssertLimit(contract, limits, limits.MaxLimit!.Value, null);
        AssertLimit(contract, limits, limits.MaxLimit.Value + 1, JsonQueryErrorCode.StructuralLimitExceeded);
        var json = JsonSerializer.Serialize(new { select = new[] { selected } });
        Assert.Null(Probe(contract, limits, json.PadRight(4096)));
        StructuralFailure(contract, limits, json.PadRight(4097), "$");
    }

    /// <summary>Asserts a depth-0 schema offers no grouping and the validator refuses one nested group.</summary>
    private static void AssertFlatOnly(SourceContract contract, JsonQueryLimits limits)
    {
        var schema = SourceDescription.Map(contract, limits).QuerySchema;
        Assert.Null(schema.Filters!.MaxGroupDepth);
        Assert.DoesNotContain("Group conditions", schema.Filters.Rule);
        var selected = contract.Columns.First(c => c.CanSelect).Name;
        var node = new { col = contract.Columns.First(c => c.CanFilter).Name, op = "is_not_null" };
        Assert.Null(Probe(contract, limits, JsonSerializer.Serialize(new { select = new[] { selected }, filters = new[] { node } })));
        StructuralFailure(contract, limits, JsonSerializer.Serialize(new { select = new[] { selected }, filters = new[] { new { and = new[] { node } } } }), "filters");
    }

    /// <summary>Asserts the query is refused for a structural limit at a path starting with <paramref name="path"/>.</summary>
    private static void StructuralFailure(SourceContract contract, JsonQueryLimits limits, string json, string path)
    {
        var failure = Probe(contract, limits, json);
        Assert.Equal(JsonQueryErrorCode.StructuralLimitExceeded, failure?.Code);
        Assert.StartsWith(path, failure?.Path);
    }

    /// <summary>Asserts a describe request was refused with this code, path and the matching fixed message, at step
    /// <c>request</c>, without echoing the input.</summary>
    private static void AssertRequestFailure(CatalogResult<string> result, string code, string path)
    {
        Assert.False(result.IsSuccess);
        var failure = Assert.IsType<CatalogFailure>(result.Failure);
        Assert.Equal(code, failure.Code);
        Assert.Equal(path, failure.Path);
        Assert.Equal("request", failure.Step);
        var expected = code == "InvalidJson"
            ? "The request body is not valid JSON. Send {\"source\": \"<name>\"} with a name from list sources."
            : path == "$" ? "Send a JSON object with exactly one property, source: {\"source\": \"<name>\"} with a name from list sources."
            : "source must be a JSON string: the name of a source from list sources.";
        Assert.Equal(expected, failure.Message);
        Assert.DoesNotContain("fake-input-secret", failure.ToString());
    }

    /// <summary>A synthetic column; bits 1, 2, 4, 8 and 16 of <paramref name="flags"/> switch on select, filter, sort,
    /// group and aggregate.</summary>
    private static DataSourceColumnRow Column(string name, int ordinal, int flags = 31, string type = "number") =>
        new(name, ordinal, "Title " + name, "Meaning " + name, type, null, null, null,
            (flags & 1) != 0, (flags & 2) != 0, (flags & 4) != 0, (flags & 8) != 0, (flags & 16) != 0);

    /// <summary>Maps a synthetic source with <paramref name="columns"/>, asserting the mapping succeeds.</summary>
    private static SourceContract Contract(IReadOnlyList<DataSourceColumnRow> columns)
    {
        var result = CatalogMapping.Map(new(new DataSourceRow("hidden.synthetic", "fixture-connection-removed")
        { QueryTimeoutSeconds = 30, Id = 7319,
            Name = "synthetic", Title = "Synthetic", Description = "Measurements", Grain = "One row",  RowCap = 500, IsActive = true
        }, columns));
        Assert.True(result.IsSuccess, result.Failure?.Message);
        return result.Value;
    }

    /// <summary>A source with one column for every type and flag combination: 4 types times 32 combinations.</summary>
    private static SourceContract Matrix() => Contract(new[] { "text", "number", "date", "bool" }
        .SelectMany((type, index) => Enumerable.Range(0, 32).Select(flags => Column($"c_{type}_{flags}", index * 32 + flags + 1, flags, type))).ToArray());

    /// <summary>The answer as the web serializer writes it.</summary>
    private static JsonElement Serialize(SourceDescribeResponse response) => JsonSerializer.SerializeToElement(response, JsonSerializerOptions.Web);

    /// <summary>Asserts the answer, its source and each column have exactly their documented members.</summary>
    private static void AssertShape(JsonElement body)
    {
        Properties(body, "source", "querySchema");
        Properties(body.GetProperty("source"), "name", "title", "description", "grain", "rowCap");
        foreach (var c in body.GetProperty("querySchema").GetProperty("columns").EnumerateArray())
            Properties(c, "name", "title", "description", "dataType", "unit", "magnitude", "displayFormat",
                "canSelect", "canFilter", "canSort", "canGroup", "canAggregate");
    }

    /// <summary>Asserts <paramref name="value"/> has exactly these members, in any order.</summary>
    private static void Properties(JsonElement value, params string[] names) =>
        Assert.Equal(names.Order(StringComparer.Ordinal), value.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));

    /// <summary>Asserts the answer's source and columns equal the catalog rows, in ordinal order.</summary>
    private static void AssertMetadata(JsonElement body, SourceMetadata source, IReadOnlyList<DataSourceColumnRow> rows)
    {
        Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(source, JsonSerializerOptions.Web), body.GetProperty("source")));
        var expected = rows.OrderBy(c => c.Ordinal).ThenBy(c => c.ColumnName, StringComparer.Ordinal).Select(c => new
        {
            name = c.ColumnName, title = c.Title, description = c.Description, dataType = c.DataType,
            unit = c.Unit, magnitude = c.Magnitude, displayFormat = c.DisplayFormat,
            canSelect = c.CanSelect, canFilter = c.CanFilter, canSort = c.CanSort, canGroup = c.CanGroup, canAggregate = c.CanAggregate
        });
        Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(expected, JsonSerializerOptions.Web), body.GetProperty("querySchema").GetProperty("columns")));
    }

    /// <summary>The strings in a JSON array.</summary>
    private static string[] Strings(JsonElement array) => array.EnumerateArray().Select(v => v.GetString()!).ToArray();
    /// <summary>The item schema of a clause in the library's JSON Schema.</summary>
    private static JsonElement Items(JsonElement schema, string clause) => schema.GetProperty("properties").GetProperty(clause).GetProperty("items");
    /// <summary>The column names a list clause offers; none when its items schema is <c>false</c>.</summary>
    private static string[] Names(JsonElement schema, string clause)
    {
        var items = Items(schema, clause);
        return items.ValueKind == JsonValueKind.False ? [] : Strings(items.GetProperty("enum"));
    }
    /// <summary>The column names the filter schema offers, across its per-type branches.</summary>
    private static string[] FilterNames(JsonElement schema)
    {
        var filter = schema.GetProperty("$defs").GetProperty("filter");
        return filter.ValueKind == JsonValueKind.False ? [] : filter.GetProperty("oneOf").EnumerateArray()
            .SelectMany(b => Strings(b.GetProperty("properties").GetProperty("col").GetProperty("enum"))).ToArray();
    }
    /// <summary>The aggregate functions the schema offers for each column.</summary>
    private static Dictionary<string, string[]> Functions(JsonElement schema)
    {
        var items = Items(schema, "aggregates");
        return items.ValueKind == JsonValueKind.False ? new() : items.GetProperty("oneOf").EnumerateArray()
            .SelectMany(b => Strings(b.GetProperty("properties").GetProperty("col").GetProperty("enum"))
                .Select(c => (Name: c, Functions: Strings(b.GetProperty("properties").GetProperty("fn").GetProperty("enum")))))
            .ToDictionary(pair => pair.Name, pair => pair.Functions, StringComparer.Ordinal);
    }
    /// <summary>The names <c>order_by</c> offers: sortable columns and aggregate outputs.</summary>
    private static string[] SortNames(JsonElement schema)
    {
        var items = Items(schema, "order_by");
        return items.ValueKind == JsonValueKind.False ? [] : Strings(items.GetProperty("properties").GetProperty("col").GetProperty("enum"));
    }
    /// <summary>The aggregate functions each data type allows.</summary>
    private static string[] TypeFunctions(string type) => type switch
    {
        "number" => ["count", "sum", "avg", "min", "max"],
        "text" or "date" => ["count", "min", "max"],
        "bool" => ["count"],
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };
    /// <summary>Asserts two name lists hold the same names, ignoring order.</summary>
    private static void SetEqual(IEnumerable<string> expected, IEnumerable<string> actual) =>
        Assert.Equal(expected.Order(StringComparer.Ordinal), actual.Order(StringComparer.Ordinal));

    /// <summary>Asserts each clause of the library's JSON Schema offers exactly what the column flags and types allow,
    /// and the limit runs from 1 to <paramref name="cap"/>.</summary>
    private static void AssertSchema(JsonElement schema, IReadOnlyList<ColumnMetadata> columns, int cap)
    {
        Assert.Equal(columns.Where(c => c.CanSelect).Select(c => c.Name), Names(schema, "select"));
        Assert.Equal(columns.Where(c => c.CanGroup).Select(c => c.Name), Names(schema, "group_by"));
        SetEqual(columns.Where(c => c.CanFilter).Select(c => c.Name), FilterNames(schema));
        var functions = Functions(schema);
        SetEqual(columns.Where(c => c.CanAggregate).Select(c => c.Name), functions.Keys);
        foreach (var c in columns.Where(c => c.CanAggregate)) SetEqual(TypeFunctions(c.DataType), functions[c.Name]);
        SetEqual(columns.Where(c => c.CanSort).Select(c => c.Name).Concat(columns.Where(c => c.CanSort && c.CanAggregate)
            .SelectMany(c => TypeFunctions(c.DataType).Select(fn => fn + "_" + c.Name))), SortNames(schema));
        var limit = schema.GetProperty("properties").GetProperty("limit");
        Assert.Equal(1, limit.GetProperty("minimum").GetInt32());
        Assert.Equal(cap, limit.GetProperty("maximum").GetInt32());
    }

    /// <summary>Asserts, column by column and clause by clause, that the validator accepts a query exactly when the
    /// schema offers it; documentation-only columns are refused as unknown.</summary>
    private static void AssertAgreement(SourceContract contract, JsonElement schema, JsonQueryLimits limits, int cap)
    {
        var selected = contract.Columns.First(c => c.CanSelect).Name;
        var functions = Functions(schema);
        foreach (var c in contract.Columns)
        {
            var name = c.Name;
            (string Json, bool Accepted)[] probes =
            [
                (JsonSerializer.Serialize(new { select = new[] { name } }), Names(schema, "select").Contains(name)),
                (JsonSerializer.Serialize(new { select = new[] { selected }, filters = new[] { new { col = name, op = "is_null" } } }), FilterNames(schema).Contains(name)),
                (JsonSerializer.Serialize(new { select = new[] { selected }, order_by = new[] { new { col = name, dir = "asc" } } }), SortNames(schema).Contains(name)),
                (JsonSerializer.Serialize(new { group_by = new[] { name } }), Names(schema, "group_by").Contains(name)),
                (JsonSerializer.Serialize(new { aggregates = new[] { new { fn = "count", col = name } } }), functions.ContainsKey(name)),
                (JsonSerializer.Serialize(new { aggregates = new[] { new { fn = "count", col = name } }, order_by = new[] { new { col = "count_" + name, dir = "desc" } } }), SortNames(schema).Contains("count_" + name))
            ];
            foreach (var (json, accepted) in probes)
            {
                var failure = Probe(contract, limits, json);
                Assert.True(accepted == (failure is null), $"{name}: {json}: {failure?.Code}");
                if (!c.CanSelect && !c.CanFilter && !c.CanSort && !c.CanGroup && !c.CanAggregate)
                    Assert.Equal(JsonQueryErrorCode.UnknownColumn, failure?.Code);
            }
            if (c.CanAggregate)
                foreach (var fn in new[] { "count", "sum", "avg", "min", "max" })
                {
                    var failure = Probe(contract, limits, JsonSerializer.Serialize(new { aggregates = new[] { new { fn, col = name } } }));
                    if (functions[name].Contains(fn)) Assert.Null(failure);
                    else Assert.Equal(JsonQueryErrorCode.InvalidAggregateFunction, failure?.Code);
                }
        }
        AssertLimit(contract, limits, cap, null);
        AssertLimit(contract, limits, cap + 1, JsonQueryErrorCode.StructuralLimitExceeded);
        AssertLimit(contract, limits, 0, JsonQueryErrorCode.InvalidLimit);
    }
    /// <summary>Runs the query library's validator on <paramref name="json"/>.</summary>
    /// <returns>The refusal, or null when the query is accepted.</returns>
    private static JsonQueryException? Probe(SourceContract contract, JsonQueryLimits limits, string json)
    {
        try { _ = new Query("synthetic_view").WithJsonQuery(json, contract.QueryColumns, limits); return null; }
        catch (JsonQueryException failure) { return failure; }
    }
    /// <summary>Asserts a select with limit <paramref name="n"/> is accepted, or refused at <c>limit</c> with
    /// <paramref name="code"/>.</summary>
    private static void AssertLimit(SourceContract contract, JsonQueryLimits limits, int n, JsonQueryErrorCode? code)
    {
        var failure = Probe(contract, limits, JsonSerializer.Serialize(new { select = new[] { contract.Columns.First(c => c.CanSelect).Name }, limit = n }));
        if (code is null) Assert.Null(failure);
        else
        {
            Assert.Equal(code, failure?.Code);
            Assert.Equal("limit", failure?.Path);
        }
    }
}
