using DataGateway.Data.Catalog;
using Microsoft.Data.Sqlite;
using System.Text;
using System.Text.Json;
using DataGateway.Data.Queries;
using DataGateway.Failures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using SqlKata.JsonQuery;
using Xunit;

namespace DataGateway.Web.Tests;

public sealed class NamedGatewayLimitsTests
{
    private static readonly string?[] Protocols = [null, "2025-11-25"];
    private const string OrderedTwo = "{\"select\":[\"id\"],\"order_by\":[{\"col\":\"id\",\"dir\":\"asc\"}],\"limit\":2}";
    private const string Leaf = "{\"col\":\"id\",\"op\":\"gte\",\"value\":0}";
    private const string Advice = " Use fewer filter conditions, fewer in values or fewer nested groups.";

    [Fact]
    public async Task T1_defaults_and_partial_assignment()
    {
        var defaults = new GatewayLimits();
        Values(defaults, 65536, 8, 50, 100);
        Assert.Equal(JsonQueryLimits.Default.MaxJsonLength, defaults.MaxJsonLength);
        Assert.Equal(JsonQueryLimits.Default.MaxFilterDepth, defaults.MaxFilterDepth);
        Assert.Equal(JsonQueryLimits.Default.MaxFilterConditions, defaults.MaxFilterConditions);
        Assert.Equal(JsonQueryLimits.Default.MaxInValues, defaults.MaxInValues);
        Values(new GatewayLimits { MaxJsonLength = 200 }, 200, 8, 50, 100);
        Values(new GatewayLimits { MaxFilterDepth = 0 }, 65536, 0, 50, 100);
        Values(new GatewayLimits { MaxFilterConditions = 2 }, 65536, 8, 2, 100);
        Values(new GatewayLimits { MaxInValues = 3 }, 65536, 8, 50, 3);
        await using var rig = new Rig([null, new GatewayLimits(), null]);
        await rig.Start();
        foreach (var name in new[] { "a", "b" })
        {
            Projection(rig.Registration(name), 65536, 8, 50, 100);
            await rig.Success(name, OrderedTwo, 2);
            Assert.All(rig.Engine(name).Executed, receipt => Assert.Equal(30, receipt.Timeout));
        }
    }

    [Fact]
    public async Task T2_last_assignment_is_deferred_and_frozen()
    {
        var first = new GatewayLimits { MaxJsonLength = 300, MaxFilterDepth = 2, MaxFilterConditions = 4, MaxInValues = 5 };
        var second = new GatewayLimits { MaxJsonLength = 400, MaxFilterDepth = 3, MaxFilterConditions = 6, MaxInValues = 7 };
        var invalid = new GatewayLimits { MaxJsonLength = 0, MaxFilterDepth = -1, MaxFilterConditions = 0, MaxInValues = 0 };
        foreach (var (earlier, final) in new[]
        {
            (invalid, first),
            (first, second),
            (first, new GatewayLimits { MaxInValues = 3 })
        })
        {
            DataGatewayBuilder? captured = null;
            var completed = false;
            await using var rig = new Rig([null, null, null], configure: (name, gateway) =>
            {
                if (name != "a")
                    return;
                captured = gateway;
                Assert.Same(gateway, gateway.UseLimits(earlier));
                Assert.Same(gateway, gateway.UseLimits(final));
                completed = true;
            });
            Assert.True(completed);
            Projection(rig.Registration("a"), final.MaxJsonLength, final.MaxFilterDepth, final.MaxFilterConditions, final.MaxInValues);
            Frozen(captured!, "a");
            await rig.Start();
            await rig.Success("a", OrderedTwo, 2);
            Assert.All(rig.Engine("a").Executed, receipt => Assert.Equal(30, receipt.Timeout));
            Frozen(captured!, "a");
            await rig.Success("a", OrderedTwo, 2);
            Projection(rig.Registration("a"), final.MaxJsonLength, final.MaxFilterDepth, final.MaxFilterConditions, final.MaxInValues);
            Assert.All(rig.Engine("a").Executed, receipt => Assert.Equal(30, receipt.Timeout));
        }
        using var fixture = new SqliteFixture();
        var state = new NamedGatewayState(fixture);
        foreach (var failure in new[] { "validation", "callback", "null" })
        {
            var services = new ServiceCollection();
            services.AddSingleton(new object());
            var before = services.ToArray();
            DataGatewayBuilder? captured = null;
            var completed = false;
            var callbackError = new InvalidOperationException("synthetic callback failure");
            var error = Record.Exception(() => services.AddDataGateway("a", gateway =>
            {
                captured = gateway;
                gateway.UseCatalog(state.Create).AddEngine(new NamedGatewayEngine());
                Assert.Same(gateway, gateway.UseLimits(first));
                if (failure == "callback")
                    throw callbackError;
                if (failure == "null")
                    gateway.UseLimits(null!);
                Assert.Same(gateway, gateway.UseLimits(invalid));
                completed = true;
            }));
            if (failure == "validation")
            {
                Assert.True(completed);
                Assert.Equal("Gateway 'a'. MaxJsonLength must be an integer from 1 to 2147483647.", Assert.IsType<InvalidOperationException>(error).Message);
            }
            else if (failure == "callback")
                Assert.Same(callbackError, error);
            else
                Assert.Equal("limits", Assert.IsType<ArgumentNullException>(error).ParamName);
            SameDescriptors(before, services);
            Frozen(captured!, "a");
        }
        Assert.Equal(0, state.Factories);
    }

    [Fact]
    public void T3_invalid_values_commit_nothing()
    {
        using var fixture = new SqliteFixture();
        var state = new NamedGatewayState(fixture);
        var cases = new List<(string Field, int Value)>();
        foreach (var field in new[] { "MaxJsonLength", "MaxFilterConditions", "MaxInValues" })
            foreach (var value in new[] { int.MinValue, -1, 0 })
                cases.Add((field, value));
        foreach (var value in new[] { int.MinValue, -1, 31, int.MaxValue })
            cases.Add(("MaxFilterDepth", value));
        foreach (var existing in new[] { false, true })
            foreach (var (field, value) in cases)
            {
                var services = new ServiceCollection();
                if (existing)
                    Register(services, "other", state, new NamedGatewayEngine(), new GatewayLimits());
                var before = services.ToArray();
                var completed = false;
                DataGatewayBuilder? captured = null;
                var error = Assert.Throws<InvalidOperationException>(() => services.AddDataGateway("a", gateway =>
                {
                    captured = gateway;
                    gateway.UseCatalog(state.Create).AddEngine(new NamedGatewayEngine());
                    Assert.Same(gateway, gateway.UseLimits(One(field, value)));
                    completed = true;
                }));
                Assert.True(completed);
                var range = field == "MaxFilterDepth" ? "0 to 30" : "1 to 2147483647";
                Assert.Equal($"Gateway 'a'. {field} must be an integer from {range}.", error.Message);
                SameDescriptors(before, services);
                Assert.Equal(0, state.Factories);
                Frozen(captured!, "a");
                Register(services, "a", state, new NamedGatewayEngine(), new GatewayLimits());
                Assert.Single(services.Where(descriptor => descriptor.ServiceType == typeof(GatewayRegistration))
                    .Select(descriptor => Assert.IsType<GatewayRegistration>(descriptor.ImplementationInstance)), item => item.Name == "a");
            }
        var spaced = new ServiceCollection();
        var refusal = Assert.Throws<InvalidOperationException>(() => Register(spaced, " named ", state, new NamedGatewayEngine(), new GatewayLimits { MaxInValues = 0 }));
        Assert.Equal("Gateway ' named '. MaxInValues must be an integer from 1 to 2147483647.", refusal.Message);
        Assert.Empty(spaced);
    }

    [Fact]
    public void T4_boundaries_are_accepted()
    {
        Assert.Equal(30, JsonQueryLimits.None.MaxFilterDepth);
        using var fixture = new SqliteFixture();
        var state = new NamedGatewayState(fixture);
        var cases = new List<(string Field, int Value)>();
        foreach (var field in new[] { "MaxJsonLength", "MaxFilterConditions", "MaxInValues" })
            foreach (var value in new[] { 1, int.MaxValue })
                cases.Add((field, value));
        foreach (var value in new[] { 0, 1, JsonQueryLimits.None.MaxFilterDepth })
            cases.Add(("MaxFilterDepth", value));
        foreach (var (field, value) in cases)
        {
            var services = new ServiceCollection();
            var selected = One(field, value);
            Register(services, "a", state, new NamedGatewayEngine(), selected);
            var registration = Assert.IsType<GatewayRegistration>(Assert.Single(services, descriptor => descriptor.ServiceType == typeof(GatewayRegistration)).ImplementationInstance);
            Projection(registration, selected.MaxJsonLength, selected.MaxFilterDepth, selected.MaxFilterConditions, selected.MaxInValues);
            if (field == "MaxJsonLength")
            {
                var log = new FailureLog(NullLoggerFactory.Instance, registration.Limits);
                Assert.Equal(value == 1 ? 1025 : int.MaxValue, log.RequestCharacters);
            }
        }
        Assert.Equal(0, state.Factories);
    }

    [Fact]
    public async Task T5_json_length_is_per_gateway()
    {
        await using var rig = new Rig([new GatewayLimits { MaxJsonLength = 200 }, new GatewayLimits { MaxJsonLength = 400 }, null]);
        await rig.Start();
        var bounds = new Dictionary<string, int> { ["a"] = 200, ["b"] = 400, ["d"] = 65536 };
        foreach (var name in new[] { "a", "b", "d", "d", "b", "a" })
        {
            var limit = bounds[name];
            var exact = QueryLength(limit);
            Assert.Equal(limit, exact.Length);
            Assert.True(Body(rig.Source(name), exact).Length > limit);
            using var valid = JsonDocument.Parse(exact);
            await rig.Success(name, exact, 0);
            var excess = QueryLength(limit + 1);
            Assert.Equal(limit + 1, excess.Length);
            using var excessJson = JsonDocument.Parse(excess);
            await rig.Refusal(name, excess, "$.query", LengthMessage(limit + 1, limit));
        }
        foreach (var length in new[] { 201, 401 })
            foreach (var name in new[] { "a", "b", "d" })
            {
                if (length > bounds[name])
                    await rig.Refusal(name, QueryLength(length), "$.query", LengthMessage(length, bounds[name]));
                else
                    await rig.Success(name, QueryLength(length), 0);
            }
    }

    [Fact]
    public async Task T6_filter_depth_is_per_gateway()
    {
        await using var rig = new Rig([new GatewayLimits { MaxFilterDepth = 0 }, new GatewayLimits { MaxFilterDepth = 2 }, null]);
        await rig.Start();
        foreach (var (name, limit) in new[] { ("a", 0), ("b", 2), ("d", 8) })
        {
            await rig.Success(name, QueryDepth(limit), 1);
            var path = "$.query.filters[0]" + string.Concat(Enumerable.Repeat(".and[0]", limit)) + ".and";
            if (name == "a")
                Assert.Equal("$.query.filters[0].and", path);
            if (name == "b")
                Assert.Equal("$.query.filters[0].and[0].and[0].and", path);
            if (name == "d")
                Assert.Equal("$.query.filters[0].and" + string.Concat(Enumerable.Repeat("[0].and", 8)), path);
            await rig.Refusal(name, QueryDepth(limit + 1), path, DepthMessage(limit));
        }
        foreach (var name in new[] { "b", "d" })
            await rig.Success(name, QueryDepth(1), 1);
        await rig.Refusal("a", QueryDepth(3), "$.query.filters[0].and", DepthMessage(0));
        await rig.Refusal("b", QueryDepth(3), "$.query.filters[0].and[0].and[0].and", DepthMessage(2));
        await rig.Success("d", QueryDepth(3), 1);
    }

    [Fact]
    public async Task T7_filter_conditions_are_per_gateway()
    {
        await using var rig = new Rig([new GatewayLimits { MaxFilterConditions = 2 }, new GatewayLimits { MaxFilterConditions = 4 }, null]);
        await rig.Start();
        foreach (var (name, limit) in new[] { ("a", 2), ("b", 4), ("d", 50) })
        {
            await rig.Success(name, Conditions(limit), 1);
            await rig.Refusal(name, Conditions(limit + 1), $"$.query.filters[{limit}]", ConditionsMessage(limit));
        }
        foreach (var count in new[] { 3, 5 })
            foreach (var (name, limit) in new[] { ("a", 2), ("b", 4), ("d", 50) })
            {
                if (count > limit)
                    await rig.Refusal(name, Conditions(count), $"$.query.filters[{limit}]", ConditionsMessage(limit));
                else
                    await rig.Success(name, Conditions(count), 1);
            }
        var grouped = "{\"select\":[\"id\"],\"filters\":[{\"and\":[" + Leaf + ",{\"or\":[" + Leaf + "," + Leaf + "]}]}]}";
        await rig.Refusal("a", grouped, "$.query.filters[0].and[1].or[1]", ConditionsMessage(2));
    }

    [Fact]
    public async Task T8_in_values_are_per_gateway()
    {
        await using var rig = new Rig([new GatewayLimits { MaxInValues = 3 }, new GatewayLimits { MaxInValues = 5 }, null]);
        await rig.Start();
        foreach (var op in new[] { "in", "not_in" })
        {
            foreach (var (name, limit) in new[] { ("a", 3), ("b", 5), ("d", 100) })
            {
                var exact = Membership(op, limit);
                using var json = JsonDocument.Parse(exact);
                Assert.Equal(limit, json.RootElement.GetProperty("filters")[0].GetProperty("value").GetArrayLength());
                await rig.Success(name, exact, op == "in" ? 0 : 1);
                var excess = Membership(op, limit + 1);
                using var excessJson = JsonDocument.Parse(excess);
                Assert.Equal(limit + 1, excessJson.RootElement.GetProperty("filters")[0].GetProperty("value").GetArrayLength());
                await rig.Refusal(name, excess, "$.query.filters[0].value", InMessage(limit + 1, limit));
            }
            foreach (var count in new[] { 4, 6 })
                foreach (var (name, limit) in new[] { ("a", 3), ("b", 5), ("d", 100) })
                {
                    if (count > limit)
                        await rig.Refusal(name, Membership(op, count), "$.query.filters[0].value", InMessage(count, limit));
                    else
                        await rig.Success(name, Membership(op, count), op == "in" ? 0 : 1);
                }
        }
        await Task.WhenAll(
            rig.Refusal("a", Membership("in", 4), "$.query.filters[0].value", InMessage(4, 3)),
            rig.Success("b", Membership("not_in", 4), 1),
            rig.Success("d", Membership("not_in", 4), 1));
    }

    [Fact]
    public async Task T9_failure_log_is_per_gateway()
    {
        await using var rig = new Rig([new GatewayLimits { MaxJsonLength = 200 }, new GatewayLimits { MaxJsonLength = 400 }, null]);
        await rig.Start();
        foreach (var (name, limit, bound) in new[] { ("a", 200, 1224), ("b", 400, 1424), ("d", 65536, 66560) })
        {
            Assert.Equal(bound, new FailureLog(NullLoggerFactory.Instance, rig.Registration(name).Limits).RequestCharacters);
            foreach (var extra in new[] { 0, 1 })
            {
                var overhead = Body(rig.Source(name), "{}").Length - 2;
                var query = QueryLength(bound + extra - overhead);
                Assert.Equal(bound + extra, Body(rig.Source(name), query).Length);
                await rig.Refusal(name, query, "$.query", LengthMessage(query.Length, limit), logBound: bound);
            }
            var common = QueryLength(70000);
            Assert.Equal(70000, common.Length);
            await rig.Refusal(name, common, "$.query", LengthMessage(70000, limit), logBound: bound);
        }
    }

    [Fact]
    public async Task T10_timeout_reaches_each_engine()
    {
        var shared = new NamedGatewayEngine();
        await using var rig = new Rig([null, null, null], shared);
        rig.Fixture("a").Change("UPDATE gateway_source SET query_timeout_seconds=7");
        rig.Fixture("b").Change("UPDATE gateway_source SET query_timeout_seconds=11");
        await rig.Start();
        foreach (var alternate in new[] { false, true })
        {
            foreach (var name in new[] { "a", "b", "d" })
                rig.State(name).Provider = _ => alternate ? "alternate" : "sqlite";
            var before = shared.Executed.Count;
            foreach (var name in new[] { "a", "b", "d" })
                await rig.Success(name, OrderedTwo, 2);
            Receipts(shared.Executed.Skip(before).ToArray(), rig);
        }
        var concurrentStart = shared.Executed.Count;
        await Task.WhenAll(rig.Success("a", OrderedTwo, 2), rig.Success("b", OrderedTwo, 2), rig.Success("d", OrderedTwo, 2));
        Receipts(shared.Executed.Skip(concurrentStart).ToArray(), rig);
        shared.ChangeRows = _ => throw new TimeoutException("synthetic command timeout");
        try
        {
            var before = shared.Executed.Count;
            foreach (var (name, seconds) in new[] { ("a", 7), ("b", 11), ("d", 30) })
            {
                var message = $"The query ran longer than the {seconds}-second command timeout and was stopped. Narrow the filters, aggregate, or request fewer rows.";
                await rig.Refusal(name, OrderedTwo, "$.query", message, code: "QueryTimeout", status: 504, step: "command");
            }
            Receipts(shared.Executed.Skip(before).ToArray(), rig);
        }
        finally
        {
            shared.ChangeRows = null;
        }
    }

    [Fact]
    public async Task T11_describe_and_row_caps_use_gateway_limits()
    {
        await using var rig = new Rig([
            new GatewayLimits { MaxJsonLength = 200, MaxFilterDepth = 0, MaxFilterConditions = 2, MaxInValues = 3 },
            new GatewayLimits { MaxJsonLength = 400, MaxFilterDepth = 2, MaxFilterConditions = 4, MaxInValues = 5 },
            null]);
        rig.Fixture("a").Change("UPDATE gateway_source SET row_cap=2");
        rig.Fixture("b").Change("UPDATE gateway_source SET row_cap=4");
        await rig.Start();
        foreach (var (name, cap, conditions, values, depth) in new[] { ("a", 2, 2, 3, 0), ("b", 4, 4, 5, 2), ("d", 3, 50, 100, 8) })
        {
            Assert.Null(rig.Registration(name).Limits.MaxLimit);
            await rig.Describe(name, root =>
            {
                var source = root.GetProperty("source");
                Assert.Equal(rig.Source(name), source.GetProperty("name").GetString());
                Assert.Equal(cap, source.GetProperty("rowCap").GetInt32());
                var schema = root.GetProperty("querySchema");
                var filters = schema.GetProperty("filters");
                Assert.Equal(conditions, filters.GetProperty("maxConditions").GetInt32());
                Assert.Equal(values, filters.GetProperty("maxInValues").GetInt32());
                if (depth == 0)
                    Assert.False(filters.TryGetProperty("maxGroupDepth", out _));
                else
                    Assert.Equal(depth, filters.GetProperty("maxGroupDepth").GetInt32());
                const string flat = "List of conditions {col, op, value} on columns whose canFilter is true; every one must hold.";
                const string groups = " Group conditions with {\"and\":[...]} or {\"or\":[...]}, each holding one or more items.";
                Assert.Equal(flat + (depth == 0 ? "" : groups), filters.GetProperty("rule").GetString());
                Assert.Equal(1, schema.GetProperty("limit").GetProperty("minimum").GetInt32());
                Assert.Equal(cap, schema.GetProperty("limit").GetProperty("maximum").GetInt32());
            });
            await rig.Success(name, "{\"select\":[\"id\"],\"order_by\":[{\"col\":\"id\",\"dir\":\"asc\"}]}", cap);
            if (name == "a")
                await rig.Refusal(name, OrderedTwo.Replace("\"limit\":2", "\"limit\":3", StringComparison.Ordinal), "$.query.limit",
                    "The query is over a configured limit: limit 3 exceeds the maximum of 2. Ask for no more rows than the maximum, or leave limit out to get the first rows, with more set to true when rows were left out.");
            else
                await rig.Success(name, OrderedTwo.Replace("\"limit\":2", "\"limit\":3", StringComparison.Ordinal), 3);
            Assert.Null(rig.Registration(name).Limits.MaxLimit);
        }
    }

    [Fact]
    public async Task T12_di_limits_do_not_override_public_gateway_limits()
    {
        foreach (var exampleFirst in new[] { true, false })
        {
            using var example = new SqliteFixture();
            example.Change("UPDATE gateway_source SET query_timeout_seconds=19; UPDATE gateway_source SET name='synthetic_example_only' WHERE name='synthetic_other'; UPDATE proof_items SET id=id+100;");
            var rootState = new NamedGatewayState(example);
            var engine = new NamedGatewayEngine();
            var runner = new QueryRunner(engine);
            var limits = JsonQueryLimits.Default with { MaxJsonLength = 1024, MaxFilterDepth = 0, MaxFilterConditions = 1, MaxInValues = 1, MaxLimit = 99 };
            await using var rig = new Rig([new GatewayLimits { MaxFilterConditions = 2 }, null, null],
                extra: services =>
                {
                    services.AddSingleton<CatalogService<SqliteConnection>>(example.Catalog());
                    services.AddSingleton(runner);
                    services.AddSingleton(limits);
                    services.AddSingleton<FailureLog>();
                    services.AddDataGateway("SyntheticExample", gateway => gateway
                        .UseCatalog(rootState.Create)
                        .AddEngine(engine)
                        .UseLimits(new GatewayLimits
                        {
                            MaxJsonLength = 1024,
                            MaxFilterDepth = 0,
                            MaxFilterConditions = 1,
                            MaxInValues = 1
                        })
                        );
                }, extraFirst: exampleFirst);
            rig.Fixture("a").Change("UPDATE gateway_source SET query_timeout_seconds=7");
            rig.Host.App.MapDataGateway("SyntheticExample", "");
            await rig.Start();
            foreach (var protocol in Protocols)
            {
                await using var client = await NamedGatewayMcpWire.Connect(rig.Host.Client, "/mcp", protocol, rig.Token);
                var exampleBody = Body("synthetic_example_only", Conditions(2));
                var exampleCompiled = engine.Compiled.Count;
                var exampleExecuted = engine.Executed.Count;
                var result = await McpWire.Call(client, "query_source", exampleBody, rig.Token);
                var expected = Error("StructuralLimitExceeded", "$.query.filters[1]", ConditionsMessage(1));
                var id = ToolError(result, expected);
                Failure(rig.Host, id, "SyntheticExample", "query_source", "StructuralLimitExceeded", "$.query.filters[1]", ConditionsMessage(1), "query", exampleBody, 2048);
                Assert.Equal(exampleCompiled, engine.Compiled.Count);
                Assert.Equal(exampleExecuted, engine.Executed.Count);
                await rig.Success("a", Conditions(2), 1);
                await rig.Success("d", Conditions(2), 1);
                await rig.Refusal("a", Conditions(3), "$.query.filters[2]", ConditionsMessage(2));
                await rig.Success("d", Conditions(3), 1);
                var before = engine.Executed.Count;
                var accepted = await McpWire.Call(client, "query_source", Body("synthetic_example_only", OrderedTwo), rig.Token);
                Assert.False(accepted.IsError == true);
                Assert.Null(accepted.StructuredContent);
                Assert.Single(accepted.Content);
                Witness(McpWire.Text(accepted), "synthetic_example_only", 101, 2);
                var receipt = Assert.Single(engine.Executed.Skip(before));
                Assert.Equal(example.Connection, receipt.Connection);
                Assert.Equal(19, receipt.Timeout);
            }
            Assert.Same(limits, rig.Host.App.Services.GetRequiredService<JsonQueryLimits>());
            Assert.Same(runner, rig.Host.App.Services.GetRequiredService<QueryRunner>());
            var resolved = rig.Host.App.Services.GetRequiredService<JsonQueryLimits>();
            Assert.Equal(1024, resolved.MaxJsonLength);
            Assert.Equal(0, resolved.MaxFilterDepth);
            Assert.Equal(1, resolved.MaxFilterConditions);
            Assert.Equal(1, resolved.MaxInValues);
            Assert.Equal(99, resolved.MaxLimit);
            Projection(rig.Registration("a"), 65536, 8, 2, 100);
            Projection(rig.Registration("d"), 65536, 8, 50, 100);
            Assert.NotSame(limits, rig.Registration("a").Limits);
            Assert.NotSame(limits, rig.Registration("d").Limits);
            Assert.All(rig.Engine("a").Executed, receipt => Assert.Equal(7, receipt.Timeout));
            Assert.All(rig.Engine("d").Executed, receipt => Assert.Equal(30, receipt.Timeout));
        }
    }

    private static void Values(GatewayLimits limits, int length, int depth, int conditions, int values)
    {
        Assert.Equal(length, limits.MaxJsonLength);
        Assert.Equal(depth, limits.MaxFilterDepth);
        Assert.Equal(conditions, limits.MaxFilterConditions);
        Assert.Equal(values, limits.MaxInValues);
    }

    private static GatewayLimits One(string field, int value) => field switch
    {
        "MaxJsonLength" => new GatewayLimits { MaxJsonLength = value },
        "MaxFilterDepth" => new GatewayLimits { MaxFilterDepth = value },
        "MaxFilterConditions" => new GatewayLimits { MaxFilterConditions = value },
        "MaxInValues" => new GatewayLimits { MaxInValues = value },
        _ => throw new ArgumentException("Unknown limit field.", nameof(field))
    };

    private static void Projection(GatewayRegistration registration, int length, int depth, int conditions, int values)
    {
        Assert.Equal(length, registration.Limits.MaxJsonLength);
        Assert.Equal(depth, registration.Limits.MaxFilterDepth);
        Assert.Equal(conditions, registration.Limits.MaxFilterConditions);
        Assert.Equal(values, registration.Limits.MaxInValues);
        Assert.Null(registration.Limits.MaxLimit);
    }

    private static void SameDescriptors(ServiceDescriptor[] before, IServiceCollection after)
    {
        Assert.Equal(before.Length, after.Count);
        for (var i = 0; i < before.Length; i++)
            Assert.Same(before[i], after[i]);
    }

    private static void Frozen(DataGatewayBuilder builder, string name)
    {
        foreach (var selection in new GatewayLimits?[] { new GatewayLimits(), null })
        {
            var error = Assert.Throws<InvalidOperationException>(() => builder.UseLimits(selection!));
            Assert.Equal($"Gateway '{name}'. Registration is already complete.", error.Message);
        }
    }

    private static void Register(IServiceCollection services, string name, NamedGatewayState state, NamedGatewayEngine engine, GatewayLimits limits)
    {
        services.AddDataGateway(name, gateway => gateway.UseCatalog(state.Create).AddEngine(engine).UseLimits(limits));
    }

    private static string Body(string source, string query) => "{\"source\":\"" + source + "\",\"query\":" + query + "}";

    private static string QueryLength(int length)
    {
        const string prefix = "{\"select\":[\"id\"],\"filters\":[{\"col\":\"label\",\"op\":\"eq\",\"value\":\"";
        const string suffix = "\"}]}";
        return prefix + new string('x', length - prefix.Length - suffix.Length) + suffix;
    }

    private static string QueryDepth(int depth)
    {
        var filter = Leaf;
        for (var i = 0; i < depth; i++)
            filter = "{\"and\":[" + filter + "]}";
        return "{\"select\":[\"id\"],\"filters\":[" + filter
            + "],\"order_by\":[{\"col\":\"id\",\"dir\":\"asc\"}],\"limit\":1}";
    }

    private static string Conditions(int count) => "{\"select\":[\"id\"],\"filters\":[" + string.Join(',', Enumerable.Repeat(Leaf, count))
        + "],\"order_by\":[{\"col\":\"id\",\"dir\":\"asc\"}],\"limit\":1}";

    private static string Membership(string op, int count) => "{\"select\":[\"id\"],\"filters\":[{\"col\":\"id\",\"op\":\"" + op
        + "\",\"value\":[" + string.Join(',', Enumerable.Repeat("0", count))
        + "]}],\"order_by\":[{\"col\":\"id\",\"dir\":\"asc\"}],\"limit\":1}";

    private static string LengthMessage(int count, int limit) => $"The query is over a configured limit: {count} characters exceed the payload limit of {limit}. Send a shorter query.";
    private static string ConditionsMessage(int limit) => $"The query is over a configured limit: {limit + 1} filter conditions exceed the limit of {limit}." + Advice;
    private static string DepthMessage(int limit) => $"The query is over a configured limit: filter group nesting depth {limit + 1} exceeds the limit of {limit}." + Advice;
    private static string InMessage(int count, int limit) => $"The query is over a configured limit: {count} values exceed the in limit of {limit}." + Advice;

    private static string Error(string code, string path, string message) => "{\"code\":\"" + code + "\",\"path\":\"" + path
        + "\",\"message\":" + JsonSerializer.Serialize(message) + ",\"incidentId\":\"INCIDENT\"}";

    private static string ToolError(CallToolResult result, string expected)
    {
        Assert.True(result.IsError);
        Assert.Single(result.Content);
        Assert.Null(result.StructuredContent);
        var normalized = NamedGatewayMcpWire.Normalize(Assert.IsType<TextContentBlock>(result.Content[0]).Text, out var incident);
        Assert.Equal(expected, normalized);
        return incident;
    }

    private static void Witness(string text, string source, int first, int count)
    {
        using var json = JsonDocument.Parse(text);
        var root = json.RootElement;
        Assert.Equal(source, root.GetProperty("source").GetString());
        var column = Assert.Single(root.GetProperty("columns").EnumerateArray());
        Assert.Equal("id", column.GetProperty("name").GetString());
        Assert.Equal("number", column.GetProperty("dataType").GetString());
        Assert.Equal(Enumerable.Range(first, count), root.GetProperty("rows").EnumerateArray().Select(row => row.GetProperty("id").GetInt32()));
        Assert.Equal(count, root.GetProperty("rowCount").GetInt32());
        Assert.Equal(count > 0, root.GetProperty("more").GetBoolean());
        var hint = count == 0 ? "Nothing found that matched your query." : $"The result set was capped at {count} {(count == 1 ? "row" : "rows")}.";
        Assert.Equal(hint, root.GetProperty("hint").GetString());
    }

    private static NamedGatewayEvent Failure(NamedGatewayHost host, string incident, string? gateway, string operation,
        string code, string path, string message, string step, string body, int bound)
    {
        var line = Assert.Single(host.Logs.Failures, item => Equals(item.Fields["IncidentId"], incident));
        if (gateway is null)
            Assert.False(line.Scopes.ContainsKey("Gateway"));
        else
            Assert.Equal(gateway, line.Scopes["Gateway"]);
        Assert.Equal(operation, line.Fields["Operation"]);
        Assert.Equal(code, line.Fields["Code"]);
        Assert.Equal(path, line.Fields["Path"]);
        Assert.Equal(message, line.Fields["Message"]);
        Assert.Equal(step, line.Fields["Step"]);
        Assert.Equal(Encoding.UTF8.GetByteCount(body), line.Fields["RequestBytes"]);
        Assert.Equal(body[..Math.Min(body.Length, bound)], line.Fields["Request"]);
        Assert.Equal(body.Length > bound, line.Fields["RequestCut"]);
        return line;
    }

    private static void Receipts((string Connection, DataGateway.Core.Queries.CompiledQuery Query, int Timeout)[] receipts, Rig rig)
    {
        Assert.Equal(9, receipts.Length);
        foreach (var (name, timeout) in new[] { ("a", 7), ("b", 11), ("d", 30) })
        {
            var selected = receipts.Where(receipt => receipt.Connection == rig.Fixture(name).Connection).ToArray();
            Assert.Equal(3, selected.Length);
            Assert.All(selected, receipt => Assert.Equal(timeout, receipt.Timeout));
        }
    }

    private sealed class Rig : IAsyncDisposable
    {
        private static readonly string[] Names = ["a", "b", "d"];
        private readonly SqliteFixture[] fixtures = [new(), new(), new()];
        private readonly NamedGatewayState[] states;
        private readonly NamedGatewayEngine[] engines;
        private readonly Dictionary<(string Name, int Protocol), McpClient> clients = new();
        private readonly CancellationTokenSource deadline = new(TimeSpan.FromSeconds(120));
        internal NamedGatewayHost Host { get; }
        internal CancellationToken Token => deadline.Token;

        internal Rig(GatewayLimits?[] selections, NamedGatewayEngine? shared = null,
            Action<string, DataGatewayBuilder>? configure = null, Action<IServiceCollection>? extra = null, bool extraFirst = false)
        {
            for (var i = 0; i < Names.Length; i++)
            {
                fixtures[i].Change("UPDATE gateway_source SET name=@v0 WHERE name='synthetic_other'; UPDATE proof_items SET id=id+@v1;", "synthetic_" + Names[i] + "_only", i * 10);
            }
            states = fixtures.Select(fixture => new NamedGatewayState(fixture)).ToArray();
            engines = Names.Select(_ => shared ?? new NamedGatewayEngine()).ToArray();
            Host = new NamedGatewayHost(builder =>
            {
                if (extraFirst)
                    extra?.Invoke(builder.Services);
                for (var i = 0; i < Names.Length; i++)
                {
                    var index = i;
                    builder.Services.AddDataGateway(Names[index], gateway =>
                    {
                        gateway.UseCatalog(states[index].Create).AddEngine(engines[index]).AddEngine(engines[index], "alternate");
                        if (selections[index] is { } selected)
                            Assert.Same(gateway, gateway.UseLimits(selected));
                        configure?.Invoke(Names[index], gateway);
                    });
                }
                if (!extraFirst)
                    extra?.Invoke(builder.Services);
            });
            foreach (var name in Names)
                Host.App.MapDataGateway(name, "/" + name);
        }

        internal string Source(string name) => "synthetic_" + name + "_only";
        internal SqliteFixture Fixture(string name) => fixtures[Array.IndexOf(Names, name)];
        internal NamedGatewayState State(string name) => states[Array.IndexOf(Names, name)];
        internal NamedGatewayEngine Engine(string name) => engines[Array.IndexOf(Names, name)];
        internal GatewayRegistration Registration(string name) => Assert.Single(Host.App.Services.GetServices<GatewayRegistration>(), item => item.Name == name);

        internal async Task Start()
        {
            await Host.Start(Token);
            foreach (var name in Names)
                for (var i = 0; i < Protocols.Length; i++)
                    clients.Add((name, i), await NamedGatewayMcpWire.Connect(Host.Client, "/" + name + "/mcp", Protocols[i], Token));
        }

        internal async Task Success(string name, string query, int count)
        {
            var body = Body(Source(name), query);
            var engine = Engine(name);
            var connection = Fixture(name).Connection;
            var before = engine.Executed.Count(receipt => receipt.Connection == connection);
            var route = await Host.Post("/" + name, "query", body);
            Assert.Equal(200, route.Status);
            Assert.StartsWith("application/json", route.ContentType);
            Witness(route.Text, Source(name), Array.IndexOf(Names, name) * 10 + 1, count);
            Assert.Equal(before + 1, engine.Executed.Count(receipt => receipt.Connection == connection));
            for (var i = 0; i < Protocols.Length; i++)
            {
                before = engine.Executed.Count(receipt => receipt.Connection == connection);
                var result = await McpWire.Call(clients[(name, i)], "query_source", body, Token);
                Assert.False(result.IsError == true);
                Assert.Null(result.StructuredContent);
                Assert.Single(result.Content);
                var text = McpWire.Text(result);
                Witness(text, Source(name), Array.IndexOf(Names, name) * 10 + 1, count);
                Assert.Equal(route.Text, text);
                Assert.Equal(before + 1, engine.Executed.Count(receipt => receipt.Connection == connection));
            }
        }

        internal async Task Refusal(string name, string query, string path, string message, string code = "StructuralLimitExceeded",
            int status = 400, string step = "query", int? logBound = null)
        {
            var body = Body(Source(name), query);
            var expected = Error(code, path, message);
            var engine = Engine(name);
            var bound = logBound ?? (int)Math.Min(Registration(name).Limits.MaxJsonLength + 1024L, int.MaxValue);
            var compiled = engine.Compiled.Count;
            var executed = engine.Executed.Count;
            var route = await Host.Post("/" + name, "query", body);
            Assert.Equal(status, route.Status);
            Assert.StartsWith("application/json", route.ContentType);
            Assert.Equal(expected, NamedGatewayMcpWire.Normalize(route.Text, out var incident));
            var line = Failure(Host, incident, name, "query", code, path, message, step, body, bound);
            if (step == "command")
                Assert.Equal(Source(name), line.Fields["SourceName"]);
            Counts(engine, compiled, executed, step);
            for (var i = 0; i < Protocols.Length; i++)
            {
                compiled = engine.Compiled.Count;
                executed = engine.Executed.Count;
                var result = await McpWire.Call(clients[(name, i)], "query_source", body, Token);
                var toolIncident = ToolError(result, expected);
                Assert.Equal(NamedGatewayMcpWire.Normalize(route.Text, out _), NamedGatewayMcpWire.Normalize(McpWire.Text(result), out _));
                line = Failure(Host, toolIncident, name, "query_source", code, path, message, step, body, bound);
                if (step == "command")
                    Assert.Equal(Source(name), line.Fields["SourceName"]);
                Counts(engine, compiled, executed, step);
            }
        }

        private static void Counts(NamedGatewayEngine engine, int compiled, int executed, string step)
        {
            var increment = step == "command" ? 1 : 0;
            Assert.Equal(compiled + increment, engine.Compiled.Count);
            Assert.Equal(executed + increment, engine.Executed.Count);
        }

        internal async Task Describe(string name, Action<JsonElement> assertion)
        {
            var body = "{\"source\":\"" + Source(name) + "\"}";
            var route = await Host.Post("/" + name, "describe", body);
            Assert.Equal(200, route.Status);
            Assert.StartsWith("application/json", route.ContentType);
            using var json = JsonDocument.Parse(route.Text);
            assertion(json.RootElement);
            for (var i = 0; i < Protocols.Length; i++)
            {
                var result = await McpWire.Call(clients[(name, i)], "describe_source", body, Token);
                Assert.False(result.IsError == true);
                Assert.Null(result.StructuredContent);
                Assert.Single(result.Content);
                using var tool = JsonDocument.Parse(McpWire.Text(result));
                assertion(tool.RootElement);
                Assert.Equal(route.Text, McpWire.Text(result));
            }
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var client in clients.Values)
                await client.DisposeAsync();
            await Host.DisposeAsync();
            deadline.Dispose();
            foreach (var fixture in fixtures)
                fixture.Dispose();
        }
    }
}
