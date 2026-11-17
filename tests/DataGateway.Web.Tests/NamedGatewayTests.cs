using System.Data.Common;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using DataGateway.Core.Catalog;
using DataGateway.Core.Errors;
using DataGateway.Core.Queries;
using DataGateway.Data;
using DataGateway.Data.Catalog;
using DataGateway.Data.Sqlite;
using DataGateway.Data.SqlServer;
using DataGateway.Data.PostgreSql;
using DataGateway.Failures;
using DataGateway.Operations;
using DataGateway.Routes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SqlKata.JsonQuery;
using Xunit;

namespace DataGateway.Web.Tests;

public sealed class NamedGatewayTests
{
    private static readonly string[] Operations = ["list", "describe", "query"];
    private const string QueryGolden = """{"source":"synthetic_items","columns":[{"name":"id","dataType":"number"}],"rows":[{"id":1},{"id":2}],"rowCount":2,"more":true,"hint":"The result set was capped at 2 rows."}""";
    private const string DefaultLoggedRequest = "<use submitted request>";
    private const string Secret = "synthetic-catalog-private-sentinel";

    [Fact]
    public async Task T01_two_gateways_are_isolated()
    {
        using var a = new SqliteFixture();
        using var b = new SqliteFixture();
        a.Change("UPDATE gateway_source SET name='synthetic_a_only' WHERE name='synthetic_other'");
        b.Change("UPDATE gateway_source SET name='synthetic_b_only' WHERE name='synthetic_other'");
        b.Change("UPDATE gateway_source SET title='Synthetic B' WHERE name='synthetic_items'");
        b.Change("UPDATE proof_items SET id=id+10");
        var sa = new NamedGatewayState(a);
        var sb = new NamedGatewayState(b);
        await using var host = Host(sa, sb);
        await host.Start();
        foreach (var op in Operations)
        {
            var left = await host.Post("/a", op);
            var right = await host.Post("/b", op);
            Assert.Equal(200, left.Status);
            Assert.Equal(200, right.Status);
            Assert.NotEqual(left.Text, right.Text);
            Assert.DoesNotContain("synthetic_hidden", left.Text + right.Text);
        }

        Assert.Equal(QueryGolden, (await host.Post("/a", "query")).Text);
        Assert.Contains("\"id\":11", (await host.Post("/b", "query")).Text);
        var baseline = sa.Counts;
        await Error(host, "/b", "describe", NamedGatewayHost.Body("describe", "synthetic_a_only"), "UnknownSource", "$.source", "Choose an active source. Available sources: synthetic_b_only, synthetic_items.", 404);
        Assert.Equal(baseline, sa.Counts);
    }

    [Fact]
    public async Task T02_each_provider_selects_its_registered_engine()
    {
        using var fixture = new SqliteFixture();
        var state = new NamedGatewayState(fixture);
        state.Provider = name => name == "synthetic_items" ? "SQLITE" : "Sqlite-Alt";
        var first = new NamedGatewayEngine();
        var second = new NamedGatewayEngine();
        var other = new NamedGatewayEngine();
        var entered = Gate();
        var release = Gate();
        var runs = 0;
        first.BeforeExecute = second.BeforeExecute = async token =>
        {
            if (Interlocked.Increment(ref runs) == 2)
                entered.TrySetResult();
            await release.Task.WaitAsync(token);
        };
        await using var isolated = new NamedGatewayHost(builder =>
        {
            builder.Services.AddDataGateway("a", gateway => gateway.UseCatalog(state.Create).AddEngine(first).AddEngine(second, "sqlite-alt"));
            var sb = new NamedGatewayState(fixture);
            NamedGatewayHost.Register(builder.Services, "b", sb, other);
        });
        isolated.App.MapDataGateway("a", "/a");
        isolated.App.MapDataGateway("b", "/b");
        await isolated.Start();
        var one = isolated.Post("/a", "query");
        var two = isolated.Post("/a", "query", """{"source":"synthetic_other","query":{"select":["label"],"limit":2}}""");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        release.TrySetResult();
        Assert.All(await Task.WhenAll(one, two), reply => Assert.Equal(200, reply.Status));
        Assert.Single(first.Compiled);
        Assert.Single(first.Executed);
        Assert.Single(second.Compiled);
        Assert.Single(second.Executed);
        Assert.Same(first.Compiled.Single(), first.Executed.Single().Query);
        Assert.Same(second.Compiled.Single(), second.Executed.Single().Query);
        Assert.Equal("id", Assert.Single(first.Executed.Single().Query.Columns).Name);
        Assert.Equal("label", Assert.Single(second.Executed.Single().Query.Columns).Name);
        Assert.Contains("\"id\"", first.Compiled.Single().Sql);
        Assert.Contains("\"label\"", second.Compiled.Single().Sql);
        Assert.Equal(200, (await isolated.Post("/b", "query")).Status);
        Assert.Single(other.Executed);
    }

    [Fact]
    public async Task T03_empty_configuration_and_empty_catalog_are_supported()
    {
        using var fixture = new SqliteFixture();
        var populated = new NamedGatewayState(fixture);
        await using (var host = Host(populated))
        {
            await host.Start();
            Assert.Equal(200, (await host.Post("/a", "list")).Status);
        }

        var empty = new NamedGatewayState(fixture)
        {
            ChangeList = _ => []
        };
        await using var blank = Host(empty);
        await blank.Start();
        Assert.Equal("{\"sources\":[]}", (await blank.Post("/a", "list")).Text);
        empty.ChangeDetail = _ => null;
        await Error(blank, "/a", "query", NamedGatewayHost.Body("query", "missing"), "UnknownSource", "$.source", "No source is active in the catalog. Ask the data steward to activate a source, then call list sources.", 404);
    }

    [Fact]
    public async Task T04_example_and_named_success_bodies_are_byte_identical()
    {
        using var fixture = new SqliteFixture();
        using var settings = fixture.Settings();
        var state = new NamedGatewayState(fixture);
        await using var named = Host(state);
        await named.Start();
        await using var example = fixture.Host(new SqliteEngine(), new Dictionary<string, string?> { });
        await example.Start();
        foreach (var operation in Operations)
        {
            var actual = await named.Post("/a", operation);
            using var response = await example.Client.PostAsync(NamedGatewayHost.PathOf(operation), new StringContent(NamedGatewayHost.Body(operation)));
            Assert.Equal((int)response.StatusCode, actual.Status);
            Assert.Equal(200, actual.Status);
            Assert.Equal(response.Content.Headers.ContentType!.ToString(), actual.ContentType);
            Assert.Equal(await response.Content.ReadAsByteArrayAsync(), actual.Bytes);
        }

        var list = JsonNode.Parse((await named.Post("/a", "list")).Text)!;
        Assert.Equal(2, list["sources"]!.AsArray().Count);
        Assert.Equal("""{"name":"synthetic_items","title":"Synthetic","description":"Invented SQLite proof data.","grain":"One item","rowCap":3}""", list["sources"]![0]!.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
        Assert.Equal("""{"name":"synthetic_other","title":"Synthetic","description":"Invented SQLite proof data.","grain":"One item","rowCap":3}""", list["sources"]![1]!.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
        var describe = JsonNode.Parse((await named.Post("/a", "describe")).Text)!;
        Assert.Equal(list["sources"]![0]!.ToJsonString(), describe["source"]!.ToJsonString());
        var columns = describe["querySchema"]!["columns"]!.AsArray();
        Assert.Equal(new[] { "id", "label", "flag", "amount" }, columns.Select(column => (string)column!["name"]!));
        Assert.Equal(new[] { "number", "text", "bool", "number" }, columns.Select(column => (string)column!["dataType"]!));
        Assert.Equal(new[] { "id", "label", "flag", "amount" }, columns.Select(column => (string)column!["title"]!));
        foreach (var column in columns)
        {
            Assert.Equal("Invented value.", (string? )column!["description"]);
            foreach (var flag in new[]
            {
                "canSelect",
                "canFilter",
                "canSort",
                "canGroup",
                "canAggregate"
            })
                Assert.True((bool)column[flag]!);
            foreach (var optional in new[]
            {
                "unit",
                "magnitude",
                "displayFormat"
            })
                Assert.Null(column[optional]);
        }

        Assert.Equal(3, (int)describe["querySchema"]!["limit"]!["maximum"]!);
        Assert.Equal(QueryGolden, (await named.Post("/a", "query")).Text);
    }

    [Fact]
    public async Task T05_returned_group_policy_blocks_unsigned_catalog_access()
    {
        using var fixture = new SqliteFixture();
        var a = new NamedGatewayState(fixture);
        var b = new NamedGatewayState(fixture);
        var engine = new NamedGatewayEngine();
        await using var host = new NamedGatewayHost(builder =>
        {
            NamedGatewayHost.Register(builder.Services, "a", a);
            NamedGatewayHost.Register(builder.Services, "b", b, engine);
        });
        host.App.MapDataGateway("a", "/a");
        host.App.MapDataGateway("b", "/b").RequireAuthorization();
        await host.Start();
        var counts = b.Counts;
        foreach (var op in Operations)
            Assert.Equal(401, (await host.Post("/b", op)).Status);
        Assert.Equal(counts, b.Counts);
        Assert.Empty(engine.Compiled);
        Assert.Empty(engine.Executed);
        foreach (var op in Operations)
            Assert.Equal(200, (await host.Post("/b", op, signed: true)).Status);
        Assert.Equal(200, (await host.Post("/a", "list")).Status);
    }

    [Fact]
    public async Task T06_neighboring_host_endpoints_keep_host_behavior()
    {
        using var fixture = new SqliteFixture();
        var state = new NamedGatewayState(fixture);
        await using var host = new NamedGatewayHost(builder => NamedGatewayHost.Register(builder.Services, "a", state), app => app.Use(async (context, next) =>
        {
            try
            {
                await next(context);
            }
            catch (SyntheticFault)
            {
                app.Logger.LogWarning("Synthetic neighbor fault");
                context.Response.StatusCode = 418;
                await context.Response.WriteAsync("host fault");
            }
        }));
        host.App.MapGet("/healthy", () => "host healthy");
        host.App.MapGet("/fault", (Func<string>)(() => throw new SyntheticFault("neighbor")));
        host.App.MapDataGateway("a", "/a");
        await host.Start();
        Assert.Equal("host healthy", await host.Client.GetStringAsync("/healthy"));
        using var failed = await host.Client.GetAsync("/fault");
        Assert.Equal(418, (int)failed.StatusCode);
        Assert.Equal("host fault", await failed.Content.ReadAsStringAsync());
        Assert.Empty(host.Logs.Failures);
        var line = Assert.Single(host.Logs.Events, item => item.Text == "Synthetic neighbor fault");
        Assert.False(line.Scopes.ContainsKey("Gateway"));
        await Error(host, "/a", "list", "[]", "InvalidStructure", "$", "Send an empty JSON object, {}: list sources takes no parameters.", 400);
    }

    [Fact]
    public async Task T07_endpoint_names_and_operation_fields_are_distinct()
    {
        using var fixture = new SqliteFixture();
        var state = new NamedGatewayState(fixture);
        var engine = new NamedGatewayEngine();
        await using var host = Host(state, new NamedGatewayState(fixture), engine);
        await host.Start();
        Assert.Equal(new[]
        {
            "DataGateway:a:describe", "DataGateway:a:list", "DataGateway:a:mcp", "DataGateway:a:query",
            "DataGateway:b:describe", "DataGateway:b:list", "DataGateway:b:mcp", "DataGateway:b:query"
        }, host.Endpoints.Select(endpoint => endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()!.EndpointName).Order(StringComparer.Ordinal));
        foreach (var endpoint in host.Endpoints)
        {
            Assert.Equal(new[] { "POST" }, endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods);
            var operation = endpoint.Metadata.GetMetadata<GatewayOperation>()!.Name;
            var produces = Assert.Single(endpoint.Metadata.GetOrderedMetadata<IProducesResponseTypeMetadata>(), metadata => metadata.StatusCode == 200);
            if (operation == "mcp")
            {
                Assert.True(endpoint.Metadata.GetMetadata<IExcludeFromDescriptionMetadata>()!.ExcludeFromDescription);
                Assert.Contains("text/event-stream", produces.ContentTypes);
                continue;
            }
            Assert.Contains("application/json", produces.ContentTypes);
            Assert.Equal(operation switch
            {
                "list" => typeof(SourceListResponse),
                "describe" => typeof(SourceDescribeResponse),
                _ => typeof(QueryResponse)
            }, produces.Type);
        }

        foreach (var prefix in new[]
        {
            "/a",
            "/b"
        })
            foreach (var operation in Operations)
            {
                var before = host.Logs.Failures.Length;
                Assert.Equal(400, (await host.Post(prefix, operation, "{")).Status);
                var log = Assert.Single(host.Logs.Failures.Skip(before));
                Assert.Equal(operation, log.Fields["Operation"]);
                Assert.Equal(prefix[1..], log.Scopes["Gateway"]);
                using var get = await host.Client.GetAsync(prefix + NamedGatewayHost.PathOf(operation));
                Assert.Equal(HttpStatusCode.MethodNotAllowed, get.StatusCode);
            }

        engine.CompileFault = () => new SyntheticFault("compile");
        await GoldenError(host, "/a", "query", "UnexpectedError/route", "UnexpectedError");
    }

    [Fact]
    public async Task T08_requests_read_fresh_list_and_detail_records()
    {
        using var fixture = new SqliteFixture();
        var state = new NamedGatewayState(fixture);
        var engine = new NamedGatewayEngine();
        var alternate = new NamedGatewayEngine();
        await using var host = new NamedGatewayHost(builder => builder.Services.AddDataGateway("a", gateway => gateway.UseCatalog(state.Create).AddEngine(engine).AddEngine(alternate, "sqlite-alt")));
        host.App.MapDataGateway("a", "/a");
        await host.Start();
        Assert.Equal(200, (await host.Post("/a", "query")).Status);
        Assert.Single(engine.Executed);
        Assert.Empty(alternate.Executed);
        fixture.Change("UPDATE gateway_source SET title='Fresh title' WHERE name='synthetic_items'");
        var before = state.Counts;
        Assert.Contains("Fresh title", (await host.Post("/a", "list")).Text);
        Assert.Equal((before.Item1 + 1, before.Item2 + 1, before.Item3), state.Counts);
        fixture.Change("UPDATE gateway_column SET can_filter=0 WHERE column_name='id'");
        state.Provider = _ => "Sqlite-Alt";
        before = state.Counts;
        var description = await host.Post("/a", "describe", NamedGatewayHost.Body("describe", "SyNtHeTiC_ItEmS"));
        Assert.Contains("\"canFilter\":false", description.Text);
        Assert.Equal((before.Item1 + 1, before.Item2, before.Item3 + 1), state.Counts);
        Assert.Equal("SyNtHeTiC_ItEmS", state.Names.Last());
        foreach (var name in new[]
        {
            " synthetic_items ",
            ""
        })
        {
            Assert.Equal(404, (await host.Post("/a", "describe", NamedGatewayHost.Body("describe", name))).Status);
            Assert.Equal(name, state.Names.Last());
        }

        before = state.Counts;
        Assert.Equal(200, (await host.Post("/a", "query")).Status);
        Assert.Equal((before.Item1 + 1, before.Item2, before.Item3 + 1), state.Counts);
        Assert.Single(engine.Compiled);
        Assert.Single(engine.Executed);
        Assert.Single(alternate.Compiled);
        Assert.Same(alternate.Compiled.Single(), Assert.Single(alternate.Executed).Query);
    }

    [Fact]
    public async Task T09_concurrent_requests_use_separate_catalog_adapters()
    {
        using var fixture = new SqliteFixture();
        using var otherFixture = new SqliteFixture();
        fixture.Change("ALTER TABLE proof_items RENAME TO proof_a_items");
        fixture.Change("UPDATE gateway_source SET object_name='proof_a_items'");
        otherFixture.Change("ALTER TABLE proof_items RENAME TO proof_b_items");
        otherFixture.Change("UPDATE gateway_source SET object_name='proof_b_items'");
        var a = new NamedGatewayState(fixture);
        var b = new NamedGatewayState(otherFixture);
        var ea = new NamedGatewayEngine();
        var eb = new NamedGatewayEngine();
        await using var host = Host(a, b, ea, eb);
        await host.Start();
        var entered = Gate();
        var release = Gate();
        var calls = 0;
        a.BeforeDetail = b.BeforeDetail = async token =>
        {
            if (Interlocked.Increment(ref calls) == 6)
                entered.TrySetResult();
            await release.Task.WaitAsync(token);
        };
        var baselineA = a.Instances.Count;
        var baselineB = b.Instances.Count;
        const string badA = """{"source":"synthetic_items","query":{"select":["missing"]}}""";
        const string badB = """{ "source":"SYNTHETIC_ITEMS", "query":{"select":["missing"]}}""";
        var failureA = host.Post("/a", "query", badA);
        var failureB = host.Post("/b", "query", badB, signed: true);
        var pending = new[]
        {
            host.Post("/a", "query"),
            host.Post("/b", "query"),
            host.Post("/a", "describe"),
            host.Post("/b", "describe")
        };
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(failureA.IsCompleted);
        Assert.False(failureB.IsCompleted);
        Assert.Empty(host.Logs.Failures);
        release.TrySetResult();
        Assert.All(await Task.WhenAll(pending), result => Assert.Equal(200, result.Status));
        var failures = await Task.WhenAll(failureA, failureB);
        var instances = a.Instances.Skip(baselineA).Concat(b.Instances.Skip(baselineB)).ToArray();
        Assert.Equal(6, instances.Select(pair => pair.Catalog).Distinct().Count());
        Assert.Equal(6, instances.Select(pair => pair.Scope).Distinct().Count());
        Assert.Single(ea.Executed);
        Assert.Single(eb.Executed);
        Assert.Equal(fixture.Connection, ea.Executed.Single().Connection);
        Assert.Equal(otherFixture.Connection, eb.Executed.Single().Connection);
        Assert.Contains("\"proof_a_items\"", Assert.Single(ea.Compiled).Sql);
        Assert.Contains("\"proof_b_items\"", Assert.Single(eb.Compiled).Sql);
        Assert.Same(ea.Compiled.Single(), ea.Executed.Single().Query);
        Assert.Same(eb.Compiled.Single(), eb.Executed.Single().Query);
        Assert.Equal(2, host.Logs.Failures.Length);
        Assert.Equal(2, host.Logs.Failures.Select(line => line.Fields["IncidentId"]).Distinct().Count());
        foreach (var(response, prefix, body, caller)in new[]
        {
            (failures[0], "/a", badA, "anonymous"),
            (failures[1], "/b", badB, "synthetic-reader")
        })
        {
            Assert.Equal(400, response.Status);
            Assert.Equal("application/json; charset=utf-8", response.ContentType);
            Assert.Equal(WireGolden.Cases["UnknownColumn/route"].Body, Normalize(response.Text));
            using var json = JsonDocument.Parse(response.Bytes);
            var incident = json.RootElement.GetProperty("incidentId").GetString();
            var line = Assert.Single(host.Logs.Failures, item => Equals(item.Fields["IncidentId"], incident));
            CheckLine(line, response, prefix, "query", body, caller);
            Assert.Equal("UnknownColumn", line.Fields["Code"]);
        }
    }

    [Fact]
    public async Task T10_startup_finishes_validation_before_listening()
    {
        using var fixture = new SqliteFixture();
        var state = new NamedGatewayState(fixture);
        var unmapped = new NamedGatewayState(fixture);
        var engine = new NamedGatewayEngine();
        var listEntered = Gate();
        var listRelease = Gate();
        var detailEntered = Gate();
        var detailRelease = Gate();
        var sentinel = new NamedGatewaySentinel();
        state.BeforeList = async token =>
        {
            listEntered.TrySetResult();
            await listRelease.Task.WaitAsync(token);
        };
        state.BeforeDetail = async token =>
        {
            detailEntered.TrySetResult();
            await detailRelease.Task.WaitAsync(token);
        };
        await using var host = new NamedGatewayHost(builder =>
        {
            NamedGatewayHost.Register(builder.Services, "a", state, engine);
            NamedGatewayHost.Register(builder.Services, "unmapped", unmapped);
            builder.Services.Configure<HostOptions>(options => options.ServicesStartConcurrently = true);
            builder.Services.AddSingleton<IHostedService>(sentinel);
        });
        host.App.MapDataGateway("a", "/a");
        var ready = false;
        host.App.Lifetime.ApplicationStarted.Register(() => ready = true);
        var start = host.Start();
        await listEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(start.IsCompleted);
        Assert.False(ready);
        Assert.False(sentinel.Started);
        Assert.Empty(host.App.Urls);
        listRelease.TrySetResult();
        await detailEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(start.IsCompleted);
        Assert.False(sentinel.Started);
        detailRelease.TrySetResult();
        await start.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(ready);
        Assert.True(sentinel.Started);
        Assert.Equal((1, 1, 2), state.Counts);
        Assert.Equal((1, 1, 2), unmapped.Counts);
        Assert.Equal(new[] { "synthetic_items", "synthetic_other" }, state.Names);
        Assert.Empty(engine.Executed);
        Assert.Empty(engine.Compiled);
        state.BeforeList = null;
        state.BeforeDetail = null;
        Assert.Equal(200, (await host.Post("/a", "list")).Status);
        Assert.Equal(2, state.Lists);
    }

    [Fact]
    public async Task T11_startup_rejects_unreadable_catalog()
    {
        foreach (var detail in new[]
        {
            false,
            true
        })
        {
            using var fixture = new SqliteFixture();
            var state = new NamedGatewayState(fixture);
            Func<CancellationToken, Task> failure = _ => throw new SyntheticFault(Secret, new Exception(Secret));
            if (detail)
                state.BeforeDetail = failure;
            else
                state.BeforeList = failure;
            var error = await StartupError(state);
            Assert.Contains(detail ? "Source 'synthetic_items'. $.source" : "Gateway 'a'. $", error.Message);
            Assert.Contains("(SyntheticFault)", error.Message);
            Assert.Contains("Cannot read the catalog.", error.Message);
        }
    }

    [Fact]
    public async Task T12_startup_rejects_invalid_list()
    {
        foreach (var variant in Enumerable.Range(0, 6))
        {
            using var fixture = new SqliteFixture();
            var state = new NamedGatewayState(fixture)
            {
                ChangeList = rows => InvalidList(rows, variant)
            };
            var failure = CatalogValidator.ValidateList(InvalidList(ValidList(), variant))[0];
            var error = await StartupError(state);
            Assert.Equal(StartupText("a", failure), error.Message);
        }
    }

    [Fact]
    public async Task T13_startup_rejects_invalid_detail()
    {
        foreach (var variant in Enumerable.Range(0, 8))
        {
            using var fixture = new SqliteFixture();
            var state = new NamedGatewayState(fixture)
            {
                ChangeDetail = detail => InvalidDetail(detail!, variant)
            };
            var raw = await new NamedGatewayCatalog(new NamedGatewayState(fixture), Guid.Empty).QueryCatalogDetailAsync("synthetic_items", default);
            var invalid = InvalidDetail(raw!, variant);
            var expected = DetailFailure(invalid);
            var error = await StartupError(state);
            Assert.Equal(StartupText("a", expected), error.Message);
        }
    }

    [Fact]
    public async Task T14_startup_rejects_missing_provider_engine()
    {
        using var fixture = new SqliteFixture();
        var a = new NamedGatewayState(fixture)
        {
            Provider = _ => "sqlite-alt"
        };
        var b = new NamedGatewayState(fixture);
        var ea = new NamedGatewayEngine();
        var eb = new NamedGatewayEngine("sqlite-alt");
        b.Provider = _ => "sqlite-alt";
        await using var host = Host(a, b, ea, eb);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => host.Start());
        Assert.Equal("Gateway 'a'. Source 'synthetic_items'. $.detail.provider Source 'synthetic_items'. Register an engine named 'sqlite-alt' for this gateway.", error.Message);
        Assert.Null(error.InnerException);
        Assert.Empty(ea.Compiled);
        Assert.Empty(eb.Compiled);
        Assert.Empty(ea.Executed);
        Assert.Empty(eb.Executed);
    }

    [Fact]
    public void T15_registration_rejects_duplicate_engine_names()
    {
        foreach (var variant in Enumerable.Range(0, 3))
        {
            var services = new ServiceCollection();
            var error = Assert.Throws<InvalidOperationException>(() => services.AddDataGateway("a", gateway =>
            {
                if (variant == 0)
                    gateway.AddEngine(new NamedGatewayEngine()).AddEngine(new NamedGatewayEngine());
                if (variant == 1)
                    gateway.AddEngine(new NamedGatewayEngine(), "variant").AddEngine(new NamedGatewayEngine(), "VARIANT");
                if (variant == 2)
                    gateway.AddEngine(new NamedGatewayEngine()).AddEngine(new NamedGatewayEngine(), "SQLITE");
            }));
            Assert.Equal($"Gateway 'a'. Engine '{(variant == 0 ? "sqlite" : variant == 1 ? "VARIANT" : "SQLITE")}' is already registered.", error.Message);
            Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(GatewayRegistration));
        }
    }

    [Fact]
    public async Task T16_registration_rejects_duplicate_gateway_names()
    {
        using var fixture = new SqliteFixture();
        var state = new NamedGatewayState(fixture);
        var calls = 0;
        await using var host = new NamedGatewayHost(builder =>
        {
            NamedGatewayHost.Register(builder.Services, "a", state);
            Refusal(() => builder.Services.AddDataGateway("a", _ => calls++), "a", "A gateway with this name is already registered.");
            NamedGatewayHost.Register(builder.Services, "A", state);
        });
        host.App.MapDataGateway("a", "/a");
        host.App.MapDataGateway("A", "/upper");
        await host.Start();
        Assert.Equal(0, calls);
        Assert.Equal(200, (await host.Post("/a", "list")).Status);
        Assert.Equal(200, (await host.Post("/upper", "list")).Status);
    }

    [Fact]
    public async Task T17_mapping_rejects_a_second_map()
    {
        using var fixture = new SqliteFixture();
        await using var host = Host(new NamedGatewayState(fixture));
        Refusal(() => host.App.MapDataGateway("a", "/again"), "a", "This gateway has already been mapped.");
        Refusal(() => host.App.MapGroup("/parent").MapDataGateway("a", "/again"), "a", "This gateway has already been mapped.");
        Assert.Equal(4, host.Endpoints.Length);
        await host.Start();
        Assert.Equal(200, (await host.Post("/a", "list")).Status);
    }

    [Fact]
    public async Task T18_mapping_rejects_an_unregistered_gateway()
    {
        foreach (var withOther in new[]
        {
            false,
            true
        })
        {
            using var fixture = new SqliteFixture();
            var state = new NamedGatewayState(fixture);
            await using var host = new NamedGatewayHost(builder =>
            {
                if (withOther)
                    NamedGatewayHost.Register(builder.Services, "b", state);
            });
            Refusal(() => host.App.MapDataGateway("a", "/a"), "a", "Register this gateway with AddDataGateway before mapping it.");
            Assert.Empty(host.Endpoints);
            Assert.Equal(0, state.Factories);
        }
    }

    [Fact]
    public void T19_registration_requires_a_catalog()
    {
        var services = new ServiceCollection();
        Refusal(() => services.AddDataGateway("a", gateway => gateway.AddEngine(new NamedGatewayEngine())), "a", "Configure a catalog with UseCatalog.");
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(GatewayRegistration));
    }

    [Fact]
    public void T20_registration_requires_an_engine()
    {
        using var fixture = new SqliteFixture();
        var services = new ServiceCollection();
        var state = new NamedGatewayState(fixture);
        Refusal(() => services.AddDataGateway("a", gateway => gateway.UseCatalog(state.Create)), "a", "Register at least one engine with AddEngine.");
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(GatewayRegistration));
        Assert.Equal(0, state.Factories);
    }

    [Fact]
    public async Task T21_registration_and_mapping_reject_blank_gateway_names()
    {
        await using var host = new NamedGatewayHost();
        foreach (var name in new string? []
        {
            null,
            "",
            " \t"
        })
        {
            var services = new ServiceCollection();
            foreach (Action call in new Action[]
            {
                () => services.AddDataGateway(name!, _ =>
                {
                }),
                () => host.App.MapDataGateway(name!, "/a")
            })
            {
                var error = Assert.Throws<InvalidOperationException>(call);
                Assert.Equal("Gateway name must contain a non-whitespace character.", error.Message);
                Assert.Null(error.InnerException);
            }
        }

        Assert.Equal("services", Assert.Throws<ArgumentNullException>(() => DataGatewayExtensions.AddDataGateway(null!, "a", _ =>
        {
        })).ParamName);
        Assert.Equal("endpoints", Assert.Throws<ArgumentNullException>(() => DataGatewayExtensions.MapDataGateway(null!, "a", "/a")).ParamName);
        Assert.Equal("configure", Assert.Throws<ArgumentNullException>(() => new ServiceCollection().AddDataGateway("a", null!)).ParamName);
    }

    [Fact]
    public void T22_engine_registration_rejects_blank_names()
    {
        foreach (var name in new string? []
        {
            null,
            "",
            " \t"
        })
        {
            foreach (var intrinsic in new[]
            {
                false,
                true
            })
            {
                Refusal(() => new ServiceCollection().AddDataGateway("a", gateway =>
                {
                    if (intrinsic)
                        gateway.AddEngine(new NamedGatewayEngine(name!));
                    else
                        gateway.AddEngine(new NamedGatewayEngine(), name!);
                }), "a", "Engine name must contain a non-whitespace character.");
            }
        }

        new ServiceCollection().AddDataGateway("a", gateway =>
        {
            Assert.Equal("engine", Assert.Throws<ArgumentNullException>(() => gateway.AddEngine(null!)).ParamName);
            Assert.Equal("engine", Assert.Throws<ArgumentNullException>(() => gateway.AddEngine(null!, "alias")).ParamName);
            Assert.Equal("factory", Assert.Throws<ArgumentNullException>(() => gateway.UseCatalog<SqliteConnection>(null!)).ParamName);
            gateway.UseCatalog<SqliteConnection>(_ => null!).AddEngine(new NamedGatewayEngine());
        });
    }

    [Fact]
    public async Task T23_invalid_prefix_does_not_claim_the_mapping()
    {
        foreach (var prefix in new string? []
        {
            "/broken/{",
            null
        })
        {
            using var fixture = new SqliteFixture();
            var state = new NamedGatewayState(fixture);
            await using var host = new NamedGatewayHost(builder => NamedGatewayHost.Register(builder.Services, "a", state));
            Refusal(() => host.App.MapDataGateway("a", prefix!), "a", "Prefix must be a valid route pattern.");
            Assert.Empty(host.Endpoints);
            host.App.MapDataGateway("a", "/good");
            await host.Start();
            Assert.Equal(200, (await host.Post("/good", "list")).Status);
        }

        foreach (var root in new[]
        {
            "",
            "/",
            "/nested"
        })
        {
            using var fixture = new SqliteFixture();
            await using var host = new NamedGatewayHost(builder => NamedGatewayHost.Register(builder.Services, "a", new NamedGatewayState(fixture)));
            if (root == "/nested")
                host.App.MapGroup("/parent").MapDataGateway("a", root);
            else
                host.App.MapDataGateway("a", root);
            await host.Start();
            Assert.Equal(200, (await host.Post(root == "/nested" ? "/parent/nested" : "", "list")).Status);
        }
    }

    [Fact]
    public async Task T24_startup_requires_active_detail_for_each_active_name()
    {
        foreach (var inactive in new[]
        {
            false,
            true
        })
        {
            using var fixture = new SqliteFixture();
            var state = new NamedGatewayState(fixture);
            state.ChangeDetail = detail => inactive ? WithSource(detail!, active: false) : null;
            var error = await StartupError(state);
            Assert.Equal("Gateway 'a'. Source 'synthetic_items'. $.detail Source 'synthetic_items'. Return an active detail for every active source in the list.", error.Message);
            Assert.Equal(new[] { "synthetic_items" }, state.Names);
        }

        using var healthyFixture = new SqliteFixture();
        var healthy = new NamedGatewayState(healthyFixture);
        await using var host = Host(healthy);
        await host.Start();
        Assert.DoesNotContain("synthetic_hidden", healthy.Names);
    }

    [Fact]
    public async Task T25_factory_failures_are_catalog_failures()
    {
        foreach (var throwing in new[]
        {
            false,
            true
        })
        {
            using var fixture = new SqliteFixture();
            var state = new NamedGatewayState(fixture);
            state.FactoryOverride = _ => throwing ? throw new SyntheticFault(Secret) : null;
            var startup = await StartupError(state);
            Assert.Contains("Cannot read the catalog.", startup.Message);
            Assert.Equal(1, state.Factories);
            state.FactoryOverride = null;
            await using var host = Host(state);
            await host.Start();
            state.FactoryOverride = _ => throwing ? throw new SyntheticFault(Secret) : null;
            foreach (var op in Operations)
            {
                var before = state.Factories;
                await Error(host, "/a", op, NamedGatewayHost.Body(op), "CatalogUnavailable", op == "list" ? "$" : "$.source", Unavailable, 503);
                Assert.Equal(before + 1, state.Factories);
            }
        }

        foreach (var detailTask in new[]
        {
            false,
            true
        })
        {
            using var fixture = new SqliteFixture();
            var state = new NamedGatewayState(fixture)
            {
                NullListTask = !detailTask,
                NullDetailTask = detailTask
            };
            var error = await StartupError(state);
            Assert.Contains("(NullReferenceException)", error.Message);
            Assert.Contains(detailTask ? "$.source" : "Gateway 'a'. $", error.Message);
        }

        using var goodFixture = new SqliteFixture();
        var good = new NamedGatewayState(goodFixture);
        await using var healthy = Host(good);
        await healthy.Start();
        good.ChangeDetail = _ => null;
        var baseline = good.Counts;
        Assert.Equal(404, (await healthy.Post("/a", "describe")).Status);
        Assert.Equal((baseline.Item1 + 1, baseline.Item2 + 1, baseline.Item3 + 1), good.Counts);
        good.NullListTask = true;
        await Error(healthy, "/a", "list", "{}", "CatalogUnavailable", "$", Unavailable, 503);
        good.NullListTask = false;
        good.NullDetailTask = true;
        await Error(healthy, "/a", "describe", NamedGatewayHost.Body("describe"), "CatalogUnavailable", "$.source", Unavailable, 503);
    }

    [Fact]
    public async Task T26_missing_detail_lists_only_active_names()
    {
        foreach (var inactive in new[]
        {
            false,
            true
        })
        {
            using var fixture = new SqliteFixture();
            var state = new NamedGatewayState(fixture);
            await using var host = Host(state);
            await host.Start();
            state.ChangeDetail = detail => inactive ? WithSource(detail!, active: false) : null;
            foreach (var op in new[]
            {
                "describe",
                "query"
            })
            {
                var unknown = await Error(host, "/a", op, NamedGatewayHost.Body(op), "UnknownSource", "$.source", "Choose an active source. Available sources: synthetic_items, synthetic_other.", 404);
                Assert.Equal(inactive ? 101 : (int? )null, unknown.Fields["SourceId"]);
                state.ChangeList = rows => rows.Select(row => row with { IsActive = false }).ToArray();
                await Error(host, "/a", op, NamedGatewayHost.Body(op), "UnknownSource", "$.source", "No source is active in the catalog. Ask the data steward to activate a source, then call list sources.", 404);
                state.ChangeList = _ => null!;
                await Error(host, "/a", op, NamedGatewayHost.Body(op), "InvalidCatalog", "$.sources", "Return a non-null source list.", 500);
                state.ChangeList = null;
                state.BeforeList = _ => throw new SyntheticFault(Secret);
                await Error(host, "/a", op, NamedGatewayHost.Body(op), "CatalogUnavailable", "$", Unavailable, 503);
                state.BeforeList = null;
            }
        }
    }

    [Fact]
    public async Task T27_catalog_cancellation_preserves_existing_wire_rules()
    {
        using var fixture = new SqliteFixture();
        var state = new NamedGatewayState(fixture);
        await using var host = Host(state);
        await host.Start();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await using (var scope = host.App.Services.CreateAsyncScope())
        {
            var adapter = host.App.Services.GetServices<GatewayRegistration>().Single().CreateCatalog(scope.ServiceProvider);
            var before = state.Counts;
            Assert.Equal("CatalogCancelled", (await adapter.ListAsync(cancelled.Token)).Failure!.Code);
            Assert.Equal("CatalogCancelled", (await adapter.TargetAsync("synthetic_items", cancelled.Token)).Failure!.Code);
            Assert.Equal(before, state.Counts);
        }

        foreach (var operation in Operations)
        {
            state.BeforeList = operation == "list" ? _ => throw new OperationCanceledException(Secret) : null;
            state.BeforeDetail = operation != "list" ? _ => throw new OperationCanceledException(Secret) : null;
            var cancellationLine = await GoldenError(host, "/a", operation, "CatalogCancelled/route", "CatalogCancelled");
            Assert.Equal("catalog", cancellationLine.Fields["Step"]);
            Assert.Equal(operation == "list" ? "$" : "$.source", cancellationLine.Fields["Path"]);
        }

        state.BeforeList = null;
        using var race = new CancellationTokenSource();
        state.BeforeDetail = null;
        state.ChangeDetail = detail =>
        {
            race.Cancel();
            return detail;
        };
        await using (var scope = host.App.Services.CreateAsyncScope())
        {
            var adapter = host.App.Services.GetServices<GatewayRegistration>().Single().CreateCatalog(scope.ServiceProvider);
            var result = await adapter.TargetAsync("synthetic_items", race.Token);
            Assert.Equal("CatalogCancelled", result.Failure!.Code);
            Assert.Null(result.Failure.DriverException);
        }

        state.ChangeDetail = null;
        using var abort = new CancellationTokenSource();
        state.BeforeDetail = token =>
        {
            Assert.True(token.CanBeCanceled);
            abort.Cancel();
            throw new IOException(Secret);
        };
        var count = host.Logs.Failures.Length;
        var output = await Invoke(host, "a", "describe", abort.Token);
        Assert.Empty(output);
        var line = Assert.Single(host.Logs.Failures.Skip(count));
        Assert.Equal("CatalogCancelled", line.Fields["Code"]);
        Assert.Equal(LogLevel.Warning, line.Level);
        Assert.True(state.Tokens.Last().IsCancellationRequested);
    }

    [Fact]
    public async Task T28_catalog_exceptions_are_sanitized_unavailable_failures()
    {
        using var fixture = new SqliteFixture();
        var state = new NamedGatewayState(fixture);
        await using var host = Host(state);
        await host.Start();
        foreach (var make in new Func<Exception>[]
        {
            () => new SyntheticFault(Secret + fixture.Connection, new Exception(Secret + fixture.Connection)),
            () => new TimeoutException(Secret + fixture.Connection),
            () => new ArgumentException(Secret + fixture.Connection),
            () => new FormatException(Secret + fixture.Connection),
            () => new InvalidCastException(Secret + fixture.Connection)
        })
            foreach (var op in Operations)
            {
                var error = make();
                state.BeforeList = op == "list" ? _ => throw error : null;
                state.BeforeDetail = op != "list" ? _ => throw error : null;
                var line = await Error(host, "/a", op, NamedGatewayHost.Body(op), "CatalogUnavailable", op == "list" ? "$" : "$.source", Unavailable, 503);
                Assert.Equal(error.GetType().Name, line.Fields["ExceptionType"]);
                Assert.Null(line.Fields["DriverMessage"]);
                Assert.Null(line.Fields["ExceptionText"]);
                Assert.DoesNotContain(Secret, JsonSerializer.Serialize(line));
                Assert.DoesNotContain(fixture.Connection, JsonSerializer.Serialize(line));
            }

        Assert.DoesNotContain(Secret, string.Join("\n", host.Logs.Events.Select(item => item.Text + item.Exception + string.Join(";", item.Fields.Select(field => field.Key + "=" + field.Value)))));
    }

    [Fact]
    public async Task T29_invalid_runtime_list_projects_internal_error()
    {
        using var fixture = new SqliteFixture();
        var state = new NamedGatewayState(fixture);
        await using var host = Host(state);
        await host.Start();
        foreach (var variant in new[]
        {
            0,
            1,
            4,
            5
        })
        {
            state.ChangeList = rows => InvalidList(rows, variant);
            var failure = CatalogValidator.ValidateList(InvalidList(ValidList(), variant))[0];
            await Error(host, "/a", "list", "{}", failure.Code, failure.Path, failure.Message, 500);
        }
    }

    [Fact]
    public async Task T30_invalid_runtime_detail_projects_internal_error()
    {
        using var fixture = new SqliteFixture();
        var state = new NamedGatewayState(fixture);
        var engine = new NamedGatewayEngine();
        await using var host = Host(state, engine: engine);
        await host.Start();
        var raw = await new NamedGatewayCatalog(new NamedGatewayState(fixture), Guid.Empty).QueryCatalogDetailAsync("synthetic_items", default);
        foreach (var variant in Enumerable.Range(0, 9))
            foreach (var op in new[]
            {
                "describe",
                "query"
            })
            {
                state.ChangeDetail = detail => InvalidDetail(detail!, variant);
                var failure = DetailFailure(InvalidDetail(raw!, variant));
                var line = await Error(host, "/a", op, NamedGatewayHost.Body(op), "InvalidCatalog", failure.Path, failure.Message, 500);
                Assert.Equal("synthetic_items", line.Fields["SourceName"]);
            }

        Assert.Empty(engine.Compiled);
        Assert.Empty(engine.Executed);
    }

    [Fact]
    public async Task T31_runtime_provider_change_never_falls_back_to_an_engine()
    {
        using var fixture = new SqliteFixture();
        var state = new NamedGatewayState(fixture);
        var engine = new NamedGatewayEngine();
        await using var host = Host(state, engine: engine);
        await host.Start();
        state.Provider = _ => "missing-engine";
        foreach (var op in new[]
        {
            "describe",
            "query"
        })
            await Error(host, "/a", op, NamedGatewayHost.Body(op), "InvalidCatalog", "$.detail.provider", "Source 'synthetic_items'. Register an engine named 'missing-engine' for this gateway.", 500);
        Assert.Empty(engine.Compiled);
        Assert.Empty(engine.Executed);
    }

    [Fact]
    public async Task T32_unexpected_handler_fault_is_answered_once()
    {
        using var fixture = new SqliteFixture();
        var state = new NamedGatewayState(fixture);
        var engine = new NamedGatewayEngine();
        await using var host = Host(state, engine: engine);
        await host.Start();
        engine.CompileFault = () => new SyntheticFault("Synthetic compile fault");
        var line = await GoldenError(host, "/a", "query", "UnexpectedError/route", "UnexpectedError");
        Assert.Equal("SyntheticFault", line.Fields["ExceptionType"]);
        Assert.Equal("synthetic_items", line.Fields["SourceName"]);
        Assert.Null(line.Fields["Sql"]);
        Assert.Contains("   at ", (string)line.Fields["ExceptionText"]!);
        engine.CompileFault = null;
        Assert.Equal(QueryGolden, (await host.Post("/a", "query")).Text);
        Assert.Single(host.Logs.Failures);
    }

    [Fact]
    public async Task T33_serialization_fault_uses_the_same_boundary()
    {
        using var fixture = new SqliteFixture();
        var state = new NamedGatewayState(fixture);
        var engine = new NamedGatewayEngine();
        var fault = new AnswerFault([typeof(QueryResponse)], () => new SyntheticFault("Synthetic serialization fault"));
        await using var host = new NamedGatewayHost(builder =>
        {
            NamedGatewayHost.Register(builder.Services, "a", state, engine);
            builder.Services.AddSingleton(new global::DataGateway.GatewayJson(fault));
        });
        host.App.MapDataGateway("a", "/a");
        await host.Start();
        fault.Arm();
        var line = await GoldenError(host, "/a", "query", "UnexpectedError/route", "UnexpectedError");
        Assert.Equal("synthetic_items", line.Fields["SourceName"]);
        Assert.NotNull(line.Fields["Sql"]);
        Assert.Equal(200, (await host.Post("/a", "query")).Status);
        var streamingEngine = new NamedGatewayEngine();
        var errorWrites = new NamedGatewayErrorWrites();
        await using var streaming = new NamedGatewayHost(builder =>
        {
            NamedGatewayHost.Register(builder.Services, "a", new NamedGatewayState(fixture), streamingEngine);
            builder.Services.AddSingleton(new global::DataGateway.GatewayJson(errorWrites));
        });
        streaming.App.MapDataGateway("a", "/a");
        await streaming.Start();
        streamingEngine.ChangeRows = _ => new QueryRows(Enumerable.Range(0, 500).Select(index => (IDictionary<string, object?>)new Dictionary<string, object?> { ["label"] = index == 499 ? new ExplodingValue() : new string ('x', 400) }).ToArray(), false);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/a/query")
        {
            Content = new StringContent("""{"source":"synthetic_items","query":{"select":["label"]}}""")
        };
        using var response = await streaming.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await Assert.ThrowsAnyAsync<IOException>(async () =>
        {
            await using var stream = await response.Content.ReadAsStreamAsync();
            await stream.CopyToAsync(Stream.Null);
        });
        await Until(() => streaming.Logs.Failures.Length == 1);
        var started = Assert.Single(streaming.Logs.Failures);
        Assert.Equal("response", started.Fields["Step"]);
        Assert.Equal("a", started.Scopes["Gateway"]);
        Assert.Equal("UnexpectedError", started.Fields["Code"]);
        Assert.Equal(0, errorWrites.Attempts);
        streamingEngine.ChangeRows = null;
        Assert.Equal(QueryGolden, (await streaming.Post("/a", "query")).Text);
        streamingEngine.CompileFault = () => new SyntheticFault("Synthetic observer control");
        await GoldenError(streaming, "/a", "query", "UnexpectedError/route", "UnexpectedError");
        Assert.Equal(1, errorWrites.Attempts);
    }

    [Fact]
    public async Task T34_body_read_refusals_keep_fixed_bodies()
    {
        foreach (var status in new[]
        {
            400,
            413
        })
        {
            using var fixture = new SqliteFixture();
            var state = new NamedGatewayState(fixture);
            await using var host = new NamedGatewayHost(builder =>
            {
                NamedGatewayHost.Register(builder.Services, "a", state);
                builder.Services.AddSingleton<IStartupFilter>(new BodyFault(new BadHttpRequestException(Secret, status)));
            });
            host.App.MapDataGateway("a", "/a");
            await host.Start();
            var baseline = state.Counts;
            foreach (var operation in Operations)
            {
                var line = await GoldenError(host, "/a", operation, status == 413 ? "TooLarge/query" : "Unreadable/query", status == 413 ? "StructuralLimitExceeded" : "InvalidJson", expectedLoggedRequest: null);
                Assert.Equal("BadHttpRequestException", line.Fields["ExceptionType"]);
                Assert.Null(line.Fields["Request"]);
            }

            Assert.Equal(baseline, state.Counts);
        }

        using var cancellationFixture = new SqliteFixture();
        var cancellationState = new NamedGatewayState(cancellationFixture);
        await using var healthy = Host(cancellationState);
        await healthy.Start();
        var counts = cancellationState.Counts;
        using var abort = new CancellationTokenSource();
        Assert.Empty(await Invoke(healthy, "a", "query", abort.Token, new NamedGatewayCancelStream(abort)));
        Assert.Empty(healthy.Logs.Failures);
        Assert.Equal(counts, cancellationState.Counts);
    }

    [Fact]
    public async Task T35_request_rules_and_error_goldens_match_example()
    {
        using var fixture = new SqliteFixture();
        using var settings = fixture.Settings();
        var state = new NamedGatewayState(fixture);
        await using var named = Host(state);
        await named.Start();
        await using var example = fixture.Host(new SqliteEngine());
        await example.Start();
        foreach (var operation in Operations)
            foreach (var body in new[]
            {
                "{",
                "[]",
                "{}",
                "{\"extra\":true}",
                "{\"source\":5}",
                "{\"source\":\"synthetic_items\",\"extra\":0}",
                "{\"source\":\"synthetic_items\",\"query\":5}"
            })
            {
                var before = state.Factories;
                var actual = await named.Post("/a", operation, body);
                using var response = await example.Client.PostAsync(NamedGatewayHost.PathOf(operation), new StringContent(body));
                Assert.Equal((int)response.StatusCode, actual.Status);
                Assert.Equal(response.Content.Headers.ContentType!.ToString(), actual.ContentType);
                Assert.Equal(Normalize(await response.Content.ReadAsStringAsync()), Normalize(actual.Text));
                Assert.Equal("application/json; charset=utf-8", actual.ContentType);
                if (operation == "list" && body == "{}")
                {
                    Assert.Equal(200, actual.Status);
                    Assert.Equal("""{"sources":[{"name":"synthetic_items","title":"Synthetic","description":"Invented SQLite proof data.","grain":"One item","rowCap":3},{"name":"synthetic_other","title":"Synthetic","description":"Invented SQLite proof data.","grain":"One item","rowCap":3}]}""", actual.Text);
                }
                else
                {
                    var(code, path, correction) = operation switch
                    {
                        "list" when body == "{" => ("InvalidJson", "$", "The request body is not valid JSON. Send an empty JSON object, {}: list sources takes no parameters."),
                        "list" => ("InvalidStructure", "$", "Send an empty JSON object, {}: list sources takes no parameters."),
                        "describe" when body == "{" => ("InvalidJson", "$", "The request body is not valid JSON. Send {\"source\": \"<name>\"} with a name from list sources."),
                        "describe" when body == "{\"source\":5}" => ("InvalidStructure", "$.source", "source must be a JSON string: the name of a source from list sources."),
                        "describe" => ("InvalidStructure", "$", "Send a JSON object with exactly one property, source: {\"source\": \"<name>\"} with a name from list sources."),
                        _ when body == "{" => ("InvalidJson", "$", "The request body is not valid JSON. Send {\"source\": \"<name>\", \"query\": {...}} with a name from list sources."),
                        _ when body.EndsWith("\"query\":5}", StringComparison.Ordinal) => ("InvalidJson", "$.query", "The query must be one JSON object whose only members are select, filters, group_by, aggregates, order_by and limit, each used once and with the type querySchema shows. Correct it against querySchema from describe source."),
                        _ => ("InvalidStructure", "$", "Send a JSON object with exactly two properties, source and query: {\"source\": \"<name>\", \"query\": {...}}. Take the name from list sources and the query vocabulary from describe source.")};
                    Assert.Equal(400, actual.Status);
                    var expected = JsonSerializer.Serialize(new { code, path, message = correction, incidentId = "INCIDENT" }, new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
                    Assert.Equal(expected, Normalize(actual.Text));
                }

                // A non-object query reaches query validation after the valid source envelope is parsed.
                Assert.Equal(before + (operation == "list" && body == "{}" || operation == "query" && body.EndsWith("\"query\":5}", StringComparison.Ordinal) ? 1 : 0), state.Factories);
            }

        await GoldenError(named, "/a", "query", "UnknownColumn/route", "UnknownColumn", """{"source":"synthetic_items","query":{"select":["missing"]}}""");
        await GoldenError(named, "/a", "query", "InvalidJson/query", "InvalidJson", "{");
        foreach (var(query, code, path, message)in new[]
        {
            ("{\"limit\":0}", "InvalidLimit", "$.query.limit", "limit must be 1 or more. Give a positive limit, or leave limit out."),
            ("{\"bogus\":1}", "InvalidJson", "$.query", "The query must be one JSON object whose only members are select, filters, group_by, aggregates, order_by and limit, each used once and with the type querySchema shows. Correct it against querySchema from describe source."),
            ("{\"filters\":[{\"col\":\"id\",\"op\":\"bogus\",\"value\":1}]}", "InvalidOperator", "$.query.filters[0].op", "The filter operator at path is unknown or not allowed for the column's data type. Use an operator that querySchema lists for this column.")
        })
        {
            var body = "{\"source\":\"synthetic_items\",\"query\":" + query + "}";
            var result = await named.Post("/a", "query", body);
            using var response = await example.Client.PostAsync("/query", new StringContent(body));
            Assert.Equal(400, result.Status);
            Assert.Equal(Normalize(await response.Content.ReadAsStringAsync()), Normalize(result.Text));
            Assert.Equal("application/json; charset=utf-8", result.ContentType);
            var expected = JsonSerializer.Serialize(new { code, path, message, incidentId = "INCIDENT" }, new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            Assert.Equal(expected, Normalize(result.Text));
        }
    }

    [Fact]
    public async Task T36_defaults_and_row_caps_reach_the_selected_runner()
    {
        using var fixture = new SqliteFixture();
        var state = new NamedGatewayState(fixture);
        var engine = new NamedGatewayEngine();
        await using var host = Host(state, engine: engine);
        await host.Start();
        foreach (var limit in new[]
        {
            "",
            ",\"limit\":3"
        })
        {
            var result = await host.Post("/a", "query", "{\"source\":\"synthetic_items\",\"query\":{\"select\":[\"id\"],\"order_by\":[{\"col\":\"id\",\"dir\":\"asc\"}]" + limit + "}}");
            Assert.Equal(200, result.Status);
            var body = JsonNode.Parse(result.Text)!;
            Assert.Equal(3, (int)body["rowCount"]!);
            Assert.True((bool)body["more"]!);
            Assert.Equal("The result set was capped at 3 rows.", (string? )body["hint"]);
        }

        var before = engine.Compiled.Count;
        Assert.Equal(400, (await host.Post("/a", "query", """{"source":"synthetic_items","query":{"limit":4}}""")).Status);
        Assert.Equal(before, engine.Compiled.Count);
        fixture.Change("UPDATE gateway_source SET row_cap=1500 WHERE name='synthetic_items'");
        var generous = await host.Post("/a", "query", """{"source":"synthetic_items","query":{"select":["id"],"limit":1001}}""");
        Assert.Equal(200, generous.Status);
        var generousBody = JsonNode.Parse(generous.Text)!;
        Assert.Equal(5, (int)generousBody["rowCount"]!);
        Assert.False((bool)generousBody["more"]!);
        Assert.Equal(QueryResponse.CompleteSet, (string? )generousBody["hint"]);
        Assert.All(engine.Executed, execution => Assert.Equal(30, execution.Timeout));
        var registration = host.App.Services.GetServices<GatewayRegistration>().Single();
        Assert.Equal(JsonQueryLimits.Default with { MaxLimit = null }, registration.Limits);
        before = engine.Compiled.Count;
        var filters = Enumerable.Range(0, registration.Limits.MaxFilterConditions + 1).Select(_ => new { col = "id", op = "eq", value = 1 }).ToArray();
        Assert.Equal(400, (await host.Post("/a", "query", JsonSerializer.Serialize(new { source = "synthetic_items", query = new { filters } }))).Status);
        Assert.Equal(before, engine.Compiled.Count);
    }

    [Fact]
    public async Task T37_named_gateway_can_coexist_with_example_routes_and_mcp()
    {
        using var fixture = new SqliteFixture();
        using var settings = fixture.Settings();
        var state = new NamedGatewayState(fixture);
        var cleanup = new NamedGatewayCleanup();
        await using var host = fixture.Host(new SqliteEngine(), configure: builder =>
        {
            builder.Services.AddSingleton(cleanup);
            builder.Services.AddScoped<NamedGatewayDependency>();
            NamedGatewayHost.Register(builder.Services, "named", state);
        });
        host.App.MapDataGateway("named", "/named");
        await host.Start();
        foreach (var prefix in new[]
        {
            "",
            "/named"
        })
            foreach (var operation in Operations)
            {
                using var response = await host.Client.PostAsync(prefix + NamedGatewayHost.PathOf(operation), new StringContent(NamedGatewayHost.Body(operation)));
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }

        var names = ((IEndpointRouteBuilder)host.App).DataSources.SelectMany(source => source.Endpoints).Select(endpoint => endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName).ToArray();
        foreach (var operation in Operations)
            Assert.Contains("DataGateway:DataGateway.Web:" + operation, names);
        await using (var mcp = await McpWire.Connect(host.Client, "2025-11-25"))
        {
            Assert.Equal(new[] { "describe_source", "list_sources", "query_source" }, (await mcp.ListToolsAsync()).Select(tool => tool.Name).Order());
            Assert.False((await McpWire.Call(mcp, "list_sources", null)).IsError == true);
        }

        foreach (var path in new[]
        {
            "/scalar/v1",
            "/openapi/v1.json"
        })
        {
            using var page = await host.Client.GetAsync(path);
            Assert.Equal(HttpStatusCode.NotFound, page.StatusCode);
        }

        foreach (var prefix in new[]
        {
            "",
            "/named"
        })
        {
            using var bad = await host.Client.PostAsync(prefix + "/query", new StringContent("{"));
            Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
            if (prefix == "")
            {
                using var answer = JsonDocument.Parse(await bad.Content.ReadAsByteArrayAsync());
                ErrorLogTests.Check(Assert.Single(host.Lines()), answer.RootElement, "query", Encoding.UTF8.GetBytes("{"), "InvalidJson");
            }
        }

        var lines = host.Lines();
        Assert.Equal(2, lines.Length);
        Assert.Equal("DataGateway.Web", lines[0].GetProperty("Gateway").GetString());
        Assert.Equal("named", lines[1].GetProperty("Gateway").GetString());
        foreach (var line in lines)
        {
            Assert.Equal("query", line.GetProperty("Operation").GetString());
            Assert.Equal("request", line.GetProperty("Step").GetString());
            Assert.Equal("InvalidJson", line.GetProperty("Code").GetString());
        }
    }

    [Fact]
    public async Task T38_registration_is_atomic_frozen_and_host_local()
    {
        using var fixture = new SqliteFixture();
        var first = new NamedGatewayState(fixture);
        var last = new NamedGatewayState(fixture);
        var services = new ServiceCollection();
        DataGatewayBuilder? captured = null;
        Assert.Throws<SyntheticFault>(() => services.AddDataGateway("a", gateway =>
        {
            captured = gateway;
            throw new SyntheticFault("configure");
        }));
        Refusal(() => captured!.AddEngine(new NamedGatewayEngine()), "a", "Registration is already complete.");
        services.AddDataGateway("a", gateway =>
        {
            captured = gateway;
            gateway.UseCatalog(first.Create).UseCatalog(last.Create).AddEngine(new NamedGatewayEngine());
        });
        Refusal(() => captured!.UseCatalog(first.Create), "a", "Registration is already complete.");
        Refusal(() => captured!.AddEngine(new NamedGatewayEngine(), "new"), "a", "Registration is already complete.");
        foreach (var _ in Enumerable.Range(0, 2))
        {
            await using var host = new NamedGatewayHost(builder =>
            {
                foreach (var descriptor in services)
                    builder.Services.Add(descriptor);
            });
            host.App.MapDataGateway("a", "/a");
            await host.Start();
            Assert.Equal(200, (await host.Post("/a", "list")).Status);
        }

        Assert.Equal(0, first.Factories);
        Assert.Equal(4, last.Factories);
        Assert.Equal(4, last.Instances.Select(pair => pair.Catalog).Distinct().Count());
        // A nested registration wins the second duplicate check without partially committing the outer registration.
        var nested = new ServiceCollection();
        Refusal(() => nested.AddDataGateway("a", gateway =>
        {
            gateway.UseCatalog(first.Create).AddEngine(new NamedGatewayEngine());
            NamedGatewayHost.Register(nested, "a", last);
        }), "a", "A gateway with this name is already registered.");
        Assert.Single(nested, descriptor => descriptor.ServiceType == typeof(GatewayRegistration));
    }

    [Fact]
    public void T39_public_surface_and_provider_constants_are_exact()
    {
        var extension = typeof(DataGatewayExtensions);
        var builder = typeof(DataGatewayBuilder);
        Assert.Equal("DataGateway", extension.Assembly.GetName().Name);
        Assert.Equal("DataGateway", extension.Namespace);
        Assert.Equal("DataGateway", builder.Namespace);
        Assert.Equal(new[] { builder, extension, typeof(GatewayLimits) }, extension.Assembly.GetExportedTypes().OrderBy(type => type.FullName, StringComparer.Ordinal));
        Assert.True(extension.IsAbstract && extension.IsSealed);
        Assert.True(builder.IsSealed && !builder.IsAbstract);
        var constructor = Assert.Single(builder.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance));
        Assert.True(constructor.IsAssembly);
        var methods = extension.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly);
        Assert.Equal(new[] { "AddDataGateway", "MapDataGateway", "MapDataGatewayApiPage", "TryRecordDataGatewayRefusal", "UseDataGatewayErrors", "UseDataGatewayErrors" }, methods.Select(method => method.Name).Order());
        Signature(methods.Single(method => method.Name == "AddDataGateway"), typeof(IServiceCollection), [typeof(IServiceCollection), typeof(string), typeof(Action<DataGatewayBuilder>)]);
        Signature(methods.Single(method => method.Name == "MapDataGateway"), typeof(RouteGroupBuilder), [typeof(IEndpointRouteBuilder), typeof(string), typeof(string)]);
        Signature(methods.Single(method => method.Name == "MapDataGatewayApiPage"), typeof(RouteGroupBuilder), [typeof(IEndpointRouteBuilder), typeof(string), typeof(string)]);
        Assert.All(methods, method => Assert.True(method.IsDefined(typeof(System.Runtime.CompilerServices.ExtensionAttribute))));
        var fluent = builder.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        Assert.Equal(6, fluent.Length);
        var mcp = fluent.Single(method => method.Name == "UseMcpServer");
        Signature(mcp, builder, [typeof(string), typeof(string)]);
        Assert.False(mcp.IsGenericMethod);
        Assert.Equal(new[] { "title", "about" }, mcp.GetParameters().Select(parameter => parameter.Name));
        var nullable = new NullabilityInfoContext();
        Assert.All(mcp.GetParameters(), parameter =>
            Assert.Equal(NullabilityState.Nullable, nullable.Create(parameter).ReadState));
        var boundary = methods.Single(method => method.Name == "UseDataGatewayErrors" && method.GetParameters().Length == 1);
        var hostBoundary = methods.Single(method => method.Name == "UseDataGatewayErrors" && method.GetParameters().Length == 2);
        Signature(hostBoundary, typeof(IApplicationBuilder), [typeof(IApplicationBuilder), typeof(string)]);
        Signature(boundary, typeof(IApplicationBuilder), [typeof(IApplicationBuilder)]);
        var record = methods.Single(method => method.Name == "TryRecordDataGatewayRefusal");
        Signature(record, typeof(bool), [typeof(HttpContext), typeof(CatalogFailure)]);
        var policy = fluent.Single(method => method.Name == "UseHostPolicy");
        Signature(policy, builder, [typeof(CatalogFailure), typeof(string), typeof(string)]);
        foreach (var (method, names, isStatic) in new[]
        {
            (boundary, new[] { "app" }, true),
            (hostBoundary, new[] { "app", "name" }, true),
            (record, new[] { "context", "failure" }, true),
            (policy, new[] { "failure", "description", "responseDescription" }, false)
        })
        {
            Assert.Equal(names, method.GetParameters().Select(parameter => parameter.Name));
            Assert.Equal(isStatic, method.IsStatic);
            Assert.Equal(isStatic, method.IsDefined(typeof(System.Runtime.CompilerServices.ExtensionAttribute)));
            Assert.Empty(method.GetGenericArguments());
            Assert.All(method.GetParameters(), parameter =>
                Assert.Equal(NullabilityState.NotNull, nullable.Create(parameter).ReadState));
        }
        var category = Assert.Single(extension.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly));
        Assert.Equal("FailureLogCategory", category.Name);
        Assert.Equal(typeof(string), category.FieldType);
        Assert.True(category.IsLiteral);
        Assert.False(category.IsInitOnly);
        Assert.Equal("DataGateway.Failure", category.GetRawConstantValue());
        var limitsMethod = fluent.Single(method => method.Name == "UseLimits");
        Signature(limitsMethod, builder, [typeof(GatewayLimits)]);
        Assert.False(limitsMethod.IsGenericMethod);
        Assert.False(limitsMethod.IsStatic);
        var limitsParameter = Assert.Single(limitsMethod.GetParameters());
        Assert.Equal("limits", limitsParameter.Name);
        Assert.Equal(NullabilityState.NotNull, nullable.Create(limitsParameter).ReadState);
        var limitsType = typeof(GatewayLimits);
        Assert.Equal("DataGateway", limitsType.Namespace);
        Assert.True(limitsType.IsClass);
        Assert.True(limitsType.IsSealed);
        Assert.False(limitsType.IsAbstract);
        Assert.Equal(typeof(object), limitsType.BaseType);
        Assert.Empty(Assert.Single(limitsType.GetConstructors()).GetParameters());
        var properties = limitsType.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
        Assert.Equal(new[] { "MaxFilterConditions", "MaxFilterDepth", "MaxInValues", "MaxJsonLength" },
            properties.Select(property => property.Name).Order(StringComparer.Ordinal));
        foreach (var member in properties)
        {
            Assert.Equal(typeof(int), member.PropertyType);
            Assert.True(member.GetMethod!.IsPublic);
            Assert.True(member.SetMethod!.IsPublic);
            Assert.Contains(typeof(System.Runtime.CompilerServices.IsExternalInit), member.SetMethod.ReturnParameter.GetRequiredCustomModifiers());
        }
        Assert.Empty(limitsType.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly));
        Assert.Equal(properties.SelectMany(member => new[] { member.GetMethod, member.SetMethod }).OrderBy(method => method!.Name, StringComparer.Ordinal),
            limitsType.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly).OrderBy(method => method.Name, StringComparer.Ordinal));
        var use = fluent.Single(method => method.Name == "UseCatalog");
        Assert.True(use.IsGenericMethodDefinition);
        var connection = Assert.Single(use.GetGenericArguments());
        Assert.Equal(typeof(DbConnection), Assert.Single(connection.GetGenericParameterConstraints()));
        Assert.Equal(GenericParameterAttributes.DefaultConstructorConstraint, connection.GenericParameterAttributes);
        Signature(use, builder, [typeof(Func<, >).MakeGenericType(typeof(IServiceProvider), typeof(CatalogService<>).MakeGenericType(connection))]);
        foreach (var add in fluent.Where(method => method.Name == "AddEngine"))
            Signature(add, builder, add.GetParameters().Length == 1 ? [typeof(IDatabaseEngine)] : [typeof(IDatabaseEngine), typeof(string)]);
        foreach (var(type, value, engine)in new (Type, string, IDatabaseEngine)[]
        {
            (typeof(SqliteEngine), "sqlite", new SqliteEngine()),
            (typeof(SqlServerEngine), "sqlserver", new SqlServerEngine()),
            (typeof(PostgreSqlEngine), "postgresql", new PostgreSqlEngine())
        })
        {
            var constant = type.GetField("ProviderName")!;
            Assert.True(constant.IsLiteral);
            Assert.Equal(value, constant.GetRawConstantValue());
            Assert.Equal(value, engine.ProviderName);
            Assert.Null(type.GetProperty("ProviderName"));
        }

        var property = Assert.Single(typeof(IDatabaseEngine).GetProperties());
        Assert.Equal("ProviderName", property.Name);
        Assert.Equal(typeof(string), property.PropertyType);
        Assert.True(property.GetMethod!.IsAbstract);
        Assert.Null(property.SetMethod);
        using var fixture = new SqliteFixture();
        var state = new NamedGatewayState(fixture);
        var services = new ServiceCollection();
        Assert.Same(services, services.AddDataGateway(" a ", gateway =>
        {
            Assert.Same(gateway, gateway.UseCatalog(sp => new NamedGatewayCatalog(state, Guid.Empty)));
            Assert.Same(gateway, gateway.UseMcpServer(null, null));
            Assert.Same(gateway, gateway.UseLimits(new GatewayLimits()));
            Assert.Same(gateway, gateway.AddEngine(new NamedGatewayEngine()));
            Assert.Same(gateway, gateway.AddEngine(new NamedGatewayEngine(), " variant "));
        }));
        Assert.Equal(" a ", Assert.IsType<GatewayRegistration>(services.Single(descriptor => descriptor.ServiceType == typeof(GatewayRegistration)).ImplementationInstance).Name);
    }

    [Fact]
    public async Task T40_startup_and_request_scopes_observe_cancellation_and_cleanup()
    {
        using var fixture = new SqliteFixture();
        var state = new NamedGatewayState(fixture);
        await using (var host = Host(state))
        {
            await host.Start();
            var startup = Assert.Single(state.Instances);
            Assert.Contains(startup.Scope, host.Cleanup.Disposed);
            Assert.Equal(200, (await host.Post("/a", "list")).Status);
            Assert.Equal(200, (await host.Post("/a", "describe")).Status);
            await Until(() => host.Cleanup.Disposed.Count == 3);
            Assert.Equal(3, state.Instances.Select(pair => pair.Scope).Distinct().Count());
            Assert.All(state.Instances, pair => Assert.Contains(pair.Scope, host.Cleanup.Disposed));
        }

        var gated = new NamedGatewayState(fixture);
        var entered = Gate();
        gated.BeforeList = async token =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        };
        await using (var blocked = Host(gated))
        {
            using var abort = new CancellationTokenSource();
            var ready = false;
            blocked.App.Lifetime.ApplicationStarted.Register(() => ready = true);
            var start = blocked.Start(abort.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            abort.Cancel();
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => start.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Contains("Gateway 'a'. $ The catalog read was cancelled.", error.Message);
            Assert.Null(error.InnerException);
            Assert.False(ready);
            Assert.True(gated.Tokens.Single().IsCancellationRequested);
            Assert.Contains(gated.Instances.Single().Scope, blocked.Cleanup.Disposed);
        }

        await using var healthy = Host(new NamedGatewayState(fixture));
        await healthy.Start();
        Assert.Equal(200, (await healthy.Post("/a", "list")).Status);
    }

    private const string Unavailable = "Cannot read the catalog. Check catalog availability and the configured catalog connection. Tell the user. Do not retry.";
    private static NamedGatewayHost Host(NamedGatewayState a, NamedGatewayState? b = null, NamedGatewayEngine? engine = null, NamedGatewayEngine? engineB = null)
    {
        var host = new NamedGatewayHost(builder =>
        {
            NamedGatewayHost.Register(builder.Services, "a", a, engine);
            if (b is not null)
                NamedGatewayHost.Register(builder.Services, "b", b, engineB);
        });
        host.App.MapDataGateway("a", "/a");
        if (b is not null)
            host.App.MapDataGateway("b", "/b");
        return host;
    }

    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task Until(Func<bool> done)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!done())
            await Task.Delay(10, timeout.Token);
    }

    private static string Normalize(string body) => Regex.Replace(body, "(?<=\"incidentId\":\")[0-9a-f]{32}(?=\")", "INCIDENT");
    private static async Task<NamedGatewayEvent> Error(NamedGatewayHost host, string prefix, string operation, string body, string code, string path, string message, int status, string? expectedLoggedRequest = DefaultLoggedRequest)
    {
        var before = host.Logs.Failures.Length;
        var response = await host.Post(prefix, operation, body);
        Assert.Equal(status, response.Status);
        Assert.Equal("application/json; charset=utf-8", response.ContentType);
        var projected = code is "InvalidCatalog" or "CatalogCancelled" ? "InvalidCatalog/route" : null;
        var expected = projected is null ? JsonSerializer.Serialize(new { code, path, message, incidentId = "INCIDENT" }, new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) : WireGolden.Cases[projected].Body;
        Assert.Equal(expected, Normalize(response.Text));
        var line = Assert.Single(host.Logs.Failures.Skip(before));
        CheckLine(line, response, prefix, operation, expectedLoggedRequest == DefaultLoggedRequest ? body : expectedLoggedRequest);
        Assert.Equal(code, line.Fields["Code"]);
        Assert.Equal(path, line.Fields["Path"]);
        Assert.Equal(message, line.Fields["Message"]);
        Assert.Equal(code is "InvalidStructure" or "InvalidJson" ? "request" : "catalog", line.Fields["Step"]);
        Assert.Null(line.Fields["DriverMessage"]);
        Assert.Null(line.Fields["ExceptionText"]);
        Assert.DoesNotContain(Secret, response.Text + JsonSerializer.Serialize(line));
        return line;
    }

    private static async Task<NamedGatewayEvent> GoldenError(NamedGatewayHost host, string prefix, string operation, string golden, string code, string? body = null, string? expectedLoggedRequest = DefaultLoggedRequest)
    {
        var before = host.Logs.Failures.Length;
        var response = await host.Post(prefix, operation, body);
        var expected = WireGolden.Cases[golden];
        Assert.Equal(expected.Status, response.Status);
        Assert.Equal("application/json; charset=utf-8", response.ContentType);
        Assert.Equal(expected.Body, Normalize(response.Text));
        var line = Assert.Single(host.Logs.Failures.Skip(before));
        CheckLine(line, response, prefix, operation, expectedLoggedRequest == DefaultLoggedRequest ? body ?? NamedGatewayHost.Body(operation) : expectedLoggedRequest);
        Assert.Equal(code, line.Fields["Code"]);
        return line;
    }

    private static void CheckLine(NamedGatewayEvent line, NamedGatewayReply response, string prefix, string operation, string? expectedLoggedRequest, string caller = "anonymous")
    {
        using var json = JsonDocument.Parse(response.Bytes);
        var root = json.RootElement;
        Assert.Equal(new[] { "code", "incidentId", "message", "path" }, root.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        var incident = root.GetProperty("incidentId").GetString();
        Assert.Matches("^[0-9a-f]{32}$", incident!);
        Assert.Equal(incident, line.Fields["IncidentId"]);
        Assert.Equal(operation, line.Fields["Operation"]);
        Assert.Equal(prefix[1..], line.Scopes["Gateway"]);
        Assert.Equal(root.GetProperty("code").GetString(), line.Fields["ReturnedCode"]);
        Assert.Equal(caller, line.Fields["Caller"]);
        Assert.Equal(expectedLoggedRequest, line.Fields["Request"]);
        Assert.True(line.Fields.ContainsKey("Step"));
        Assert.True(line.Fields.ContainsKey("Path"));
        Assert.True(line.Fields.ContainsKey("ExceptionType"));
        Assert.Null(line.Exception);
    }

    private static void Refusal(Action call, string name, string reason)
    {
        var error = Assert.Throws<InvalidOperationException>(call);
        Assert.Equal($"Gateway '{name}'. {reason}", error.Message);
        Assert.Null(error.InnerException);
    }

    private static async Task<InvalidOperationException> StartupError(NamedGatewayState state)
    {
        var engine = new NamedGatewayEngine();
        await using var host = Host(state, engine: engine);
        var ready = false;
        host.App.Lifetime.ApplicationStarted.Register(() => ready = true);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => host.Start());
        Assert.StartsWith("Gateway 'a'. ", error.Message);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain(Secret, error.ToString());
        Assert.False(ready);
        Assert.Empty(engine.Compiled);
        Assert.Empty(engine.Executed);
        return error;
    }

    private static string StartupText(string gateway, CatalogFailure failure) => $"Gateway '{gateway}'. {(failure.SourceName is null ? "" : $"Source '{failure.SourceName}'. ")}{failure.Path} {failure.Message}";
    private static IReadOnlyList<SourceListRow> ValidList() => [new("synthetic_items", "Synthetic", "Invented SQLite proof data.", "One item", true, 3), new("synthetic_other", "Synthetic", "Invented SQLite proof data.", "One item", true, 3), new("synthetic_hidden", "Synthetic", "Invented SQLite proof data.", "One item", false, 3)];
    private static IReadOnlyList<SourceListRow> InvalidList(IReadOnlyList<SourceListRow> rows, int variant) => variant switch
    {
        0 => null!,
        1 => [null!],
        2 => [rows[0] with
        {
            Name = " "
        }

        ],
        3 => [rows[0], rows[0] with
        {
            Name = "SYNTHETIC_ITEMS"
        }

        ],
        4 => [rows[0] with
        {
            RowCap = 0
        }

        ],
        _ => [rows[0] with
        {
            IsActive = false,
            RowCap = 0
        }

        ]
    };
    private static CatalogDetail InvalidDetail(CatalogDetail detail, int variant) => variant switch
    {
        0 => new(" ", detail.Rows),
        1 => new(detail.Provider, null!),
        2 => WithSource(detail, name: "wrong"),
        3 => WithSource(detail, connection: " "),
        4 => new(detail.Provider, new CatalogRows(detail.Rows.Source, [null!])),
        5 => WithSource(detail, cap: 0),
        6 => new(detail.Provider, new CatalogRows(detail.Rows.Source, [detail.Rows.Columns[0] with { ColumnName = "bad name" }])),
        7 => new(detail.Provider, new CatalogRows(detail.Rows.Source, [detail.Rows.Columns[0] with { DataType = "bogus" }])),
        _ => new(" ", WithSource(detail, active: false).Rows)};
    private static CatalogDetail WithSource(CatalogDetail detail, string? name = null, string? connection = null, int? cap = null, bool? active = null)
    {
        var row = detail.Rows.Source;
        return new(detail.Provider, new CatalogRows(new DataSourceRow(row.ObjectName, connection ?? row.ConnectionString) { QueryTimeoutSeconds = row.QueryTimeoutSeconds, Id = row.Id, Name = name ?? row.Name, Title = row.Title, Description = row.Description, Grain = row.Grain, IsActive = active ?? row.IsActive, RowCap = cap ?? row.RowCap }, detail.Rows.Columns));
    }

    private static CatalogFailure DetailFailure(CatalogDetail detail)
    {
        var validation = CatalogValidator.ValidateDetail("synthetic_items", detail);
        return validation.Count > 0 ? validation[0] : CatalogMapping.Map(detail.Rows).Failure!with
        {
            SourceName = "synthetic_items"
        };
    }

    private static void Signature(MethodInfo method, Type result, Type[] parameters)
    {
        Assert.Equal(result, method.ReturnType);
        Assert.Equal(parameters, method.GetParameters().Select(parameter => parameter.ParameterType));
        Assert.All(method.GetParameters(), parameter =>
        {
            Assert.False(parameter.IsOptional);
            Assert.False(parameter.HasDefaultValue);
        });
    }

    private static async Task<byte[]> Invoke(NamedGatewayHost host, string name, string operation, CancellationToken token, Stream? body = null)
    {
        var endpoint = host.Endpoints.Single(endpoint => endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName == $"DataGateway:{name}:{operation}");
        await using var scope = host.App.Services.CreateAsyncScope();
        using var response = new MemoryStream();
        var context = new DefaultHttpContext
        {
            RequestServices = scope.ServiceProvider,
            RequestAborted = token
        };
        context.SetEndpoint(endpoint);
        context.Request.Body = body ?? new MemoryStream(Encoding.UTF8.GetBytes(NamedGatewayHost.Body(operation)));
        context.Response.Body = response;
        try
        {
            await endpoint.RequestDelegate!(context);
            return response.ToArray();
        }
        finally
        {
            context.Request.Body.Dispose();
        }
    }
}
