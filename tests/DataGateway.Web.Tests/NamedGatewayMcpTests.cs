using Microsoft.Data.Sqlite;
using System.Net;
using System.Text;
using System.Text.Json;
using DataGateway.Core.Catalog;
using DataGateway.Core.Queries;
using DataGateway.Data;
using DataGateway.Data.Catalog;
using DataGateway.Data.Queries;
using DataGateway.Data.Sqlite;
using DataGateway.Failures;
using DataGateway.Mcp;
using DataGateway.Routes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SqlKata.JsonQuery;
using Xunit;
using static DataGateway.Web.Tests.NamedGatewayMcpWire;

namespace DataGateway.Web.Tests;

public sealed class NamedGatewayMcpTests
{
    private static readonly string[] Operations = ["list", "describe", "query"];
    private static readonly string?[] Protocols = [null, "2025-11-25"];
    private const string Secret = "synthetic-private-fault-sentinel";

    [Fact]
    public async Task T01_each_gateway_has_its_own_identity_and_instructions()
    {
        await using var pair = new NamedGatewayMcpPair();
        await pair.Start();
        foreach (var protocol in Protocols)
        {
            await using var a = await pair.Connect("a", protocol);
            await using var b = await pair.Connect("b", protocol);
            for (var round = 0; round < 2; round++)
                foreach (var (client, name) in new[] { (a, "a"), (b, "b") })
                {
                    Identity(client, name, "Title " + name.ToUpperInvariant(), "About " + name.ToUpperInvariant() + ".");
                    Assert.False((await McpWire.Call(client, "list_sources", "{}", pair.Token)).IsError == true);
                    Identity(client, name, "Title " + name.ToUpperInvariant(), "About " + name.ToUpperInvariant() + ".");
                }
        }
        pair.Capture.Stateless("/north/mcp", "/south/mcp");
        Assert.Contains(pair.Capture.Responses, item => item.Status == 202);
        Assert.Contains(pair.Capture.Responses, item => item.Status == 200);
    }

    [Fact]
    public async Task T02_catalog_text_is_preserved_without_trimming()
    {
        await using var pair = new NamedGatewayMcpPair();
        pair.Sa.Metadata = McpMetadataFixture.Seed with { Title = "  Retail  ", About = new string('x', 1000) };
        await pair.Start();
        await using var client = await pair.Connect("a");
        Identity(client, "a", "  Retail  ", new string('x', 1000));
    }

    [Fact]
    public async Task T03_invalid_catalog_metadata_refuses_startup()
    {
        await using var pair = new NamedGatewayMcpPair();
        pair.Sa.Metadata = McpMetadataFixture.Seed with { Title = "bad\nvalue" };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => pair.Start());
        Assert.Contains("mcpserver.title", error.Message);
    }

    [Fact]
    public async Task T04_catalog_edits_are_independent_of_frozen_builder()
    {
        using var fixture = new SqliteFixture();
        var state = new NamedGatewayState(fixture);
        DataGatewayBuilder saved = null!;
        await using var host = new NamedGatewayHost(builder => builder.Services.AddDataGateway("a", gateway =>
        {
            saved = gateway;
            gateway.UseCatalog(state.Create).AddEngine(new NamedGatewayEngine());
        }));
        host.App.MapDataGateway("a", "/a");
        await host.Start();
        state.Metadata = state.Metadata with { Title = "Retail", About = "Retail data." };
        using var deadline = Deadline();
        await using var client = await Connect(host.Client, "/a/mcp", null, deadline.Token);
        Identity(client, "a", "Retail", "Retail data.");
        Frozen(saved);
    }

    [Fact]
    public async Task T05_each_stateless_post_reads_metadata_and_creates_one_catalog()
    {
        await using var pair = new NamedGatewayMcpPair();
        await pair.Start();
        foreach (var (state, path) in new[] { (pair.Sa, "/north/mcp"), (pair.Sb, "/south/mcp") })
        {
            var start = state.Counts;
            var metadata = state.MetadataReads;
            var bodies = new[]
            {
                "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-11-25\",\"capabilities\":{},\"clientInfo\":{\"name\":\"retail\",\"version\":\"1\"}}}",
                LegacyList, LegacyList
            };
            for (var index = 0; index < bodies.Length; index++)
            {
                using var response = await Raw(pair.Http, path, bodies[index], pair.Token);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.False(response.Headers.Contains("Mcp-Session-Id"));
                Assert.Equal(metadata + index + 1, state.MetadataReads);
                Assert.Equal((start.Item1 + index + 1, start.Item2, start.Item3), state.Counts);
            }
        }
        await using var client = await pair.Connect("a");
        await Definitions(client, pair.Token);
        pair.Capture.Stateless("/north/mcp", "/south/mcp");
    }

    [Fact]
    public async Task T06_alternating_concurrent_calls_keep_gateway_witnesses()
    {
        await using var pair = new NamedGatewayMcpPair();
        await pair.Start();
        await using var a = await pair.Connect("a");
        await using var b = await pair.Connect("b");
        foreach (var operation in Operations)
            foreach (var gateway in new[] { "a", "b" })
                await Successful(pair, gateway == "a" ? a : b, gateway, operation);
        var left = Gate();
        var right = Gate();
        var release = Gate();
        pair.Sa.BeforeList = pair.Sa.BeforeDetail = async token =>
        {
            left.TrySetResult();
            await release.Task.WaitAsync(token);
        };
        pair.Sb.BeforeList = pair.Sb.BeforeDetail = async token =>
        {
            right.TrySetResult();
            await release.Task.WaitAsync(token);
        };
        var pending = Enumerable.Range(0, 12).Select(index => Successful(pair, index % 2 == 0 ? a : b,
            index % 2 == 0 ? "a" : "b", Operations[index % 3])).ToArray();
        try
        {
            await Task.WhenAll(left.Task, right.Task).WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            release.TrySetResult();
        }
        await Task.WhenAll(pending);
        pair.Sa.BeforeList = pair.Sa.BeforeDetail = null;
        pair.Sb.BeforeList = pair.Sb.BeforeDetail = null;
        await Task.WhenAll(FailureForCaller(pair, "a", "caller-a", "absent_a"),
            FailureForCaller(pair, "b", "caller-b", "absent_b"));
        foreach (var (engine, fixture, table) in new[] { (pair.Ea, pair.A, "proof_a_items"), (pair.Eb, pair.B, "proof_b_items") })
            foreach (var execution in engine.Executed)
            {
                Assert.Equal(fixture.Connection, execution.Connection);
                Assert.Contains(table, execution.Query.Sql);
                Assert.Contains(engine.Compiled, query => ReferenceEquals(query, execution.Query));
                Assert.Equal(30, execution.Timeout);
            }
        Assert.Equal(2, pair.Host.Logs.Failures.Length);
        pair.Capture.Stateless("/north/mcp", "/south/mcp");
    }

    [Fact]
    public async Task T07_cross_gateway_sources_are_unknown_locally()
    {
        await using var pair = new NamedGatewayMcpPair();
        await pair.Start();
        foreach (var gateway in new[] { "a", "b" })
        {
            await using var client = await pair.Connect(gateway);
            var foreign = gateway == "a" ? pair.Sb : pair.Sa;
            var snapshot = foreign.Counts;
            foreach (var operation in new[] { "describe", "query" })
            {
                var body = NamedGatewayHost.Body(operation, gateway == "a" ? "synthetic_b_only" : "synthetic_a_only");
                var result = await McpWire.Call(client, Tool(operation), body, pair.Token);
                var incident = Failed(result, Unknown(gateway));
                Event(pair.Host, incident, gateway, Tool(operation), body, "UnknownSource");
                Assert.Equal(snapshot, foreign.Counts);
            }
        }
        Assert.Equal(4, pair.Host.Logs.Failures.Length);
        Assert.Empty(pair.Ea.Executed);
        Assert.Empty(pair.Eb.Executed);
    }

    [Fact]
    public async Task T08_calls_create_fresh_scoped_catalog_adapters()
    {
        await using var pair = new NamedGatewayMcpPair(builder =>
            builder.Services.AddSingleton<CatalogService<SqliteConnection>>(_ => throw new SyntheticFault("unkeyed must not resolve")));
        await pair.Start();
        await using var a = await pair.Connect("a");
        var start = pair.Sa.Counts;
        var instances = pair.Sa.Instances.Count;
        var calls = Enumerable.Range(0, 9).Select(index => McpWire.Call(a, Tool(Operations[index % 3]),
            NamedGatewayHost.Body(Operations[index % 3], index == 4 ? "missing" : "synthetic_items"), pair.Token).AsTask()).ToArray();
        await Task.WhenAll(calls);
        Assert.Equal(start.Item1 + 9, pair.Sa.Factories);
        Assert.Equal(start.Item2 + 4, pair.Sa.Lists);
        Assert.Equal(start.Item3 + 6, pair.Sa.Details);
        var created = pair.Sa.Instances.Skip(instances).ToArray();
        Assert.Equal(9, created.Length);
        Assert.Equal(9, created.Select(item => item.Catalog).Distinct().Count());
        Assert.Equal(9, created.Select(item => item.Scope).Distinct().Count());
        var concurrentReads = pair.Sa.Reads.Where(read => created.Any(item => item.Catalog == read.Catalog.Id)).ToArray();
        Assert.Equal(19, concurrentReads.Length);
        Assert.Equal(9, concurrentReads.Count(read => read.Operation == "metadata"));
        Assert.Equal(9, concurrentReads.Select(read => read.Catalog).Distinct().Count());
        NamedGatewayCatalog? previous = null;
        for (var call = 0; call < 2; call++)
        {
            var before = pair.Sa.Reads.Count;
            var result = await McpWire.Call(a, "describe_source", NamedGatewayHost.Body("describe", "missing"), pair.Token);
            Failed(result, Unknown("a"));
            var allReads = pair.Sa.Reads.Skip(before).ToArray();
            Assert.Equal(3, allReads.Length);
            Assert.Equal("metadata", allReads[0].Operation);
            Assert.All(allReads, read => Assert.Same(allReads[0].Catalog, read.Catalog));
            var reads = allReads.Where(read => read.Operation != "metadata").ToArray();
            Assert.Equal(2, reads.Length);
            Assert.Equal("detail", reads[0].Operation);
            Assert.Equal("missing", reads[0].Source);
            Assert.Equal("list", reads[1].Operation);
            Assert.Null(reads[1].Source);
            Assert.True(ReferenceEquals(reads[0].Catalog, reads[1].Catalog), "T08 within-call catalog identity: missing detail and list fallback must read the same instance.");
            Assert.True(!concurrentReads.Any(read => ReferenceEquals(read.Catalog, reads[0].Catalog))
                && !ReferenceEquals(previous, reads[0].Catalog), "T08 across-call catalog freshness: different calls must read different instances.");
            previous = reads[0].Catalog;
        }
        var snapshot = pair.Sa.Counts;
        var metadataBefore = pair.Sa.MetadataReads;
        foreach (var operation in Operations)
        {
            var body = operation == "list" ? "{\"extra\":1}" : "{}";
            Assert.True((await McpWire.Call(a, Tool(operation), body, pair.Token)).IsError);
        }
        Assert.Equal((snapshot.Item1 + 3, snapshot.Item2, snapshot.Item3), pair.Sa.Counts);
        Assert.Equal(metadataBefore + 3, pair.Sa.MetadataReads);
        await a.DisposeAsync();
        await pair.Host.App.StopAsync(pair.Token);
        Assert.All(created, item => Assert.Contains(item.Scope, pair.Host.Cleanup.Disposed));
    }

    [Fact]
    public async Task T09_provider_aliases_select_the_correct_runner()
    {
        using var fixture = new SqliteFixture();
        var state = new NamedGatewayState(fixture) { Provider = name => name == "synthetic_items" ? "SQLITE" : "Sqlite-Alt" };
        var first = new NamedGatewayEngine();
        var second = new NamedGatewayEngine();
        await using var host = new NamedGatewayHost(builder => builder.Services.AddDataGateway("a", gateway =>
            gateway.UseCatalog(state.Create).AddEngine(first).AddEngine(second, "sqlite-alt")));
        host.App.MapDataGateway("a", "/runner");
        await host.Start();
        using var deadline = Deadline();
        await using var client = await Connect(host.Client, "/runner/mcp", null, deadline.Token);
        var entered = Gate();
        var release = Gate();
        var count = 0;
        first.BeforeExecute = second.BeforeExecute = async token =>
        {
            if (Interlocked.Increment(ref count) == 2)
                entered.TrySetResult();
            await release.Task.WaitAsync(token);
        };
        var bodies = new[] { NamedGatewayHost.Body("query"), """{"source":"synthetic_other","query":{"select":["label"],"limit":2}}""" };
        var calls = bodies.Select(body => McpWire.Call(client, "query_source", body, deadline.Token).AsTask()).ToArray();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            release.TrySetResult();
        }
        Assert.All(await Task.WhenAll(calls), result => Assert.False(result.IsError == true));
        foreach (var (engine, column, body) in new[] { (first, "id", bodies[0]), (second, "label", bodies[1]) })
        {
            var compiled = Assert.Single(engine.Compiled);
            var executed = Assert.Single(engine.Executed);
            Assert.Same(compiled, executed.Query);
            Assert.Equal(fixture.Connection, executed.Connection);
            Assert.Equal(30, executed.Timeout);
            Assert.Equal(column, Assert.Single(compiled.Columns).Name);
            var source = await fixture.Source();
            using var parsed = JsonDocument.Parse(body);
            var validated = QueryBuilding.Validate(source, parsed.RootElement.GetProperty("query").GetRawText(), QueryLimits.ForRowCap(3));
            var expected = new SqliteEngine().Compile(validated.Value);
            Assert.Equal(expected.Sql, compiled.Sql);
            Assert.Equal(expected.Columns, compiled.Columns);
            Assert.Equal(JsonSerializer.Serialize(expected.Parameters), JsonSerializer.Serialize(compiled.Parameters));
        }
        state.Provider = _ => "missing-engine";
        var failed = await McpWire.Call(client, "query_source", bodies[0], deadline.Token);
        var id = Failed(failed, WireGolden.Cases["InvalidCatalog/tool"].Text[0]);
        var line = Event(host, id, "a", "query_source", bodies[0], "InvalidCatalog");
        Assert.Equal("$.detail.provider", line.Fields["Path"]);
        Assert.Equal("Source 'synthetic_items'. Register an engine named 'missing-engine' for this gateway.", line.Fields["Message"]);
        Assert.Single(first.Executed);
        Assert.Single(second.Executed);
    }

    [Fact]
    public async Task T10_catalog_edits_and_library_defaults_are_observed()
    {
        var alternative = new NamedGatewayEngine();
        using var fixture = new SqliteFixture();
        var state = new NamedGatewayState(fixture);
        var engine = new NamedGatewayEngine();
        await using var host = new NamedGatewayHost(builder => builder.Services.AddDataGateway("a", gateway =>
            gateway.UseCatalog(state.Create).AddEngine(engine).AddEngine(alternative, "sqlite-alt")));
        host.App.Services.GetRequiredService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>().Value.SerializerOptions.Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Default;
        host.App.MapDataGateway("a", "/fresh");
        await host.Start();
        using var deadline = Deadline();
        await using var client = await Connect(host.Client, "/fresh/mcp", null, deadline.Token);
        const string safeTitle = "A \"quoted\" \u00e9 <value>";
        fixture.Change("UPDATE gateway_source SET title=@v0 WHERE name='synthetic_items'", safeTitle);
        foreach (var operation in new[] { "list", "describe" })
        {
            var text = McpWire.Text(await McpWire.Call(client, Tool(operation), NamedGatewayHost.Body(operation), deadline.Token));
            Assert.Equal((await host.Post("/fresh", operation)).Text, text);
            using var json = JsonDocument.Parse(text);
            var source = operation == "list" ? json.RootElement.GetProperty("sources")[0] : json.RootElement.GetProperty("source");
            Assert.Equal(safeTitle, source.GetProperty("title").GetString());
            // The host switched its own JSON encoder; DataGateway's wire keeps the library's relaxed escaping.
            Assert.Contains("é", text);
            Assert.Contains("<value>", text);
            Assert.DoesNotContain("\\u00E9", text);
        }
        fixture.Change("UPDATE gateway_column SET can_filter=0 WHERE column_name='id'");
        var description = McpWire.Text(await McpWire.Call(client, "describe_source", NamedGatewayHost.Body("describe"), deadline.Token));
        using (var json = JsonDocument.Parse(description))
            Assert.False(json.RootElement.GetProperty("querySchema").GetProperty("columns")[0].GetProperty("canFilter").GetBoolean());
        state.Provider = _ => "SQLITE-ALT";
        const string all = """{"source":"synthetic_items","query":{"select":["id"],"order_by":[{"col":"id","dir":"asc"}]}}""";
        var capped = McpWire.Text(await McpWire.Call(client, "query_source", all, deadline.Token));
        Assert.Equal("""{"source":"synthetic_items","columns":[{"name":"id","dataType":"number"}],"rows":[{"id":1},{"id":2},{"id":3}],"rowCount":3,"more":true,"hint":"The result set was capped at 3 rows."}""", capped);
        Assert.Empty(engine.Executed);
        Assert.Single(alternative.Executed);
        var before = alternative.Compiled.Count;
        Assert.True((await McpWire.Call(client, "query_source", """{"source":"synthetic_items","query":{"limit":4}}""", deadline.Token)).IsError);
        Assert.Equal(before, alternative.Compiled.Count);
        fixture.Change("UPDATE gateway_source SET row_cap=1500 WHERE name='synthetic_items'");
        var large = McpWire.Text(await McpWire.Call(client, "query_source", """{"source":"synthetic_items","query":{"select":["id"],"order_by":[{"col":"id","dir":"asc"}],"limit":1001}}""", deadline.Token));
        Assert.Equal("""{"source":"synthetic_items","columns":[{"name":"id","dataType":"number"}],"rows":[{"id":1},{"id":2},{"id":3},{"id":4},{"id":5}],"rowCount":5,"more":false,"hint":"Complete result set."}""", large);
        var registration = host.App.Services.GetRequiredService<GatewayRegistry>().Find("a")!;
        Assert.Equal(JsonQueryLimits.Default with { MaxLimit = null }, registration.Limits);
        Assert.Null(registration.Limits.MaxLimit);
        fixture.Change("UPDATE gateway_column SET can_filter=1 WHERE column_name='id'");
        var filters = Enumerable.Range(0, registration.Limits.MaxFilterConditions + 1).Select(_ => new { col = "id", op = "eq", value = 1 });
        var excessive = JsonSerializer.Serialize(new { source = "synthetic_items", query = new { filters } });
        before = alternative.Compiled.Count;
        Assert.True((await McpWire.Call(client, "query_source", excessive, deadline.Token)).IsError);
        Assert.Equal(before, alternative.Compiled.Count);
        Assert.All(alternative.Executed, execution => Assert.Equal(30, execution.Timeout));
    }

    [Fact]
    public async Task T11_returned_group_authorization_blocks_catalog_reads()
    {
        foreach (var nested in new[] { false, true })
        {
            using var a = new SqliteFixture();
            using var b = new SqliteFixture();
            b.Change("UPDATE gateway_source SET title='Protected B' WHERE name='synthetic_items'");
            var sa = new NamedGatewayState(a);
            var sb = new NamedGatewayState(b);
            var engine = new NamedGatewayEngine();
            await using var host = new NamedGatewayHost(builder =>
            {
                NamedGatewayHost.Register(builder.Services, "a", sa);
                NamedGatewayHost.Register(builder.Services, "b", sb, engine);
            });
            host.App.MapDataGateway("a", "/open");
            IEndpointRouteBuilder parent = nested ? host.App.MapGroup("/parent") : host.App;
            parent.MapDataGateway("b", "/protected").RequireAuthorization();
            var prefix = nested ? "/parent/protected" : "/protected";
            await host.Start();
            var baseline = sb.Counts;
            var instances = sb.Instances.Count;
            using var deadline = Deadline();
            using var capture = new Capture();
            using var http = Http(host.Client.BaseAddress!, capture);
            foreach (var envelope in new[]
            {
                """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"synthetic","version":"1.0"}}}""",
                LegacyList,
                """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"query_source","arguments":{"source":"synthetic_items","query":{"limit":2}}}}"""
            })
            {
                using var response = await Raw(http, prefix + "/mcp", envelope, deadline.Token);
                Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            }
            Assert.Equal(baseline, sb.Counts);
            Assert.Equal(instances, sb.Instances.Count);
            Assert.Empty(engine.Executed);
            Assert.Empty(host.Logs.Failures);
            http.DefaultRequestHeaders.Add("X-Synthetic-User", "signed-reader");
            await using var signed = await Connect(http, prefix + "/mcp", "2025-11-25", deadline.Token);
            var text = McpWire.Text(await McpWire.Call(signed, "describe_source", NamedGatewayHost.Body("describe"), deadline.Token));
            using (var json = JsonDocument.Parse(text))
                Assert.Equal("Protected B", json.RootElement.GetProperty("source").GetProperty("title").GetString());
            await using var open = await Connect(http, "/open/mcp", null, deadline.Token);
            var openText = McpWire.Text(await McpWire.Call(open, "describe_source", NamedGatewayHost.Body("describe"), deadline.Token));
            using (var json = JsonDocument.Parse(openText))
                Assert.Equal("Synthetic", json.RootElement.GetProperty("source").GetProperty("title").GetString());
            capture.Stateless(prefix + "/mcp", "/open/mcp");
            Assert.Contains(capture.Responses, response => response.Status == 401);
        }
    }

    [Fact]
    public async Task T12_argument_failures_use_the_selected_log_once()
    {
        await using var pair = new NamedGatewayMcpPair();
        await pair.Start();
        var cases = new[]
        {
            ("list", "{\"extra\":1}", "{\"code\":\"InvalidStructure\",\"path\":\"$\",\"message\":\"Send an empty JSON object, {}: list sources takes no parameters.\",\"incidentId\":\"INCIDENT\"}"),
            ("describe", "{}", "{\"code\":\"InvalidStructure\",\"path\":\"$\",\"message\":\"Send a JSON object with exactly one property, source: {\\\"source\\\": \\\"<name>\\\"} with a name from list sources.\",\"incidentId\":\"INCIDENT\"}"),
            ("query", "{}", WireGolden.Cases["InvalidStructure/tool"].Text[0]),
            ("query", "{\"source\":\"synthetic_items\",\"query\":{\"select\":[\"unknown\"]}}", WireGolden.Cases["UnknownColumn/tool"].Text[0])
        };
        foreach (var gateway in new[] { "a", "b" })
        {
            await using var client = await pair.Connect(gateway);
            foreach (var (operation, body, expected) in cases)
                await Parity(pair.Host, client, gateway, gateway == "a" ? "/north" : "/south", operation,
                    body, expected, operation == "query" && body != "{}" ? "UnknownColumn" : "InvalidStructure", pair.Token);
            await Successful(pair, client, gateway, "query");
        }
        Assert.Equal(16, pair.Host.Logs.Failures.Length);
        NoSdkFailure(pair.Host);
    }

    [Fact]
    public async Task T13_catalog_faults_match_post_wire_and_diagnostics()
    {
        await using var pair = new NamedGatewayMcpPair();
        await pair.Start();
        await using var client = await pair.Connect("a");
        foreach (var variant in Enumerable.Range(0, 7))
            foreach (var operation in Operations)
            {
                Reset(pair.Sa);
                if (variant == 0)
                    pair.Sa.FactoryOverride = _ => throw new SyntheticFault(Secret);
                if (variant == 1)
                    pair.Sa.FactoryOverride = _ => null;
                if (variant is 2 or 3 or 6)
                {
                    Func<CancellationToken, Task> failure = _ => throw (variant == 3 ? new TimeoutException(Secret) :
                        variant == 6 ? new OperationCanceledException(Secret) : new SyntheticFault(Secret));
                    pair.Sa.BeforeList = pair.Sa.BeforeDetail = failure;
                }
                if (variant == 4)
                    pair.Sa.NullListTask = pair.Sa.NullDetailTask = true;
                if (variant == 5)
                {
                    pair.Sa.ChangeList = _ => throw new SyntheticFault(Secret);
                    pair.Sa.ChangeDetail = _ => throw new SyntheticFault(Secret);
                }
                var cancelled = variant == 6;
                var key = cancelled ? "CatalogCancelled" : "CatalogUnavailable";
                var expected = WireGolden.Cases[key + "/tool"].Text[0];
                if (operation == "list" && !cancelled)
                    expected = expected.Replace("$.source", "$", StringComparison.Ordinal);
                var line = await Parity(pair.Host, client, "a", "/north", operation, NamedGatewayHost.Body(operation), expected, key, pair.Token);
                Assert.Equal("catalog", line.Fields["Step"]);
                Assert.Equal(variant == 0 || variant is 2 or 5 ? "SyntheticFault" : variant == 1 ? "InvalidOperationException" :
                    variant == 3 ? "TimeoutException" : variant == 4 ? "NullReferenceException" : "OperationCanceledException", line.Fields["ExceptionType"]);
                Assert.Equal(operation == "list" ? null : "synthetic_items", line.Fields["SourceName"]);
                Assert.Equal(LogLevel.Error, line.Level);
                Assert.Null(line.Fields["ExceptionText"]);
                Assert.DoesNotContain(Secret, JsonSerializer.Serialize(line.Fields));
            }
        Reset(pair.Sa);
        Assert.Empty(pair.Ea.Executed);
        Assert.Equal(42, pair.Host.Logs.Failures.Length);
        NoSdkFailure(pair.Host);
    }

    [Fact]
    public async Task T14_malformed_and_changed_catalogs_use_existing_validation()
    {
        await using var pair = new NamedGatewayMcpPair();
        await pair.Start();
        await using var client = await pair.Connect("a");
        var listCases = new (Func<IReadOnlyList<SourceListRow>, IReadOnlyList<SourceListRow>> Change, string Path, string Message)[]
        {
            (_ => null!, "$.sources", "Return a non-null source list."),
            (_ => [null!], "$.sources[0]", "Replace the null source entry with a source row."),
            (rows => [rows[0], rows[0] with { Name = "SYNTHETIC_ITEMS" }], "$.sources[1].name", "Source 'SYNTHETIC_ITEMS'. Use a source name that is unique ignoring case."),
            (rows => [rows[0] with { RowCap = 0 }], "$.sources[0].rowCap", "Source 'synthetic_items'. Set rowCap to an integer from 1 through 2147483646."),
            (rows => [rows[0] with { RowCap = 0, IsActive = false }], "$.sources[0].rowCap", "Source 'synthetic_items'. Set rowCap to an integer from 1 through 2147483646.")
        };
        foreach (var item in listCases)
        {
            pair.Sa.ChangeList = item.Change;
            var line = await Parity(pair.Host, client, "a", "/north", "list", "{}", WireGolden.Cases["InvalidCatalog/tool"].Text[0], "InvalidCatalog", pair.Token);
            Assert.Equal(item.Path, line.Fields["Path"]);
            Assert.Equal(item.Message, line.Fields["Message"]);
            Assert.Equal("InternalError", line.Fields["ReturnedCode"]);
        }
        pair.Sa.ChangeList = null;
        var detailCases = new (Func<CatalogDetail, CatalogDetail> Change, string Path, string Message)[]
        {
            (detail => new(" ", detail.Rows), "$.detail.provider", "Source 'synthetic_items'. Set provider to a non-blank value."),
            (detail => new(detail.Provider, null!), "$.detail.rows", "Source 'synthetic_items'. Return non-null rows for this detail."),
            (detail => WithSource(detail, name: "wrong"), "$.detail.rows.source.name", "Source 'synthetic_items'. Return the requested source name, matching it ignoring case."),
            (detail => WithSource(detail, connection: " "), "$.detail.rows.source.connectionString", "Source 'synthetic_items'. Set connectionString to a non-blank value."),
            (detail => new(detail.Provider, new(detail.Rows.Source, [null!])), "$.detail.rows.columns[0]", "Source 'synthetic_items'. Replace the null column entry with a column row."),
            (detail => WithSource(detail, cap: 0), "$.detail.rows.source.rowCap", "Source 'synthetic_items'. Set rowCap to an integer from 1 through 2147483646."),
            (detail => new(detail.Provider, new(detail.Rows.Source, [detail.Rows.Columns[0] with { ColumnName = "bad name" }])), "$.source", "Correct the catalog column name: use a bare ASCII SQL identifier."),
            (detail => new(detail.Provider, new(detail.Rows.Source, [detail.Rows.Columns[0] with { DataType = "bogus" }])), "$.source", "Column 'id' has an unsupported data_type. Set it to text, number, date or bool in the catalog."),
            (detail => new("missing", detail.Rows), "$.detail.provider", "Source 'synthetic_items'. Register an engine named 'missing' for this gateway.")
        };
        foreach (var item in detailCases)
        {
            pair.Sa.ChangeDetail = detail => item.Change(detail!);
            foreach (var operation in new[] { "describe", "query" })
            {
                var line = await Parity(pair.Host, client, "a", "/north", operation, NamedGatewayHost.Body(operation),
                    WireGolden.Cases["InvalidCatalog/tool"].Text[0], "InvalidCatalog", pair.Token);
                Assert.Equal(item.Path, line.Fields["Path"]);
                Assert.Equal(item.Message, line.Fields["Message"]);
                Assert.Equal("InternalError", line.Fields["ReturnedCode"]);
            }
        }
        Assert.Empty(pair.Ea.Compiled);
        Assert.Empty(pair.Ea.Executed);
        foreach (var inactive in new[] { false, true })
        {
            pair.Sa.ChangeDetail = detail => inactive ? WithSource(detail!, active: false) : null;
            foreach (var operation in new[] { "describe", "query" })
                await Parity(pair.Host, client, "a", "/north", operation, NamedGatewayHost.Body(operation), Unknown("a"), "UnknownSource", pair.Token);
        }
        pair.Sa.ChangeDetail = _ => null;
        pair.Sa.ChangeList = _ => [];
        const string noSources = "{\"code\":\"UnknownSource\",\"path\":\"$.source\",\"message\":\"No source is active in the catalog. Ask the data steward to activate a source, then call list sources.\",\"incidentId\":\"INCIDENT\"}";
        await Parity(pair.Host, client, "a", "/north", "describe", NamedGatewayHost.Body("describe"), noSources, "UnknownSource", pair.Token);
        Reset(pair.Sa);
        pair.A.Change("UPDATE gateway_source SET is_active=0 WHERE name='synthetic_items'");
        await Parity(pair.Host, client, "a", "/north", "query", NamedGatewayHost.Body("query"),
            Unknown("a").Replace("synthetic_a_only, synthetic_items", "synthetic_a_only", StringComparison.Ordinal), "UnknownSource", pair.Token);
        foreach (var line in pair.Host.Logs.Failures)
        {
            Assert.DoesNotContain(pair.A.Connection, JsonSerializer.Serialize(line.Fields));
            Assert.DoesNotContain("proof_a_items", JsonSerializer.Serialize(line.Fields));
        }
    }

    [Fact]
    public async Task T15_unexpected_tool_faults_have_two_blocks_and_one_event()
    {
        const string sql = "SELECT \"id\" FROM \"proof_a_items\" ORDER BY \"id\" LIMIT @p0";
        const string outer = "DataGateway.Web.Tests.SyntheticFault: " + Secret;
        const string withInner = outer + "\n ---> System.InvalidOperationException: synthetic inner";
        var expected = new (int? SourceId, string? Sql, Dictionary<string, object?>? Parameters, string ExceptionText)[]
        {
            (101, null, null, withInner),
            (101, sql, new() { ["@p0"] = 3 }, outer),
            (101, sql, new() { ["@p0"] = 3 }, "System.OperationCanceledException: " + Secret),
            (null, null, null, withInner),
            (101, null, null, withInner),
            (101, sql, new() { ["@p0"] = 3 }, withInner)
        };
        foreach (var variant in Enumerable.Range(0, 6))
        {
            var operation = variant < 3 ? "query" : Operations[variant - 3];
            var root = operation == "list" ? typeof(SourceListResponse) : operation == "describe" ? typeof(SourceDescribeResponse) : typeof(QueryResponse);
            var fault = new AnswerFault([root], () => new SyntheticFault(Secret, new InvalidOperationException("synthetic inner")));
            await using var pair = new NamedGatewayMcpPair(builder =>
                builder.Services.AddSingleton(new global::DataGateway.GatewayJson(fault)));
            await pair.Start();
            await using var a = await pair.Connect("a");
            await using var b = await pair.Connect("b");
            if (variant == 0)
                pair.Ea.CompileFault = () => new SyntheticFault(Secret, new InvalidOperationException("synthetic inner"));
            if (variant is 1 or 2)
                pair.Ea.BeforeExecute = _ => throw (variant == 1 ? new SyntheticFault(Secret) : new OperationCanceledException(Secret));
            if (variant >= 3)
                fault.Arm();
            var body = NamedGatewayHost.Body(operation);
            var result = await McpWire.Call(a, Tool(operation), body, pair.Token);
            var incident = Failed(result, WireGolden.Cases["UnexpectedError/tool"].Text[0], unexpected: true);
            var line = Event(pair.Host, incident, "a", Tool(operation), body, "UnexpectedError");
            Assert.Equal(variant == 2 ? "OperationCanceledException" : "SyntheticFault", line.Fields["ExceptionType"]);
            Assert.Equal(expected[variant].ExceptionText, McpToolTests.WithoutFrames(Assert.IsType<string>(line.Fields["ExceptionText"])));
            Assert.Equal(LogLevel.Error, line.Level);
            Assert.Equal(operation == "list" ? null : "synthetic_items", line.Fields["SourceName"]);
            Assert.Equal(expected[variant].SourceId, line.Fields["SourceId"]);
            Assert.Equal(expected[variant].Sql, line.Fields["Sql"]);
            Assert.Equal(JsonSerializer.Serialize(expected[variant].Parameters), JsonSerializer.Serialize(line.Fields["SqlParameters"]));
            pair.Ea.CompileFault = null;
            pair.Ea.BeforeExecute = null;
            await Successful(pair, a, "a", operation);
            await Successful(pair, b, "b", operation);
            Assert.Single(pair.Host.Logs.Failures);
            NoSdkFailure(pair.Host);
        }
    }

    [Fact]
    public async Task T16_endpoint_body_faults_are_answered_once()
    {
        foreach (var variant in Enumerable.Range(0, 4))
        {
            var writes = new NamedGatewayErrorWrites();
            await using var pair = new NamedGatewayMcpPair(builder =>
            {
                builder.Services.AddSingleton(new global::DataGateway.GatewayJson(writes));
                builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 1024);
                if (variant < 3)
                    builder.Services.AddSingleton<IStartupFilter>(new BodyFault(variant == 0 ? new SyntheticFault(Secret) :
                        new BadHttpRequestException(Secret, variant == 1 ? 400 : 413)));
            });
            await pair.Start();
            var baseline = pair.Sa.Counts;
            var envelope = variant == 3 ? LegacyList.Replace("77", "77,\"padding\":\"" + new string('x', 2000) + "\"", StringComparison.Ordinal) : LegacyList;
            using var response = await Raw(pair.Http, "/north/mcp", envelope, pair.Token);
            var expected = WireGolden.Cases[variant == 0 ? "UnexpectedError/route" : variant == 1 ? "Unreadable/mcp" : "TooLarge/mcp"];
            Assert.Equal(expected.Status, (int)response.StatusCode);
            Assert.Equal("application/json", response.Content.Headers.ContentType!.MediaType);
            var text = await response.Content.ReadAsStringAsync(pair.Token);
            Assert.Equal(expected.Body, Normalize(text, out var incident));
            Event(pair.Host, incident, "a", "mcp", null, variant == 0 ? "UnexpectedError" : variant == 1 ? "InvalidJson" : "StructuralLimitExceeded");
            Assert.Equal(1, writes.Attempts);
            Assert.Single(pair.Host.Logs.Failures);
            Assert.Equal(baseline, pair.Sa.Counts);
            pair.Capture.Stateless("/north/mcp");
        }
    }

    [Fact]
    public async Task T17_disconnected_tool_calls_keep_one_cancellation_event()
    {
        foreach (var protocol in Protocols)
            foreach (var seam in Enumerable.Range(0, 4))
            {
                await using var pair = new NamedGatewayMcpPair();
                await pair.Start();
                await using var client = await pair.Connect("a", protocol);
                var operation = seam == 0 ? "list" : seam == 1 ? "describe" : "query";
                var entered = Gate();
                var observed = Gate();
                Func<CancellationToken, Task> wait = async token =>
                {
                    using var registration = token.Register(() => observed.TrySetResult());
                    entered.TrySetResult();
                    await Task.Delay(Timeout.Infinite, token);
                };
                if (seam == 0)
                    pair.Sa.BeforeList = wait;
                else if (seam < 3)
                    pair.Sa.BeforeDetail = wait;
                else
                    pair.Ea.BeforeExecute = wait;
                var baseline = pair.Sa.Instances.Count;
                using var caller = CancellationTokenSource.CreateLinkedTokenSource(pair.Token);
                var pending = McpWire.Call(client, Tool(operation), NamedGatewayHost.Body(operation), caller.Token).AsTask();
                try
                {
                    await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    caller.Cancel();
                    await observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
                }
                finally
                {
                    caller.Cancel();
                }
                await client.DisposeAsync();
                await pair.Host.App.StopAsync(pair.Token);
                var line = Assert.Single(pair.Host.Logs.Failures);
                Assert.Equal(LogLevel.Warning, line.Level);
                Assert.Equal(seam < 3 ? "CatalogCancelled" : "QueryCancelled", line.Fields["Code"]);
                Assert.Equal(seam < 3 ? "catalog" : "connect", line.Fields["Step"]);
                Assert.Equal(operation == "list" ? "$" : seam < 3 ? "$.source" : "$.query", line.Fields["Path"]);
                Assert.Equal(Tool(operation), line.Fields["Operation"]);
                Assert.Equal("a", line.Scopes["Gateway"]);
                Assert.All(pair.Sa.Instances.Skip(baseline), item => Assert.Contains(item.Scope, pair.Host.Cleanup.Disposed));
                Assert.DoesNotContain(pair.Host.Logs.Failures, item => Equals(item.Fields["Code"], "UnexpectedError"));
            }
        await CrossEndpointCancellation();
    }

    [Fact]
    public async Task T18_endpoint_abort_guard_distinguishes_pre_server_faults()
    {
        await using var pair = new NamedGatewayMcpPair();
        await pair.Start();
        var baseline = pair.Sa.Counts;
        var endpoint = Assert.Single(pair.Host.Endpoints, item => item.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName == "DataGateway:a:mcp");
        await using var scope = pair.Host.App.Services.CreateAsyncScope();
        using var abort = new CancellationTokenSource();
        using var body = new NamedGatewayCancelStream(abort);
        using var output = new MemoryStream();
        var context = Context(scope.ServiceProvider, endpoint, output, abort.Token);
        context.Request.Body = body;
        await endpoint.RequestDelegate!(context);
        Assert.Empty(output.ToArray());
        var line = Assert.Single(pair.Host.Logs.Failures);
        Assert.Equal(LogLevel.Warning, line.Level);
        Assert.Equal("UnexpectedError", line.Fields["Code"]);
        Assert.Equal("mcp", line.Fields["Operation"]);
        Assert.Equal("a", line.Scopes["Gateway"]);
        Assert.Equal(baseline, pair.Sa.Counts);
        var registration = pair.Host.App.Services.GetRequiredService<GatewayRegistry>().Find("a")!;
        var failures = new FailureLog(pair.Host.App.Services.GetRequiredService<ILoggerFactory>(), registration.Limits);
        await using var server = McpServer.Create(new StreamableHttpServerTransport { Stateless = true }, new McpServerOptions());
        foreach (var cancelled in new[] { false, true })
            foreach (var present in new[] { false, true })
                foreach (var error in new Exception[] { new OperationCanceledException(), new IOException(), new SyntheticFault(Secret) })
                {
                    using var target = new MemoryStream();
                    var probe = Context(scope.ServiceProvider, endpoint, target, cancelled ? abort.Token : CancellationToken.None);
                    if (present)
                        probe.Features.Set(server);
                    var count = pair.Host.Logs.Failures.Length;
                    using (failures.BeginGatewayScope("a"))
                        await CatchAll.Answer(probe, error, failures);
                    var suppress = cancelled && present && error is OperationCanceledException or IOException;
                    Assert.Equal(count + (suppress ? 0 : 1), pair.Host.Logs.Failures.Length);
                    if (cancelled || suppress)
                        Assert.Empty(target.ToArray());
                    else
                        Assert.Equal(WireGolden.Cases["UnexpectedError/route"].Body,
                            Normalize(Encoding.UTF8.GetString(target.ToArray()), out _));
                }
    }

    [Fact]
    public async Task T19_public_root_and_named_registration_orders_are_equivalent()
    {
        foreach (var exampleFirst in new[] { false, true })
            foreach (var reverseMap in new[] { false, true })
            {
                using var a = new SqliteFixture();
                using var b = new SqliteFixture();
                using var example = new SqliteFixture();
                a.Change("UPDATE gateway_source SET name='synthetic_a_only' WHERE name='synthetic_other'");
                b.Change("UPDATE gateway_source SET name='synthetic_b_only' WHERE name='synthetic_other'; UPDATE gateway_source SET title='Synthetic B' WHERE name='synthetic_items'; UPDATE proof_items SET id=id+10");
                PrepareExample(example);
                var sa = new NamedGatewayState(a);
                var sb = new NamedGatewayState(b);
                var rootState = new NamedGatewayState(example) { Metadata = McpMetadataFixture.Seed with { Title = "Example title", About = "Example about." } };
                var engine = new SqliteEngine();
                ServiceDescriptor[]? before = null;
                await using var host = new NamedGatewayHost(builder =>
                {
                    void Example()
                    {
                        builder.Services.AddDataGateway("DataGateway.Web", gateway => gateway
                            .UseCatalog(rootState.Create)
                            .AddEngine(engine)
                            .UseLimits(new GatewayLimits { MaxFilterConditions = 1 })
                            );
                    }
                    if (exampleFirst)
                        Example();
                    NamedGatewayMcpPair.Register(builder.Services, "a", sa, new(), "Title A", "About A.");
                    if (!exampleFirst)
                        Example();
                    before = TransportDescriptors(builder.Services);
                    NamedGatewayMcpPair.Register(builder.Services, "b", sb, new(), "Title B", "About B.");
                    Assert.Equal(before, TransportDescriptors(builder.Services));
                });
                if (reverseMap)
                    host.App.MapDataGateway("DataGateway.Web", "");
                host.App.MapDataGateway("a", "/north");
                host.App.MapDataGateway("b", "/south");
                if (!reverseMap)
                    host.App.MapDataGateway("DataGateway.Web", "");
                await host.Start();
                var transport = host.App.Services.GetRequiredService<IOptions<HttpServerTransportOptions>>().Value;
                Assert.True(transport.Stateless);
                Assert.NotNull(transport.ConfigureSessionOptions);
                Assert.NotEmpty(before!);
                var factory = host.App.Services.GetRequiredService<IOptionsFactory<McpServerOptions>>();
                var selected = new List<McpServerOptions>();
                foreach (var name in new[] { "a", "b", "DataGateway.Web", "a" })
                {
                    var endpoint = Assert.Single(host.Endpoints, endpoint => endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName == $"DataGateway:{name}:mcp");
                    var context = new DefaultHttpContext { RequestServices = host.App.Services };
                    context.SetEndpoint(endpoint);
                    var options = factory.Create(Options.DefaultName);
                    await transport.ConfigureSessionOptions!(context, options, CancellationToken.None);
                    Assert.Equal(name, options.ServerInfo!.Name);
                    Assert.Equal(new[] { "describe_source", "list_sources", "query_source" }, options.ToolCollection!.Select(tool => tool.ProtocolTool.Name).Order(StringComparer.Ordinal));
                    Assert.All(selected, previous => Assert.NotSame(previous.ToolCollection, options.ToolCollection));
                    selected.Add(options);
                }
                using var deadline = Deadline();
                using var capture = new Capture();
                using var http = Http(host.Client.BaseAddress!, capture);
                foreach (var protocol in Protocols)
                {
                    await using var ca = await Connect(http, "/north/mcp", protocol, deadline.Token);
                    await using var cb = await Connect(http, "/south/mcp", protocol, deadline.Token);
                    await using var cp = await Connect(http, "/mcp", protocol, deadline.Token);
                    Identity(ca, "a", "Title A", "About A.");
                    Identity(cb, "b", "Title B", "About B.");
                    ExampleIdentity(cp, "Example title", "Example about.", "1.0.0");
                    await Definitions(ca, deadline.Token);
                    await Definitions(cb, deadline.Token);
                    await Definitions(cp, deadline.Token);
                    foreach (var operation in Operations)
                    {
                        var body = NamedGatewayHost.Body(operation);
                        Witness(operation, McpWire.Text(await McpWire.Call(ca, Tool(operation), body, deadline.Token)), "a", "Synthetic");
                        Witness(operation, McpWire.Text(await McpWire.Call(cb, Tool(operation), body, deadline.Token)), "b", "Synthetic B");
                        var exampleBody = ExampleBody(operation, limit: 1);
                        ExampleWitness(operation, McpWire.Text(await McpWire.Call(cp, Tool(operation), exampleBody, deadline.Token)), limit: 1);
                    }
                    foreach (var (client, gateway, source) in new[] { (ca, "a", "synthetic_example_only"), (cb, "b", "synthetic_example_only"), (cp, "example", "synthetic_a_only") })
                    {
                        var body = NamedGatewayHost.Body("describe", source);
                        var expected = gateway == "example" ? UnknownExample : Unknown(gateway);
                        var incident = Failed(await McpWire.Call(client, "describe_source", body, deadline.Token), expected);
                        var line = Assert.Single(host.Logs.Failures, item => Equals(item.Fields["IncidentId"], incident));
                        Assert.Equal("describe_source", line.Fields["Operation"]);
                        Assert.Equal(body, line.Fields["Request"]);
                        if (gateway == "example")
                            Assert.Equal("DataGateway.Web", line.Scopes["Gateway"]);
                        else
                            Assert.Equal(gateway, line.Scopes["Gateway"]);
                    }
                    const string filters = "{\"source\":\"synthetic_example_only\",\"query\":{\"select\":[\"id\"],\"filters\":[{\"col\":\"id\",\"op\":\"gte\",\"value\":1},{\"col\":\"id\",\"op\":\"lte\",\"value\":200}],\"order_by\":[{\"col\":\"id\",\"dir\":\"asc\"}],\"limit\":2}}";
                    var refused = await McpWire.Call(cp, "query_source", filters, deadline.Token);
                    Assert.True(refused.IsError);
                    var namedBody = filters.Replace("synthetic_example_only", "synthetic_items", StringComparison.Ordinal);
                    var accepted = await McpWire.Call(ca, "query_source", namedBody, deadline.Token);
                    Assert.False(accepted.IsError == true);
                    Assert.Equal(QueryA, McpWire.Text(accepted));
                }
                capture.Stateless("/mcp", "/north/mcp", "/south/mcp");
            }
    }

    [Fact]
    public async Task T20_real_example_composition_coexists_with_named_mcp()
    {
        using var example = new SqliteFixture();
        using var named = new SqliteFixture();
        PrepareExample(example);
        named.Change("UPDATE gateway_source SET name='synthetic_a_only' WHERE name='synthetic_other'");
        using var settings = example.Settings();
        var state = new NamedGatewayState(named);
        var cleanup = new NamedGatewayCleanup();
        await using var host = example.Host(new SqliteEngine(), settings: new Dictionary<string, string?>
        {
            ["DataGateway:Mcp:Title"] = "Composition example",
            ["DataGateway:Mcp:About"] = "Composition about.",
            }, configure: builder =>
        {
            builder.Services.AddSingleton(cleanup);
            builder.Services.AddScoped<NamedGatewayDependency>();
            NamedGatewayMcpPair.Register(builder.Services, "a", state, new(), "Named composition", "Named about.");
        });
        host.App.MapDataGateway("a", "/named");
        await host.Start();
        using var deadline = Deadline();
        using var capture = new Capture();
        using var http = Http(host.Client.BaseAddress!, capture);
        foreach (var protocol in Protocols)
        {
            await using var cp = await Connect(http, "/mcp", protocol, deadline.Token);
            await using var cn = await Connect(http, "/named/mcp", protocol, deadline.Token);
            ExampleIdentity(cp, "DataGateway", McpMetadataFixture.Seed.About, "1.0.0");
            Identity(cn, "a", "Named composition", "Named about.");
            foreach (var operation in Operations)
                foreach (var (client, prefix, isExample) in new[] { (cp, "", true), (cn, "/named", false) })
                {
                    var body = isExample ? ExampleBody(operation, 2) : NamedGatewayHost.Body(operation);
                    var result = await McpWire.Call(client, Tool(operation), body, deadline.Token);
                    Assert.False(result.IsError == true);
                    var text = McpWire.Text(result);
                    if (isExample)
                        ExampleWitness(operation, text, 2);
                    else
                        Witness(operation, text, "a", "Synthetic");
                    using var response = await http.PostAsync(prefix + NamedGatewayHost.PathOf(operation), new StringContent(body), deadline.Token);
                    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                    Assert.Equal(await response.Content.ReadAsByteArrayAsync(deadline.Token), Encoding.UTF8.GetBytes(text));
                }
            foreach (var (client, gateway) in new[] { (cp, "example"), (cn, "a") })
            {
                var body = NamedGatewayHost.Body("describe", "missing");
                var id = Failed(await McpWire.Call(client, "describe_source", body, deadline.Token), gateway == "example" ? UnknownExample : Unknown("a"));
                var line = Assert.Single(host.Lines(), item => item.GetProperty("IncidentId").GetString() == id);
                Assert.Equal("describe_source", line.GetProperty("Operation").GetString());
                Assert.Equal(body, line.GetProperty("Request").GetString());
                if (gateway == "a")
                    Assert.Equal("a", line.GetProperty("Gateway").GetString());
                else
                    Assert.Equal("DataGateway.Web", line.GetProperty("Gateway").GetString());
            }
        }
        capture.Stateless("/mcp", "/named/mcp");
        McpToolTests.NoSdkFailure(host);
    }

    [Fact]
    public async Task T21_registration_is_host_local()
    {
        using var a = new SqliteFixture();
        using var b = new SqliteFixture();
        b.Change("UPDATE proof_items SET id=id+10");
        var sa = new NamedGatewayState(a);
        var sb = new NamedGatewayState(b);
        var unmapped = new NamedGatewayState(a);
        await using var first = new NamedGatewayHost(builder =>
        {
            var original = builder.Services.ToArray();
            Assert.Throws<InvalidOperationException>(() => builder.Services.AddDataGateway("a", gateway => gateway.UseCatalog(sa.Create).AddEngine(new NamedGatewayEngine()).UseMcpTools("invalid_")));
            Assert.Equal(original, builder.Services.ToArray());
            NamedGatewayMcpPair.Register(builder.Services, "a", sa, new(), "First", "First about.");
            var transport = TransportDescriptors(builder.Services);
            NamedGatewayHost.Register(builder.Services, "unmapped", unmapped);
            Assert.Equal(transport, TransportDescriptors(builder.Services));
        });
        await using var second = new NamedGatewayHost(builder =>
            NamedGatewayMcpPair.Register(builder.Services, "a", sb, new(), "Second", "Second about."));
        first.App.MapDataGateway("a", "/route");
        second.App.MapDataGateway("a", "/route");
        await first.Start();
        await second.Start();
        Assert.Equal(1, unmapped.Factories);
        Assert.Equal(1, unmapped.Lists);
        Assert.Equal(2, unmapped.Details);
        using var deadline = Deadline();
        await using var one = await Connect(first.Client, "/route/mcp", null, deadline.Token);
        await using var two = await Connect(second.Client, "/route/mcp", "2025-11-25", deadline.Token);
        foreach (var iteration in Enumerable.Range(0, 3))
        {
            Identity(one, "a", "First", "First about.");
            Identity(two, "a", "Second", "Second about.");
            Assert.Equal(QueryA, McpWire.Text(await McpWire.Call(one, "query_source", NamedGatewayHost.Body("query"), deadline.Token)));
            Assert.Equal(QueryB, McpWire.Text(await McpWire.Call(two, "query_source", NamedGatewayHost.Body("query"), deadline.Token)));
        }
    }

    [Fact]
    public async Task T22_mapping_metadata_methods_and_document_exclusion()
    {
        foreach (var prefix in new[] { "", "/", "/deep/nested", "/trailing/", "/tenant/{tenant}" })
        {
            using var fixture = new SqliteFixture();
            var state = new NamedGatewayState(fixture);
            await using var host = new NamedGatewayHost(builder => NamedGatewayHost.Register(builder.Services, "unrelated", state));
            host.App.MapDataGateway("unrelated", prefix);
            await host.Start();
            Assert.Equal(new[] { "describe", "list", "mcp", "query" }.Select(operation => "DataGateway:unrelated:" + operation),
                host.Endpoints.Select(endpoint => endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()!.EndpointName).Order(StringComparer.Ordinal));
            var endpoint = Assert.Single(host.Endpoints, item => item.Metadata.GetMetadata<GatewayOperation>()?.Name == "mcp");
            Assert.Equal(new[] { "POST" }, endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods);
            Assert.True(endpoint.Metadata.GetMetadata<IExcludeFromDescriptionMetadata>()!.ExcludeFromDescription);
            var path = prefix.TrimEnd('/').Replace("{tenant}", "synthetic", StringComparison.Ordinal) + "/mcp";
            using var deadline = Deadline();
            using var capture = new Capture();
            using var http = Http(host.Client.BaseAddress!, capture);
            foreach (var method in new[] { HttpMethod.Get, HttpMethod.Delete })
            {
                using var request = new HttpRequestMessage(method, path);
                using var response = await http.SendAsync(request, deadline.Token);
                Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
                Assert.Empty(await response.Content.ReadAsStringAsync(deadline.Token));
            }
            await using var client = await Connect(http, path, "2025-11-25", deadline.Token);
            Identity(client, "unrelated", "DataGateway", McpMetadataFixture.Seed.About);
            capture.Stateless(path);
            Assert.Equal(2, capture.Responses.Count(item => item.Status == 405));
        }
        await using (var pair = new NamedGatewayMcpPair(builder =>
        {
            Func<IServiceCollection, string, IServiceCollection> addOpenApi = OpenApiServiceCollectionExtensions.AddOpenApi;
            addOpenApi(builder.Services, "v1");
        }))
        {
            pair.Host.App.MapOpenApi();
            await pair.Start();
            using var document = JsonDocument.Parse(await pair.Http.GetStringAsync("/openapi/v1.json", pair.Token));
            var paths = document.RootElement.GetProperty("paths").EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ToArray();
            Assert.Equal(new[] { "/north/query", "/north/sources/describe", "/north/sources/list", "/south/query", "/south/sources/describe", "/south/sources/list" }, paths);
            var left = pair.Sa.Counts;
            var right = pair.Sb.Counts;
            foreach (var path in new[] { "/north/mcp", "/south/mcp" })
            {
                using var response = await Raw(pair.Http, path, LegacyList, pair.Token, "synthetic-legacy-session");
                Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
                Assert.Equal(SessionRefusal, await response.Content.ReadAsStringAsync(pair.Token));
            }
            Assert.Equal(left, pair.Sa.Counts);
            Assert.Equal(right, pair.Sb.Counts);
            Assert.Empty(pair.Host.Logs.Failures);
            pair.Capture.Stateless("/north/mcp", "/south/mcp");
            Assert.Equal(2, pair.Capture.Responses.Count(item => item.Status == 400));
        }
    }

    private static CancellationTokenSource Deadline() => new(TimeSpan.FromSeconds(45));
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static void Identity(McpClient client, string name, string? title, string? about)
    {
        Assert.Equal(name, client.ServerInfo.Name);
        Assert.Equal(typeof(DataGatewayBuilder).Assembly.GetName().Version!.ToString(3), client.ServerInfo.Version);
        Assert.Equal(3, client.ServerInfo.Version.Split('.').Length);
        Assert.Equal(title, client.ServerInfo.Title);
        Assert.Equal(about is null ? Instructions : about + " " + Instructions, client.ServerInstructions);
    }

    private static void Frozen(DataGatewayBuilder builder) => Assert.Equal("Gateway 'a'. Registration is already complete.",
        Assert.Throws<InvalidOperationException>(() => builder.UseMcpTools()).Message);

    private static async Task Successful(NamedGatewayMcpPair pair, McpClient client, string gateway, string operation)
    {
        var body = NamedGatewayHost.Body(operation);
        var result = await McpWire.Call(client, Tool(operation), body, pair.Token);
        Assert.False(result.IsError == true);
        Assert.Single(result.Content);
        var text = McpWire.Text(result);
        Witness(operation, text, gateway, gateway == "a" ? "Synthetic" : "Synthetic B");
        var route = await pair.Host.Post(gateway == "a" ? "/north" : "/south", operation, body);
        Assert.Equal(200, route.Status);
        Assert.Equal(route.Bytes, Encoding.UTF8.GetBytes(text));
    }

    private static async Task FailureForCaller(NamedGatewayMcpPair pair, string gateway, string caller, string column)
    {
        using var capture = new Capture();
        using var http = Http(pair.Http.BaseAddress!, capture, caller);
        await using var client = await Connect(http, gateway == "a" ? "/north/mcp" : "/south/mcp", null, pair.Token);
        var body = "{\"source\":\"synthetic_items\",\"query\":{\"select\":[\"" + column + "\"]}}";
        var id = Failed(await McpWire.Call(client, "query_source", body, pair.Token), WireGolden.Cases["UnknownColumn/tool"].Text[0]);
        Event(pair.Host, id, gateway, "query_source", body, "UnknownColumn", caller);
        capture.Stateless(gateway == "a" ? "/north/mcp" : "/south/mcp");
    }

    private static async Task<NamedGatewayEvent> Parity(NamedGatewayHost host, McpClient client,
        string gateway, string prefix, string operation, string body, string expected, string code, CancellationToken token)
    {
        var count = host.Logs.Failures.Length;
        var result = await McpWire.Call(client, Tool(operation), body, token);
        var id = Failed(result, expected);
        Assert.Equal(count + 1, host.Logs.Failures.Length);
        var tool = Event(host, id, gateway, Tool(operation), body, code);
        var route = await host.Post(prefix, operation, body);
        Assert.Equal(expected, Normalize(route.Text, out var routeId));
        var line = Event(host, routeId, gateway, operation, body, code);
        Assert.Equal(count + 2, host.Logs.Failures.Length);
        Assert.Equal("application/json", route.ContentType!.Split(';')[0]);
        Assert.Equal(code == "UnknownSource" ? 404 : code == "CatalogUnavailable" ? 503 :
            code is "InvalidCatalog" or "CatalogCancelled" ? 500 : 400, route.Status);
        EqualFields(line, tool);
        return tool;
    }

    private static void Reset(NamedGatewayState state)
    {
        state.FactoryOverride = null;
        state.BeforeList = state.BeforeDetail = null;
        state.ChangeList = null;
        state.ChangeDetail = null;
        state.NullListTask = state.NullDetailTask = false;
    }

    private static CatalogDetail WithSource(CatalogDetail detail, string? name = null, string? connection = null,
        int? cap = null, bool? active = null)
    {
        var row = detail.Rows.Source;
        var source = new DataSourceRow(row.ObjectName, connection ?? row.ConnectionString)
        {
            Id = row.Id,
            Name = name ?? row.Name,
            Title = row.Title,
            Description = row.Description,
            Grain = row.Grain,
            IsActive = active ?? row.IsActive,
            QueryTimeoutSeconds = row.QueryTimeoutSeconds, RowCap = cap ?? row.RowCap
        };
        return new(detail.Provider, new(source, detail.Rows.Columns));
    }

    private static DefaultHttpContext Context(IServiceProvider services, Endpoint endpoint, Stream output, CancellationToken token)
    {
        var context = new DefaultHttpContext { RequestServices = services, RequestAborted = token };
        context.SetEndpoint(endpoint);
        context.Request.Method = "POST";
        context.Request.Path = "/north/mcp";
        context.Request.ContentType = "application/json";
        context.Request.ContentLength = Encoding.UTF8.GetByteCount(LegacyList);
        context.Request.Headers.Accept = "application/json, text/event-stream";
        context.Request.Headers["MCP-Protocol-Version"] = "2025-11-25";
        context.Response.Body = output;
        return context;
    }

    private static async Task CrossEndpointCancellation()
    {
        var httpAborted = Gate();
        await using var pair = new NamedGatewayMcpPair(pipeline: app => app.Use(async (context, next) =>
        {
            using var registration = context.Request.Path == "/north/mcp"
                ? context.RequestAborted.Register(() => httpAborted.TrySetResult()) : default;
            await next(context);
        }));
        await pair.Start();
        var entered = Gate();
        var observed = Gate();
        var release = Gate();
        pair.Sa.BeforeDetail = async token =>
        {
            entered.TrySetResult();
            try
            {
                await release.Task.WaitAsync(token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                observed.TrySetResult();
                throw;
            }
        };
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(pair.Token);
        var envelope = """{"jsonrpc":"2.0","id":41,"method":"tools/call","params":{"name":"query_source","arguments":{"source":"synthetic_items","query":{"limit":2}}}}""";
        var pending = Raw(pair.Http, "/north/mcp", envelope, caller.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var notification = await Raw(pair.Http, "/south/mcp", """{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":41,"reason":"synthetic"}}""", pair.Token);
            Assert.Equal(HttpStatusCode.Accepted, notification.StatusCode);
            Assert.Empty(await notification.Content.ReadAsStringAsync(pair.Token));
            Assert.False(observed.Task.IsCompleted);
            Assert.False(pending.IsCompleted);
            Assert.Empty(pair.Host.Logs.Failures);
            caller.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            await httpAborted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            caller.Cancel();
            release.TrySetResult();
        }
        await pair.Host.App.StopAsync(pair.Token);
        var line = Assert.Single(pair.Host.Logs.Failures);
        Assert.Equal("a", line.Scopes["Gateway"]);
        Assert.Equal("CatalogCancelled", line.Fields["Code"]);
        Assert.Equal("query_source", line.Fields["Operation"]);
        Assert.Equal(LogLevel.Warning, line.Level);
    }

    private static ServiceDescriptor[] TransportDescriptors(IServiceCollection services) => services.Where(descriptor =>
        descriptor.ServiceType == typeof(IConfigureOptions<HttpServerTransportOptions>)
        || descriptor.ServiceType.FullName?.Contains("StreamableHttpHandler", StringComparison.Ordinal) == true).ToArray();

    private static void PrepareExample(SqliteFixture fixture) => fixture.Change("UPDATE gateway_source SET name='synthetic_example_only' WHERE name='synthetic_items'; UPDATE gateway_source SET name='synthetic_example_other' WHERE name='synthetic_other'; UPDATE proof_items SET id=id+100");

    private const string UnknownExample = "{\"code\":\"UnknownSource\",\"path\":\"$.source\",\"message\":\"Choose an active source. Available sources: synthetic_example_only, synthetic_example_other.\",\"incidentId\":\"INCIDENT\"}";

    private static string ExampleBody(string operation, int limit) => NamedGatewayHost.Body(operation, "synthetic_example_only")
        .Replace("\"limit\":2", "\"limit\":" + limit, StringComparison.Ordinal);

    private static void ExampleIdentity(McpClient client, string title, string about, string expectedVersion)
    {
        Assert.Equal("DataGateway.Web", client.ServerInfo.Name);
        Assert.Equal(expectedVersion, client.ServerInfo.Version);
        Assert.Equal(title, client.ServerInfo.Title);
        Assert.Equal(about + " " + Instructions, client.ServerInstructions);
    }

    private static void ExampleWitness(string operation, string text, int limit)
    {
        if (operation == "list")
        {
            Assert.Equal("""{"sources":[{"name":"synthetic_example_only","title":"Synthetic","description":"Invented SQLite proof data.","grain":"One item","rowCap":3},{"name":"synthetic_example_other","title":"Synthetic","description":"Invented SQLite proof data.","grain":"One item","rowCap":3}]}""", text);
            return;
        }
        if (operation == "query")
        {
            Assert.Equal(limit == 1 ? """{"source":"synthetic_example_only","columns":[{"name":"id","dataType":"number"}],"rows":[{"id":101}],"rowCount":1,"more":true,"hint":"The result set was capped at 1 row."}""" :
                """{"source":"synthetic_example_only","columns":[{"name":"id","dataType":"number"}],"rows":[{"id":101},{"id":102}],"rowCount":2,"more":true,"hint":"The result set was capped at 2 rows."}""", text);
            return;
        }
        using var json = JsonDocument.Parse(text);
        Assert.Equal("""{"name":"synthetic_example_only","title":"Synthetic","description":"Invented SQLite proof data.","grain":"One item","rowCap":3}""", json.RootElement.GetProperty("source").GetRawText());
    }

    private static async Task Definitions(McpClient client, CancellationToken token)
    {
        var tools = await client.ListToolsAsync(cancellationToken: token);
        Assert.Equal(new[] { "describe_source", "list_sources", "query_source" }, tools.Select(tool => tool.Name).Order(StringComparer.Ordinal));
        Assert.Equal(3, tools.Count);
        foreach (var tool in tools)
        {
            var expected = ExpectedDefinitions[tool.Name];
            Assert.Equal(expected.Description, tool.Description);
            Assert.True(JsonElement.DeepEquals(JsonSerializer.Deserialize<JsonElement>(expected.Schema), tool.ProtocolTool.InputSchema));
            Assert.True(tool.ProtocolTool.Annotations!.ReadOnlyHint);
            Assert.False(tool.ProtocolTool.Annotations.DestructiveHint);
            Assert.False(tool.ProtocolTool.Annotations.OpenWorldHint);
            Assert.Null(tool.ProtocolTool.Annotations.IdempotentHint);
            Assert.Null(tool.ProtocolTool.OutputSchema);
            Assert.Equal(McpMetadataFixture.Seed.Tools.Single(row => row.ToolName == tool.Name).Title, tool.ProtocolTool.Title);
        }
    }

    private static readonly Dictionary<string, (string Description, string Schema)> ExpectedDefinitions = new()
    {
            ["list_sources"] = ("""No source named? See all data sources and what each holds. Takes no arguments and returns {"sources":[...]}: each active source's name, title, description, grain (what one row represents) and rowCap (maximum rows per query). Pick the source whose description and grain fit the question, then call describe_source with its name.""", """{"type":"object","properties":{},"additionalProperties":false}"""),
            ["describe_source"] = ("""Named source not yet described? Get schema and query rules. Returns one source (name, title, description, grain, rowCap) with querySchema: every column with its meaning, type, unit, magnitude, display format and can* flags, then the rules of each query clause. Use only listed columns, as their flags allow. Before stating a number, read its unit and magnitude: "millions" means 523.06 is 523.06 million, for sum_, avg_, min_ and max_ outputs too; a count_ output is a plain count.""", """{"type":"object","properties":{"source":{"type":"string","description":"Name of one source from list_sources, matched without regard to case."}},"required":["source"],"additionalProperties":false}"""),
            ["query_source"] = ("""Source already described? Run a read-only query, get rows. Build query from that source's querySchema (describe_source). Returns {source, columns, rows, rowCount, more, hint}. Prefer filters and aggregates over pulling raw rows. With or without limit, more is true exactly when rows were left out: never report that answer as complete. Narrow or aggregate; there is no way to ask for the rest. Empty rows, or an aggregate's 0 or null, can mean no row matched or a misspelled value.""", """{"type":"object","properties":{"source":{"type":"string","description":"Name of one source from list_sources, matched without regard to case."},"query":{"type":"object","description":"Built from this source's querySchema (describe_source), e.g. {\"select\":[\"a\"],\"filters\":[{\"col\":\"b\",\"op\":\"eq\",\"value\":\"x\"}],\"limit\":5}. {} returns every selectable column, if any, up to the row cap."}},"required":["source","query"],"additionalProperties":false}"""),
    };

}
