using DataGateway.Data.SqlServer;
using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using DataGateway.Web.Configuration;
using DataGateway.Core.Catalog;
using DataGateway.Core.Queries;
using DataGateway.Data.Queries;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using SqlKata.JsonQuery;
using Xunit;

namespace DataGateway.Web.Tests;

public sealed class PerSourceRowCapTests
{
    private static readonly string[] Members = ["name", "title", "description", "grain", "rowCap"];

    private sealed class Catalog
    {
        internal readonly ConcurrentDictionary<string, int> Caps = new(new Dictionary<string, int>
        { ["synthetic_small"] = 3, ["synthetic_large"] = 7 });
        internal int ListReads;
        internal int SourceReads;
        internal HostCatalogState ListCatalog() => HostCatalogState.Raw(_ =>
        {
            Interlocked.Increment(ref ListReads);
            return Task.FromResult<IReadOnlyList<dynamic>>(Caps.Select(pair => (dynamic)new Dictionary<string, object?>
            { ["name"] = pair.Key, ["title"] = "Synthetic", ["description"] = "Synthetic", ["grain"] = "One row", ["row_cap"] = pair.Value }).ToArray());
        }, (name, _) => Task.FromResult<(IReadOnlyList<dynamic>, IReadOnlyList<dynamic>)>((ErrorLogTests.Rows(), [])));
        internal HostCatalogState SourceCatalog() => HostCatalogState.Raw(_ => Task.FromResult<IReadOnlyList<dynamic>>([]), (name, _) =>
        {
            Interlocked.Increment(ref SourceReads);
            var cap = Caps[name];
            var rows = ErrorLogTests.Rows();
            foreach (var row in rows) { row["name"] = name; row["row_cap"] = cap; }
            return Task.FromResult<(IReadOnlyList<dynamic>, IReadOnlyList<dynamic>)>((rows, []));
        });
    }

    private static async Task<JsonElement> Pair(LoggedHost host, McpClient mcp, string operation, string body, bool success = true)
    {
        using var response = await host.Client.PostAsync(ErrorLogTests.Route(operation), new StringContent(body));
        var route = await response.Content.ReadAsStringAsync();
        var tool = await McpWire.Call(mcp, operation switch { "list" => "list_sources", "describe" => "describe_source", _ => "query_source" }, body);
        Assert.Equal(success ? HttpStatusCode.OK : HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(!success, tool.IsError == true);
        if (success) Assert.Equal(route, McpWire.Text(tool));
        else McpWire.SameDocument(route, McpWire.Text(tool));
        using var json = JsonDocument.Parse(route);
        return json.RootElement.Clone();
    }

    private static string Body(string name, string query) => "{\"source\":\"" + name + "\",\"query\":" + query + "}";
    private static string Describe(string name) => "{\"source\":\"" + name + "\"}";
    private static void Metadata(JsonElement source, string name, int cap)
    {
        Assert.Equal(Members, source.EnumerateObject().Select(p => p.Name));
        Assert.Equal(name, source.GetProperty("name").GetString());
        Assert.Equal("Synthetic", source.GetProperty("title").GetString());
        Assert.Equal("Synthetic", source.GetProperty("description").GetString());
        Assert.Equal("One row", source.GetProperty("grain").GetString());
        Assert.Equal(cap, source.GetProperty("rowCap").GetInt32());
    }
    private static void Answer(JsonElement answer, int kept, bool more)
    {
        Assert.Equal(kept, answer.GetProperty("rowCount").GetInt32());
        Assert.Equal(kept, answer.GetProperty("rows").GetArrayLength());
        Assert.Equal(more, answer.GetProperty("more").GetBoolean());
        Assert.Equal(more ? "The result set was capped at " + kept + (kept == 1 ? " row." : " rows.") + " Rows are in no particular order." : kept == 0 ? "Nothing found that matched your query." : "Complete result set.",
            answer.GetProperty("hint").GetString());
    }

    [Fact]
    public async Task T1_catalog_caps_drive_list_describe_query_and_live_refresh()
    {
        using var settings = new ErrorLogSettingsScope();
        var catalog = new Catalog();
        var seen = new ConcurrentQueue<CompiledQuery>();
        var available = 8;
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var overlap = false;
        var arrivals = 0;
        var runner = FakeRows.Engine(new SqlServerEngine(), FakeRows.From(async (_, query, connected, token) =>
        {
            connected(); seen.Enqueue(query);
            if (overlap)
            {
                if (Interlocked.Increment(ref arrivals) == 2) barrier.TrySetResult();
                await barrier.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
            }
            var fetch = (int)query.Parameters["@p0"];
            return Enumerable.Range(0, Math.Min(available, fetch)).Select(i => (dynamic)new Dictionary<string, object?> { ["text"] = "row " + i }).ToArray();
        }));
        await using var list = new LoggedHost(catalog.ListCatalog());
        await using var source = new LoggedHost(catalog.SourceCatalog(), runner);
        await list.Start(); await source.Start();
        await using var listMcp = await McpWire.Connect(list.Client);
        await using var sourceMcp = await McpWire.Connect(source.Client);
        var template = source.App.Services.GetRequiredService<JsonQueryLimits>();
        Assert.Null(template.MaxLimit);
        var expectedReads = 0;
        async Task Query(string name, string query, int keep, int rows, bool more)
        {
            var before = catalog.SourceReads;
            Answer(await Pair(source, sourceMcp, "query", Body(name, query)), rows, more);
            Assert.Equal(before + 2, catalog.SourceReads);
            for (var i = 0; i < 2; i++)
            {
                Assert.True(seen.TryDequeue(out var compiled));
                Assert.Equal(keep, compiled.Keep);
                Assert.Contains("TOP (@p0)", compiled.Sql);
                Assert.Equal(checked(keep + 1), compiled.Parameters["@p0"]);
            }
        }
        async Task Round()
        {
            var listing = await Pair(list, listMcp, "list", "{}");
            expectedReads += 2;
            Assert.Equal(expectedReads, catalog.ListReads);
            Assert.Equal(new[] { "synthetic_large", "synthetic_small" }, listing.GetProperty("sources").EnumerateArray().Select(s => s.GetProperty("name").GetString()));
            foreach (var name in new[] { "synthetic_small", "synthetic_large" })
            {
                var cap = catalog.Caps[name];
                Metadata(Assert.Single(listing.GetProperty("sources").EnumerateArray(), s => s.GetProperty("name").GetString() == name), name, cap);
                var before = catalog.SourceReads;
                var description = await Pair(source, sourceMcp, "describe", Describe(name));
                Assert.Equal(before + 2, catalog.SourceReads);
                Metadata(description.GetProperty("source"), name, cap);
                Assert.Equal(cap, description.GetProperty("querySchema").GetProperty("limit").GetProperty("maximum").GetInt32());
                await Query(name, "{\"select\":[\"text\"]}", cap, cap, true);
                await Query(name, "{\"select\":[\"text\"],\"limit\":null}", cap, cap, true);
                await Query(name, "{\"select\":[\"text\"],\"limit\":1}", 1, 1, true);
                await Query(name, "{\"select\":[\"text\"],\"limit\":" + cap + "}", cap, cap, true);
                var refused = await Pair(source, sourceMcp, "query", Body(name, "{\"select\":[\"text\"],\"limit\":" + (cap + 1) + "}"), false);
                Assert.Equal("StructuralLimitExceeded", refused.GetProperty("code").GetString());
                Assert.Equal("$.query.limit", refused.GetProperty("path").GetString());
                Assert.Empty(seen);
            }
        }
        await Round();
        catalog.Caps["synthetic_small"] = 2;
        await Round();
        foreach (var count in new[] { 0, 2, 3 })
        {
            available = count;
            await Query("synthetic_small", "{\"select\":[\"text\"]}", 2, Math.Min(2, count), count > 2);
        }
        available = 8; overlap = true;
        var small = source.Client.PostAsync("/query", new StringContent(Body("synthetic_small", "{\"select\":[\"text\"]}")));
        var large = source.Client.PostAsync("/query", new StringContent(Body("synthetic_large", "{\"select\":[\"text\"]}")));
        var responses = await Task.WhenAll(small, large).WaitAsync(TimeSpan.FromSeconds(15));
        for (var i = 0; i < responses.Length; i++)
        {
            using var response = responses[i];
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Answer(body.RootElement, i == 0 ? 2 : 7, true);
        }
        Assert.Equal(new[] { 2, 7 }, seen.Select(q => q.Keep).Order());
        Assert.Equal(new[] { 3, 8 }, seen.Select(q => (int)q.Parameters["@p0"]).Order());
        Assert.Same(template, source.App.Services.GetRequiredService<JsonQueryLimits>());
        Assert.Equal(JsonQueryLimits.Default with { MaxLimit = null }, template);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(500)]
    [InlineData(700)]
    [InlineData(2147483646)]
    public async Task T2_catalog_boundaries_override_template_and_keep_structural_limits(int cap)
    {
        using var settings = new ErrorLogSettingsScope();
        var catalog = new Catalog(); catalog.Caps["synthetic_small"] = cap; catalog.Caps["synthetic_large"] = 7;
        var template = JsonQueryLimits.Default with { MaxLimit = 1, MaxJsonLength = 2048, MaxFilterDepth = 0, MaxFilterConditions = 3, MaxInValues = 2 };
        var seen = new ConcurrentQueue<CompiledQuery>();
        var runner = FakeRows.Engine(new SqlServerEngine(), FakeRows.From((_, query, connected, _) =>
        {
            connected(); seen.Enqueue(query);
            return Task.FromResult<IReadOnlyList<dynamic>>([new Dictionary<string, object?> { ["text"] = "one" }, new Dictionary<string, object?> { ["text"] = "two" }]);
        }));
        await using var host = new LoggedHost(catalog.SourceCatalog(), runner, new Dictionary<string, string?>
        { ["DataGateway:Limits:MaxJsonLength"] = "2048", ["DataGateway:Limits:MaxFilterDepth"] = "0", ["DataGateway:Limits:MaxFilterConditions"] = "3", ["DataGateway:Limits:MaxInValues"] = "2" },
            configure: b => b.Services.AddSingleton(template));
        await host.Start(); await using var mcp = await McpWire.Connect(host.Client);
        foreach (var name in new[] { "synthetic_small", "synthetic_large" })
        {
            var effective = catalog.Caps[name];
            var schema = (await Pair(host, mcp, "describe", Describe(name))).GetProperty("querySchema");
            Assert.Equal(effective, schema.GetProperty("limit").GetProperty("maximum").GetInt32());
            var filters = schema.GetProperty("filters");
            Assert.False(filters.TryGetProperty("maxGroupDepth", out _));
            Assert.DoesNotContain("Group conditions", filters.GetProperty("rule").GetString());
            Assert.Equal(3, filters.GetProperty("maxConditions").GetInt32());
            Assert.Equal(2, filters.GetProperty("maxInValues").GetInt32());
            foreach (var limit in new[] { "", ",\"limit\":null", ",\"limit\":" + effective }.Concat(effective == 700 ? new[] { ",\"limit\":600" } : []))
            {
                var keep = limit.EndsWith("600", StringComparison.Ordinal) ? 600 : effective;
                Answer(await Pair(host, mcp, "query", Body(name, "{\"select\":[\"text\"]" + limit + "}")), Math.Min(2, keep), keep == 1);
                for (var i = 0; i < 2; i++)
                {
                    Assert.True(seen.TryDequeue(out var compiled));
                    Assert.Equal(keep, compiled.Keep);
                    Assert.Equal(checked(keep + 1), compiled.Parameters["@p0"]);
                }
            }
            foreach (var query in new[] { "{\"limit\":" + ((long)effective + 1) + "}", "{\"filters\":[{\"and\":[{\"col\":\"text\",\"op\":\"eq\",\"value\":\"x\"}]}]}", "{\"filters\":[{\"col\":\"text\",\"op\":\"in\",\"value\":[\"a\",\"b\",\"c\"]}]}" })
            {
                var refusal = await Pair(host, mcp, "query", Body(name, query), false);
                Assert.Equal("StructuralLimitExceeded", refusal.GetProperty("code").GetString());
                Assert.Empty(seen);
            }
        }
        Assert.Same(template, host.App.Services.GetRequiredService<JsonQueryLimits>());
        Assert.Equal(1, template.MaxLimit); Assert.Equal(2048, template.MaxJsonLength);
        Assert.Null(typeof(DataGatewayLimits).GetProperty("RowCap"));
    }

    [Fact]
    public async Task T3_reader_rejects_missing_null_wrong_type_and_out_of_range_caps()
    {
        using var settings = new ErrorLogSettingsScope();
        foreach (var operation in new[] { "list", "describe", "query" })
        {
            var rows = ErrorLogTests.Rows(); rows[1].Remove("row_cap");
            var catalog = HostCatalogState.Raw(_ => Task.FromResult<IReadOnlyList<dynamic>>([rows[1]]),
                (_, _) => Task.FromResult<(IReadOnlyList<dynamic>, IReadOnlyList<dynamic>)>((rows, [])));
            await using var host = new LoggedHost(catalog); await host.Start();
            await using var mcp = await McpWire.Connect(host.Client);
            using var response = await host.Client.PostAsync(ErrorLogTests.Route(operation), new StringContent(ErrorLogTests.Body(operation)));
            var route = await response.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            var tool = await McpWire.Call(mcp, operation == "list" ? "list_sources" : operation == "describe" ? "describe_source" : "query_source", ErrorLogTests.Body(operation));
            Assert.True(tool.IsError); McpWire.SameDocument(route, McpWire.Text(tool));
            using var json = JsonDocument.Parse(route); Assert.Equal("CatalogUnavailable", json.RootElement.GetProperty("code").GetString());
            var normalized = route.Replace(json.RootElement.GetProperty("incidentId").GetString()!, "INCIDENT", StringComparison.Ordinal);
            Assert.Equal(WireGolden.Cases["CatalogUnavailable/route"].Body.Replace("\"path\":\"$.source\"", operation == "list" ? "\"path\":\"$\"" : "\"path\":\"$.source\"", StringComparison.Ordinal), normalized);
            Assert.Equal(2, host.Lines().Length);
            Assert.All(host.Lines(), line => { Assert.Equal("catalog", line.GetProperty("Step").GetString()); Assert.Equal("InvalidCastException", line.GetProperty("ExceptionType").GetString()); });
        }
    }
}
