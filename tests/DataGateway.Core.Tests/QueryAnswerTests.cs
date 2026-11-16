using System.Text;
using System.Text.Json;
using DataGateway.Core.Catalog;
using DataGateway.Core.Queries;
using SqlKata.JsonQuery;
using Xunit;

namespace DataGateway.Core.Tests;

public sealed class QueryAnswerTests
{
    private const string M1 = "The request body is not valid JSON. Send {\"source\": \"<name>\", \"query\": {...}} with a name from list sources.";
    private const string M2 = "Send a JSON object with exactly two properties, source and query: {\"source\": \"<name>\", \"query\": {...}}. Take the name from list sources and the query vocabulary from describe source.";
    private const string M3 = "source must be a JSON string: the name of a source from list sources.";

    [Theory]
    [InlineData("[]", "$")]
    [InlineData("null", "$")]
    [InlineData("1", "$")]
    [InlineData("\"x\"", "$")]
    [InlineData("{}", "$")]
    [InlineData("{\"source\":\"a\"}", "$")]
    [InlineData("{\"query\":{}}", "$")]
    [InlineData("{\"source\":\"a\",\"query\":{},\"limit\":1}", "$")]
    [InlineData("{\"source\":\"a\",\"source\":\"b\"}", "$")]
    [InlineData("{\"source\":\"a\",\"query\":{},\"query\":{}}", "$")]
    [InlineData("{\"Source\":\"a\",\"query\":{}}", "$")]
    [InlineData("{\"source\":\"a\",\"Query\":{}}", "$")]
    [InlineData("{\"source\":null,\"query\":{}}", "$.source")]
    [InlineData("{\"source\":1,\"query\":{}}", "$.source")]
    [InlineData("{\"source\":[],\"query\":{}}", "$.source")]
    [InlineData("{\"source\":{},\"query\":{}}", "$.source")]
    public void T7_structure(string text, string path)
    {
        using var body = JsonDocument.Parse(text);
        Failure(QueryAnswer.ParseRequest(body.RootElement).Failure!, "InvalidStructure", path, path == "$" ? M2 : M3);
    }

    [Theory]
    [InlineData("")]
    [InlineData("/*comment*/{}")]
    [InlineData("{\"source\":\"a\",\"query\":{},}")]
    [InlineData("{}{}")]
    public void T7_json_syntax_is_rejected_by_body_parser(string text)
    {
        Assert.ThrowsAny<JsonException>(() => JsonDocument.Parse(text, new() { MaxDepth = 65 }));
        Failure(QueryAnswer.InvalidJson, "InvalidJson", "$", M1);
    }

    [Fact]
    public void T7_body_depth_above_65_is_invalid_json()
    {
        Assert.ThrowsAny<JsonException>(() => JsonDocument.Parse(new string('[',66) + "0" + new string(']',66), new() { MaxDepth=65 }));
        Failure(QueryAnswer.InvalidJson,"InvalidJson","$",M1);
    }

    [Fact]
    public void T7_invalid_utf8_values_and_name()
    {
        foreach (var (prefix, suffix, code) in new[]
        {
            ("{\"source\":\"", "\",\"query\":{}}", "InvalidJson"),
            ("{\"source\":\"a\",\"query\":{\"x\":\"", "\"}}", "InvalidJson"),
            ("{\"source\":\"a\",\"", "\":{}}", "InvalidStructure")
        })
        {
            byte[] bytes = [.. Encoding.UTF8.GetBytes(prefix), 255, 254, .. Encoding.UTF8.GetBytes(suffix)];
            using var body = JsonDocument.Parse(bytes);
            Failure(QueryAnswer.ParseRequest(body.RootElement).Failure!, code, "$", code == "InvalidJson" ? M1 : M2);
        }
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("\"{}\"")]
    [InlineData("null")]
    [InlineData("5")]
    [InlineData("{ \"limit\" : 2 ,\"limit\":3 }")]
    [InlineData("{\"x\":\"\\u00f6\"}")]
    public void T7_raw_query_and_source_survive(string query)
    {
        foreach (var name in new[] { "", "N\u00f6rth", "a" })
        foreach (var text in new[] { "{\"source\":" + JsonSerializer.Serialize(name) + ",\"query\": " + query + " }",
                     "{\"query\": " + query + ",\"source\":" + JsonSerializer.Serialize(name) + "}" })
        {
            using var body = JsonDocument.Parse(text);
            var request = QueryAnswer.ParseRequest(body.RootElement);
            Assert.True(request.IsSuccess);
            Assert.Equal(new QueryRequest(name, query), request.Value);
        }
    }

    [Theory]
    [InlineData("$", "$.query")]
    [InlineData("", "$.query")]
    [InlineData("select[0]", "$.query.select[0]")]
    [InlineData("filters[1].or[1].and[0].col", "$.query.filters[1].or[1].and[0].col")]
    [InlineData("limit", "$.query.limit")]
    [InlineData("[0]", "$.query[0]")]
    [InlineData("x'; --", "$.query.x'; --")]
    [InlineData("$.source", "$.query.$.source")]
    public void T8_root_preserves_all_diagnostics(string path, string rooted)
    {
        foreach (var code in Enum.GetNames<JsonQueryErrorCode>())
        {
            Assert.True(QueryAnswer.IsRefusal(code));
            var failure = new CatalogFailure(code, path, "unchanged message") { Step = "query", ExceptionType = "Synthetic", SqlNumber = 42 };
            Assert.Equal(failure with { Path = rooted }, QueryAnswer.RootPath(failure));
        }
        foreach (var code in new[] { "UnknownSource", "CatalogUnavailable", "SourceUnavailable", "QueryTimeout", "InvalidCatalog",
                     "CatalogConfiguration", "CatalogCancelled", "QueryCancelled", "Bogus", "invalidjson" })
        {
            Assert.False(QueryAnswer.IsRefusal(code));
            var failure = new CatalogFailure(code, code == "QueryTimeout" ? "$.query" : "$.source", "unchanged");
            Assert.Same(failure, QueryAnswer.RootPath(failure));
        }
    }

    [Fact]
    public void T9_exact_web_json_values_names_order_and_dates()
    {
        object?[] values = [1, 9007199254740993L, 523.0600m, 0.1, 1e20, 0.1f, true, false, "N\u00f6rth <&>", null,
            new DateTime(2024,12,31), new DateTime(2026,10,30,11,5,12,347), new DateTime(2026,10,30,11,5,12),
            DateTime.MinValue, DateTime.MaxValue, (short)2, (byte)3];
        string[] names = ["i", "l", "m", "d", "large", "f", "yes", "no", "text", "nil", "OrderDay", "ms", "seconds", "min", "max", "short", "byte"];
        var contract = Contract(names);
        var validated = QueryBuilding.Validate(contract, "{}", QueryLimits.ForRowCap(1)).Value;
        var input = names.Zip(values).ToDictionary(p => p.First, p => p.Second);
        var capped = new QueryRows([input], more: true);
        var answer = QueryAnswer.Map(contract, validated, capped);
        Assert.Same(validated.Columns, answer.Columns);
        const string expected = """{"source":"synthetic_sales","columns":[{"name":"i","dataType":"text"},{"name":"l","dataType":"text"},{"name":"m","dataType":"text"},{"name":"d","dataType":"text"},{"name":"large","dataType":"text"},{"name":"f","dataType":"text"},{"name":"yes","dataType":"text"},{"name":"no","dataType":"text"},{"name":"text","dataType":"text"},{"name":"nil","dataType":"text"},{"name":"OrderDay","dataType":"text"},{"name":"ms","dataType":"text"},{"name":"seconds","dataType":"text"},{"name":"min","dataType":"text"},{"name":"max","dataType":"text"},{"name":"short","dataType":"text"},{"name":"byte","dataType":"text"}],"rows":[{"i":1,"l":9007199254740993,"m":523.0600,"d":0.1,"large":1E+20,"f":0.1,"yes":true,"no":false,"text":"N\u00F6rth \u003C\u0026\u003E","nil":null,"OrderDay":"2024-12-31","ms":"2026-10-30T11:05:12.347","seconds":"2026-10-30T11:05:12","min":"0001-01-01","max":"9999-12-31T23:59:59.9999999","short":2,"byte":3}],"rowCount":1,"more":true,"hint":"The result set was capped at 1 row. Rows are in no particular order."}""";
        Assert.Equal(expected, JsonSerializer.Serialize(answer, JsonSerializerOptions.Web));
        Assert.Same(input, answer.Rows[0]);
    }

    [Theory]
    [InlineData(true, 1, "The result set was capped at 1 row.")]
    [InlineData(true, 2, "The result set was capped at 2 rows.")]
    [InlineData(true, 3, "The result set was capped at 3 rows.")]
    [InlineData(true, 500, "The result set was capped at 500 rows.")]
    [InlineData(false, 0, "Nothing found that matched your query.")]
    [InlineData(false, 1, "Complete result set.")]
    [InlineData(false, 7, "Complete result set.")]
    public void T9_hint_is_a_plain_fact_from_more_and_rowcount_and_is_the_last_member(bool more, int count, string hint)
    {
        var rows = Enumerable.Range(0, count).Select(_ => (IDictionary<string, object?>)new Dictionary<string, object?>()).ToArray();
        var answer = new QueryResponse("synthetic_sales", [], rows, count, more);
        Assert.Equal(hint, answer.Hint);
        Assert.Equal("{\"source\":\"synthetic_sales\",\"columns\":[],\"rows\":" + JsonSerializer.Serialize(rows) + ",\"rowCount\":" + count
            + ",\"more\":" + (more ? "true" : "false") + ",\"hint\":\"" + hint + "\"}", JsonSerializer.Serialize(answer, JsonSerializerOptions.Web));
    }

    [Fact]
    public void T9_aggregate_empty_and_mismatch()
    {
        var contract = Contract(["OrderDay"]);
        var query = QueryBuilding.Validate(contract, """{"aggregates":[{"fn":"count","col":"OrderDay"}]}""", QueryLimits.ForRowCap(7)).Value;
        Assert.Equal("""{"source":"synthetic_sales","columns":[{"name":"count_OrderDay","dataType":"number"}],"rows":[{"count_OrderDay":6431}],"rowCount":1,"more":false,"hint":"Complete result set."}""",
            JsonSerializer.Serialize(QueryAnswer.Map(contract, query, new QueryRows([new Dictionary<string, object?> { ["count_OrderDay"] = 6431 }], false)), JsonSerializerOptions.Web));
        Assert.Equal("""{"source":"synthetic_sales","columns":[{"name":"count_OrderDay","dataType":"number"}],"rows":[],"rowCount":0,"more":false,"hint":"Nothing found that matched your query."}""",
            JsonSerializer.Serialize(QueryAnswer.Map(contract, query, new QueryRows([], false)), JsonSerializerOptions.Web));
        foreach (var row in new[] { new Dictionary<string, object?>(), new Dictionary<string, object?> { ["wrong"] = "fake-secret" },
                     new Dictionary<string, object?> { ["count_OrderDay"] = 1, ["extra"] = "fake-secret" },
                     new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["COUNT_ORDERDAY"] = 1 } })
        {
            var error = Assert.Throws<InvalidOperationException>(() => QueryAnswer.Map(contract, query, new QueryRows([row], false)));
            Assert.Equal("The query row does not match its output columns.", error.Message);
        }
    }

    [Fact]
    public void T12_reordered_first_row_is_refused()
    {
        var contract = Contract(["first", "second"]);
        var query = QueryBuilding.Validate(contract, "{}", QueryLimits.ForRowCap(7)).Value;
        var row = new Dictionary<string, object?> { ["second"] = 2, ["first"] = 1 };
        var error = Assert.Throws<InvalidOperationException>(() => QueryAnswer.Map(contract, query, new QueryRows([row], false)));
        Assert.Equal("The query row does not match its output columns.", error.Message);
    }

    private static SourceContract Contract(string[] names) => CatalogMapping.Map(new(new DataSourceRow("hidden_schema.order_lines", "fixture-removed")
    { Id = 7319,
        Name = "synthetic_sales", Title = "Synthetic", Description = "Synthetic rows", Grain = "One line", RowCap = 500, IsActive = true
    }, names.Select((n,i) => new DataSourceColumnRow(n,i+1,n,"Meaning", "text",null,null,null,true,true,true,true,true)).ToArray())).Value;

    private static void Failure(CatalogFailure failure, string code, string path, string message) =>
        Assert.Equal((code, path, message, "request"), (failure.Code, failure.Path, failure.Message, failure.Step));
}

