using DataGateway.Data.SqlServer;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DataGateway.Core.Tests;
using DataGateway.Demo;
using DataGateway.Data.Queries;
using Xunit;

namespace DataGateway.Web.Tests;

public sealed class DemoTests
{
    [Theory]
    [InlineData("full")]
    [InlineData("dates")]
    [InlineData("groupable")]
    [InlineData("empty")]
    [InlineData("unlisted")]
    [InlineData("unselectable")]
    [InlineData("mutation")]
    public async Task T6_demo_runs_real_routes_and_reports_cap_skips_stops_and_mutations(string scenario)
    {
        using var settings = new ErrorLogSettingsScope();
        var describe = JsonNode.Parse(ReadmeTests.Example("describe").GetRawText())!.AsObject();
        if (scenario == "dates")
        {
            describe["querySchema"]!["columns"]![0]!["dataType"] = "date";
            describe["querySchema"]!["columns"]![0]!["canGroup"] = false;
            describe["querySchema"]!["columns"]![1]!["canFilter"] = false;
        }
        if (scenario == "groupable") describe["querySchema"]!["columns"]![1]!["canGroup"] = true;
        if (scenario == "unselectable") foreach (var c in describe["querySchema"]!["columns"]!.AsArray()) c!["canSelect"] = false;
        describe["source"]!["rowCap"] = 5;
        describe["querySchema"]!["limit"]!["maximum"] = 5;
        var second = describe.DeepClone();
        second["source"]!["name"] = "sales_by_line_other";
        var list = new JsonObject { ["sources"] = scenario == "empty" ? new JsonArray() :
            new JsonArray(describe["source"]!.DeepClone(), second["source"]!.DeepClone()) };
        var descriptions = new Dictionary<string, JsonElement>
        {
            ["sales_by_line"] = JsonSerializer.SerializeToElement(describe), ["sales_by_line_other"] = JsonSerializer.SerializeToElement(second)
        };
        var columnTypes = DemoPlan.Columns(JsonSerializer.SerializeToElement(describe)).ToDictionary(c => c.Name, c => c.DataType);
        var runner = FakeRows.Engine(new SqlServerEngine(), FakeRows.From((_, compiled, connected, _) =>
        {
            connected();
            var sql = compiled.Sql;
            var start = sql.IndexOf("TOP (@p0) ", StringComparison.Ordinal) + "TOP (@p0) ".Length;
            var end = sql.IndexOf(" FROM ", start, StringComparison.Ordinal);
            Assert.True(start >= 10 && end > start, "Compiled SQL must contain the projected select list.");
            var names = sql[start..end].Split(',').Select(item => Regex.Matches(item, @"\[([^\]]+)\]")[^1].Groups[1].Value).ToArray();
            var requested = Convert.ToInt32(compiled.Parameters["@p0"]);
            var count = requested == 6 ? requested : Math.Min(requested, 3);
            IReadOnlyList<dynamic> rows = Enumerable.Range(0, count).Select(_ => (dynamic)names.ToDictionary(name => name, name =>
                name.StartsWith("count_", StringComparison.Ordinal) ? (object?)7m : columnTypes[name] switch
                { "text" => "USA", "number" => 12.5m, "date" => "2024-01-02", "bool" => true, _ => throw new InvalidOperationException() })).ToArray();
            return Task.FromResult(rows);
        }));
        await using var wire = new ReadmeWire(JsonSerializer.SerializeToElement(list), descriptions, runner);
        await wire.Start();
        using var client = wire.Client(scenario == "mutation" ? new RenameMember() : null);
        using var output = new StringWriter();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var report = await DemoRun.RunAsync(client, wire.Anonymous.Client, scenario == "unlisted" ? "absent" : null, output, timeout.Token);
        if (scenario is "empty" or "unlisted" or "unselectable" or "mutation")
        {
            Assert.False(report.Passed);
            Assert.NotNull(report.Stopped);
            if (scenario == "mutation") Assert.Contains(report.Steps.SelectMany(s => s.Problems), p => p.Contains("sources", StringComparison.Ordinal));
        }
        else
        {
            Assert.True(report.Passed, output.ToString());
            Assert.Equal(12, report.Steps.Count);
            Assert.Equal(new[] { "list", "describe sales_by_line", "describe sales_by_line_other", "detail", "filter", "group", "cap", "unknown source", "unknown column", "not groupable", "over cap", "anonymous" }, report.Steps.Select(s => s.Name));
            var skips = report.Steps.Where(s => s.Skipped is not null).ToArray();
            Assert.Equal(scenario == "dates" ? new[] { "filter", "group" } : scenario == "groupable" ? new[] { "not groupable" } : [], skips.Select(s => s.Name));
            Assert.All(skips, s => Assert.False(string.IsNullOrWhiteSpace(s.Skipped)));
            using var cap = JsonDocument.Parse(report.Steps.Single(s => s.Name == "cap").Answer!.Body);
            Assert.True(cap.RootElement.GetProperty("more").GetBoolean());
            Assert.Equal(5, cap.RootElement.GetProperty("rowCount").GetInt32());
            Assert.Contains("Result: PASS", output.ToString());
            Assert.Contains("[12/12] anonymous", output.ToString());
            Assert.Contains("\u2026 (", output.ToString());
        }
    }

    [Fact]
    public void T22_limited_more_requires_exactly_the_limit_rows()
    {
        DemoColumn[] columns = [new("token", "text", true, false, false, false, false)];
        const string full = """{"source":"synthetic","columns":[{"name":"token","dataType":"text"}],"rows":[{"token":"a"},{"token":"b"}],"rowCount":2,"more":true,"hint":"The result set was capped at 2 rows."}""";
        var answer = new DemoAnswer(200, "application/json; charset=utf-8", null, full);
        Assert.Empty(DemoChecks.Rows(answer, "synthetic", columns, 7, 2));
        Assert.Contains("more disagrees with limit or cap", DemoChecks.Rows(answer, "synthetic", columns, 7, 3));
    }

    [Theory]
    [InlineData(1, true, "The result set was capped at 1 row.", true)]
    [InlineData(1, false, "Complete result set.", true)]
    [InlineData(0, false, "Nothing found that matched your query.", true)]
    [InlineData(1, true, "The result set was capped at 1 rows.", false)]
    [InlineData(2, true, "The result set was capped at 2 rows.", true)]
    [InlineData(2, true, "The result set was capped at 2 row.", false)]
    [InlineData(1, true, "The result set was capped at 2 rows.", false)]
    [InlineData(1, true, "Complete result set.", false)]
    [InlineData(1, false, "The result set was capped at 1 row.", false)]
    [InlineData(1, false, "Nothing found that matched your query.", false)]
    [InlineData(0, false, "Complete result set.", false)]
    [InlineData(1, false, "Narrow or aggregate.", false)]
    [InlineData(1, false, "missing", false)]
    [InlineData(1, false, "number", false)]
    [InlineData(1, false, "empty", false)]
    public void T19_hint_is_required_and_is_one_of_the_three_forms(int rowCount, bool more, string hint, bool passes)
    {
        var body = JsonNode.Parse("""{"source":"synthetic","columns":[{"name":"token","dataType":"text"}],"rows":[],"rowCount":0,"more":false}""")!.AsObject();
        for (var i = 0; i < rowCount; i++) body["rows"]!.AsArray().Add(new JsonObject { ["token"] = "a" });
        body["rowCount"] = rowCount;
        body["more"] = more;
        if (hint != "missing") body["hint"] = hint == "number" ? JsonValue.Create(42) : JsonValue.Create(hint == "empty" ? "" : hint);
        var answer = new DemoAnswer(200, "application/json; charset=utf-8", null, body.ToJsonString());
        var problems = DemoChecks.Rows(answer, "synthetic", [new("token", "text", true, false, false, false, false)], Math.Max(1, rowCount), null);
        Assert.Equal(passes, problems.Count == 0);
    }


    private sealed class RenameMember : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var response = await base.SendAsync(request, token);
            if (request.RequestUri!.AbsolutePath == "/sources/list")
            {
                var body = (await response.Content.ReadAsStringAsync(token)).Replace("\"sources\"", "\"renamed\"", StringComparison.Ordinal);
                response.Content.Dispose();
                response.Content = new StringContent(body, Encoding.UTF8, "application/json");
            }
            return response;
        }
    }

    private static DemoAnswer Answer(ReadmeExample example) => new(example.Status!.Value, "application/json; charset=utf-8",
        example.Anonymous ? "Negotiate" : null, ReadmeTests.Compact(example.Answer!.Value));

    internal static IReadOnlyList<string> Check(ReadmeExample example, DemoAnswer answer) => example.Name switch
    {
        "list" => DemoChecks.List(answer), "describe" => DemoChecks.Describe(answer, "sales_by_line"),
        "query" => DemoChecks.Rows(answer, "sales_by_line", [
            new("country", "text", true, true, true, true, false), new("sum_line_total", "number", true, false, true, false, true)], 500, 5),
        "refusal" => DemoChecks.Refusal(answer, 400, "ColumnOperationNotAllowed", "$.query.group_by[0]", DemoChecks.GroupMessage),
        "unknown-source" => DemoChecks.Refusal(answer, 404, "UnknownSource", "$.source", DemoChecks.UnknownSourceMessage(["sales_by_line"])),
        "challenge" => DemoChecks.Anonymous(answer), _ => throw new InvalidOperationException()
    };

    public static IEnumerable<object[]> Answers() => ReadmeTests.Examples.Where(e => e.Answer.HasValue).Select(e => new object[] { e.Name });

    [Theory]
    [MemberData(nameof(Answers))]
    public void T7_readme_answers_pass_and_single_wire_mutations_fail(string name)
    {
        var example = ReadmeTests.Examples.Single(e => e.Name == name);
        var answer = Answer(example);
        Assert.Empty(Check(example, answer));
        Assert.NotEmpty(Check(example, answer with { Status = 599 }));
        Assert.NotEmpty(Check(example, answer with { ContentType = "text/plain" }));
        var original = JsonNode.Parse(answer.Body)!.AsObject();
        var pairs = original.ToArray();
        var renamed = new JsonObject();
        foreach (var pair in pairs) renamed.Add(pair.Key == pairs[0].Key ? "renamed" : pair.Key, pair.Value?.DeepClone());
        Assert.NotEmpty(Check(example, answer with { Body = renamed.ToJsonString() }));
        var swapped = new JsonObject();
        if (pairs.Length > 1)
        {
            foreach (var pair in pairs.Take(2).Reverse().Concat(pairs.Skip(2))) swapped.Add(pair.Key, pair.Value?.DeepClone());
            Assert.NotEmpty(Check(example, answer with { Body = swapped.ToJsonString() }));
        }
        else
        {
            var item = original["sources"]![0]!.AsObject();
            var reordered = new JsonObject();
            foreach (var pair in item.Reverse()) reordered.Add(pair.Key, pair.Value?.DeepClone());
            var list = original.DeepClone(); list["sources"]![0] = reordered;
            Assert.NotEmpty(Check(example, answer with { Body = list.ToJsonString() }));
        }
        var extra = original.DeepClone(); extra["extra"] = true;
        Assert.NotEmpty(Check(example, answer with { Body = extra.ToJsonString() }));
        if (original.ContainsKey("incidentId"))
        {
            var missing = original.DeepClone().AsObject(); missing.Remove("incidentId");
            Assert.NotEmpty(Check(example, answer with { Body = missing.ToJsonString() }));
            var path = original.DeepClone(); path["path"] = "$.wrong";
            Assert.NotEmpty(Check(example, answer with { Body = path.ToJsonString() }));
        }
    }

    [Theory]
    [InlineData("list")]
    [InlineData("describe")]
    public void T7_source_caps_require_a_present_integer_in_range_and_matching_schema(string name)
    {
        var example = ReadmeTests.Examples.Single(e => e.Name == name);
        var answer = Answer(example);
        foreach (var bad in new[] { "missing", "null", "\"500\"", "0", "2147483647", "1.5" })
        {
            var body = JsonNode.Parse(answer.Body)!.AsObject();
            var source = (name == "list" ? body["sources"]![0] : body["source"])!.AsObject();
            if (bad == "missing") source.Remove("rowCap"); else source["rowCap"] = JsonNode.Parse(bad);
            Assert.NotEmpty(Check(example, answer with { Body = body.ToJsonString() }));
        }
        if (name == "describe")
        {
            var body = JsonNode.Parse(answer.Body)!.AsObject(); body["source"]!["rowCap"] = 7;
            Assert.NotEmpty(Check(example, answer with { Body = body.ToJsonString() }));
        }
    }

    [Fact]
    public void T7_row_checks_refuse_bad_counts_types_filters_order_and_more()
    {
        var example = ReadmeTests.Examples.Single(e => e.Name == "query");
        var answer = Answer(example);
        foreach (var mutation in new Action<JsonObject>[]
        {
            b => b["rowCount"] = 8, b => b["more"] = true,
            b => b["rows"]![0]!["sum_line_total"] = "wrong", b => b["columns"]![0]!["dataType"] = "bool",
            b => b["rows"]![0]!["extra"] = 1
        })
        {
            var body = JsonNode.Parse(answer.Body)!.AsObject(); mutation(body);
            Assert.NotEmpty(Check(example, answer with { Body = body.ToJsonString() }));
        }
        var column = new DemoColumn("country", "text", true, true, true, true, false);
        using var text = JsonDocument.Parse("\"usa   \"");
        var filter = new DemoAnswer(200, answer.ContentType, null, """{"source":"sales_by_line","columns":[{"name":"country","dataType":"text"}],"rows":[{"country":"USA"}],"rowCount":1,"more":false,"hint":"Complete result set."}""");
        Assert.Empty(DemoChecks.Rows(filter, "sales_by_line", [column], 500, 3, column, text.RootElement));
        using var other = JsonDocument.Parse("\"Canada\"");
        Assert.NotEmpty(DemoChecks.Rows(filter, "sales_by_line", [column], 500, 3, column, other.RootElement));
        var count = column with { Name = "count_line_total", DataType = "number" };
        var group = filter with { Body = """{"source":"sales_by_line","columns":[{"name":"count_line_total","dataType":"number"}],"rows":[{"count_line_total":1},{"count_line_total":2}],"rowCount":2,"more":false,"hint":"Complete result set."}""" };
        Assert.NotEmpty(DemoChecks.Rows(group, "sales_by_line", [count], 500, 5, descendingCounts: true));
    }

    [Fact]
    public void T8_picks_use_catalog_order_flags_and_canonical_alias()
    {
        DemoColumn C(string name, bool select = true, bool filter = false, bool group = false, bool aggregate = false, string type = "text", bool sort = false) =>
            new(name, type, select, filter, sort, group, aggregate);
        var columns = new[] { C("off", false), C("day", type: "date", filter: true), C("country", group: true), C("line_total", aggregate: true, type: "number"), C("flag", filter: true, type: "bool") };
        var picks = DemoPlan.Pick(columns, 500);
        Assert.Equal(new[] { "day", "country", "line_total", "flag" }, picks.Select.Select(c => c.Name));
        Assert.Equal("flag", picks.Filter!.Name);
        Assert.Equal("country", picks.Group!.Name);
        Assert.Equal("day", picks.NotGroupable!.Name);
        var aggregate = picks.Aggregate! with { Name = "Line_Total" };
        using var noSort = JsonDocument.Parse(DemoPlan.Group("sales_by_line", picks.Group, aggregate));
        Assert.False(noSort.RootElement.GetProperty("query").TryGetProperty("order_by", out _));
        using var sort = JsonDocument.Parse(DemoPlan.Group("sales_by_line", picks.Group, aggregate with { CanSort = true }));
        Assert.Equal("count_Line_Total", sort.RootElement.GetProperty("query").GetProperty("order_by")[0].GetProperty("col").GetString());
        Assert.Null(DemoPlan.Pick([columns[0]], 5).NotGroupable);
        Assert.Empty(DemoPlan.Pick([columns[0]], 5).Select);
    }

    public static IEnumerable<object[]> BadArguments()
    {
        yield return [Array.Empty<string>()];
        foreach (var url in new[] { "localhost:5000", "ftp://localhost", "http://a@localhost", "http://localhost/path", "http://localhost?x", "http://localhost#x" }) yield return [new[] { url }];
        foreach (var tail in new[] { new[] { "--source" }, new[] { "--source", " " }, new[] { "--wrong", "x" }, new[] { "--source", "x", "--source", "y" }, new[] { "http://localhost" } })
            yield return [new[] { "http://localhost:5000" }.Concat(tail).ToArray()];
    }
    [Theory]
    [MemberData(nameof(BadArguments))]
    public void T8_bad_arguments_fail_before_any_request(string[] args)
    {
        var result = DemoProgram.Parse(args);
        Assert.Null(result.Arguments);
        Assert.Contains("Usage:", result.Error);
    }
    [Theory]
    [InlineData("http://localhost:5000")]
    [InlineData("https://localhost/")]
    public void T8_root_urls_and_optional_source_are_accepted(string url)
    {
        Assert.NotNull(DemoProgram.Parse([url]).Arguments);
        Assert.Equal("sales_by_line", DemoProgram.Parse([url, "--source", "sales_by_line"]).Arguments!.Source);
    }
}
